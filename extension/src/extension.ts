import * as path from "path";
import * as vscode from "vscode";
import { CostDecorations } from "./decorations";
import { EngineCancelledError, EngineNotFoundError, listEntryPoints, measure } from "./engine";
import { EntryPoint, InputSpec, JobRequest, JobResponse } from "./types";

let decorations: CostDecorations;

export function activate(context: vscode.ExtensionContext) {
  decorations = new CostDecorations();

  context.subscriptions.push(
    decorations,
    vscode.commands.registerCommand("dsaViz.measure", () => start(context, false)),
    vscode.commands.registerCommand("dsaViz.compare", () => start(context, true)),
    vscode.commands.registerCommand("dsaViz.clearAnnotations", () => decorations.clear()));
}

export function deactivate() {}

async function start(context: vscode.ExtensionContext, withComparison: boolean) {
  const editor = vscode.window.activeTextEditor;
  if (!editor || !editor.document.fileName.endsWith(".cs")) {
    vscode.window.showWarningMessage("Open the C# file you want to measure first.");
    return;
  }

  await editor.document.save();
  const file = editor.document.fileName;

  try {
    const entry = await pickEntryPoint(file, "Which method should be measured?");
    if (!entry) return;

    let compareFile: string | undefined;
    let compareEntry: string | undefined;

    if (withComparison) {
      const picked = await vscode.window.showOpenDialog({
        canSelectMany: false,
        filters: { "C# files": ["cs"] },
        openLabel: "Compare against",
        title: "Pick the second solution"
      });
      if (!picked?.length) return;

      compareFile = picked[0].fsPath;
      compareEntry = (await pickEntryPoint(compareFile, "Which method in the second file?"))?.qualified;
      if (!compareEntry) return;
    }

    // An empty setting means "let the engine choose", which is what lets it fall back
    // to small sizes for an expensive algorithm. Sending the default list explicitly
    // used to switch that fallback off and leave exponential solutions unmeasured.
    const configuredSizes = vscode.workspace.getConfiguration("dsaViz").get<number[]>("sizes") ?? [];

    const request: JobRequest = {
      file,
      entry: entry.qualified,
      sizes: configuredSizes.length > 0 ? configuredSizes : undefined,
      budget: vscode.workspace.getConfiguration("dsaViz").get<number>("budget"),
      compareFile,
      compareEntry
    };

    ReportPanel.show(context, request);
  } catch (error) {
    reportError(error);
  }
}

async function pickEntryPoint(file: string, title: string): Promise<EntryPoint | undefined> {
  const entries = await listEntryPoints(file);

  if (entries.length === 0) {
    vscode.window.showWarningMessage(`${path.basename(file)} has no public methods to measure.`);
    return undefined;
  }

  // One method is the common case for a solution file, and stopping to ask about it
  // would be friction for no decision.
  if (entries.length === 1 && entries[0].measurable) return entries[0];

  const picked = await vscode.window.showQuickPick(
    entries.map(entry => ({
      label: entry.measurable ? entry.name : `$(warning) ${entry.name}`,
      description: entry.signature,
      detail: entry.reason ?? undefined,
      entry
    })),
    { title, matchOnDescription: true });

  if (picked && !picked.entry.measurable) {
    vscode.window.showWarningMessage(`${picked.entry.name} cannot be measured: ${picked.entry.reason}`);
    return undefined;
  }

  return picked?.entry;
}

function reportError(error: unknown) {
  if (error instanceof EngineCancelledError) return;
  const message = error instanceof Error ? error.message : String(error);
  if (error instanceof EngineNotFoundError) {
    vscode.window.showErrorMessage(message, "Open Settings").then(choice => {
      if (choice === "Open Settings") {
        vscode.commands.executeCommand("workbench.action.openSettings", "dsaViz.enginePath");
      }
    });
    return;
  }
  vscode.window.showErrorMessage(`DSA Visualizer: ${message}`);
}

interface RerunMessage {
  type: "rerun";
  inputs?: InputSpec[];
  sizes?: number[];
}

interface RevealMessage {
  type: "reveal";
  line: number;
}

class ReportPanel {
  private static current: ReportPanel | undefined;

  private readonly disposables: vscode.Disposable[] = [];
  private inFlight: AbortController | undefined;

  private constructor(
    private readonly panel: vscode.WebviewPanel,
    private readonly context: vscode.ExtensionContext,
    private request: JobRequest) {

    this.panel.webview.html = this.render();

    this.panel.webview.onDidReceiveMessage(
      message => this.onMessage(message), null, this.disposables);

    this.panel.onDidDispose(() => this.dispose(), null, this.disposables);
  }

  static show(context: vscode.ExtensionContext, request: JobRequest) {
    const column = vscode.ViewColumn.Beside;

    if (ReportPanel.current) {
      ReportPanel.current.request = request;
      ReportPanel.current.panel.reveal(column);
      void ReportPanel.current.run();
      return;
    }

    const panel = vscode.window.createWebviewPanel(
      "dsaVizReport", "DSA Visualizer", column,
      {
        enableScripts: true,
        retainContextWhenHidden: true,
        localResourceRoots: [vscode.Uri.joinPath(context.extensionUri, "media")]
      });

    ReportPanel.current = new ReportPanel(panel, context, request);
    void ReportPanel.current.run();
  }

  private async onMessage(message: RerunMessage | RevealMessage) {
    if (message.type === "reveal") {
      // The panel ranks the costliest statements; clicking one lands the cursor on it,
      // since the annotated source is the thing the user actually works in.
      const document = await vscode.workspace.openTextDocument(this.request.file);
      const editor = await vscode.window.showTextDocument(document, { preview: false });
      const line = Math.max(0, Math.min(message.line - 1, document.lineCount - 1));
      const range = document.lineAt(line).range;
      editor.selection = new vscode.Selection(range.start, range.start);
      editor.revealRange(range, vscode.TextEditorRevealType.InCenterIfOutsideViewport);
      return;
    }

    // The panel sends back the engine's own resolved inputs with the user's edits
    // applied, so this side never has to reproduce the engine's inference rules — the
    // duplication that previously let the two drift apart.
    this.request = {
      ...this.request,
      inputs: message.inputs ?? this.request.inputs,
      sizes: message.sizes ?? this.request.sizes
    };
    await this.run();
  }

  private async run() {
    // Re-measuring while a measurement is still running replaces it rather than
    // queueing behind it.
    this.inFlight?.abort();
    const controller = new AbortController();
    this.inFlight = controller;

    this.panel.webview.postMessage({ type: "loading" });

    try {
      const request: JobRequest = { ...this.request };
      const response: JobResponse = await measure(request, controller.signal);
      if (controller.signal.aborted) return;

      this.panel.title = response.label ? `DSA: ${response.label}` : "DSA Visualizer";
      this.panel.webview.postMessage({ type: "result", response, request });

      // The costs belong on the user's own file, not on a copy of it inside a panel.
      const detail = response.primary?.detail;
      if (detail && response.primary?.status === "Completed") {
        const points = response.primary.points;
        const measuredAt = points.length ? points[points.length - 1].size : null;
        await decorations.apply(request.file, detail, measuredAt, response.primary.derivation);
      } else {
        decorations.clear();
      }
    } catch (error) {
      if (error instanceof EngineCancelledError) return;
      const message = error instanceof Error ? error.message : String(error);
      this.panel.webview.postMessage({ type: "error", message });
      reportError(error);
    } finally {
      if (this.inFlight === controller) this.inFlight = undefined;
    }
  }

  private render(): string {
    const webview = this.panel.webview;
    const media = (name: string) =>
      webview.asWebviewUri(vscode.Uri.joinPath(this.context.extensionUri, "media", name));

    const nonce = Array.from({ length: 32 },
      () => "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789"[
        Math.floor(Math.random() * 62)]).join("");

    return `<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8">
<meta http-equiv="Content-Security-Policy"
      content="default-src 'none'; style-src ${webview.cspSource}; script-src 'nonce-${nonce}';">
<meta name="viewport" content="width=device-width, initial-scale=1.0">
<link href="${media("styles.css")}" rel="stylesheet">
<title>DSA Visualizer</title>
</head>
<body>
<div id="app"><p class="placeholder">Measuring…</p></div>
<script nonce="${nonce}" src="${media("main.js")}"></script>
</body>
</html>`;
  }

  private dispose() {
    this.inFlight?.abort();
    ReportPanel.current = undefined;
    this.panel.dispose();
    while (this.disposables.length) this.disposables.pop()?.dispose();
  }
}
