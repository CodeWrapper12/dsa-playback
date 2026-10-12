import * as path from "path";
import * as vscode from "vscode";
import { CostDecorations } from "./decorations";
import { EngineCancelledError, EngineNotFoundError, listEntryPoints, measure } from "./engine";
import { EntryPoint, InputSpec, JobRequest, JobResponse, StepTrace } from "./types";

let decorations: CostDecorations;

/**
 * What the integration tests can observe. Decorations and webviews are not readable
 * through VS Code's API, so without this the only way to check the extension really works
 * is to look at it — which is how criterion 1 stayed unverified for three milestones.
 */
export interface TestApi {
  lastResponse(): JobResponse | undefined;
  lastSteps(): StepTrace | undefined;
  lastError(): string | undefined;
  linesDecoratedIn(uri: vscode.Uri): number;
  sendPanelMessage(message: unknown): Promise<void>;
}

const observed: { response?: JobResponse; steps?: StepTrace; error?: string } = {};

export function activate(context: vscode.ExtensionContext): TestApi {
  decorations = new CostDecorations();

  context.subscriptions.push(
    decorations,
    vscode.commands.registerCommand("dsaViz.measure", () => start(context, false)),
    vscode.commands.registerCommand("dsaViz.compare", () => start(context, true)),
    vscode.commands.registerCommand("dsaViz.clearAnnotations", () => decorations.clear()));

  return {
    lastResponse: () => observed.response,
    lastSteps: () => observed.steps,
    lastError: () => observed.error,
    linesDecoratedIn: uri => decorations.linesDecoratedIn(uri),
    sendPanelMessage: async message => { await ReportPanel.deliver(message); }
  };
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
  observed.error = message;
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

interface StepsMessage {
  type: "steps";
  size: number;
}

interface StepAtMessage {
  type: "stepAt";
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

  /** Routes a message as if the webview had sent it. For integration tests. */
  static async deliver(message: unknown) {
    await ReportPanel.current?.onMessage(message as RerunMessage | RevealMessage | StepsMessage | StepAtMessage);
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

  private async onMessage(message: RerunMessage | RevealMessage | StepsMessage | StepAtMessage) {
    if (message.type === "steps") { await this.recordSteps(message.size); return; }
    if (message.type === "stepAt") { await decorations.showStep(this.request.file, message.line); return; }

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
      observed.response = response;

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

  /**
   * Records a step-through on request rather than with every measurement: it is a second
   * build and a second run, and most measurements never open the step view.
   */
  private async recordSteps(size: number) {
    this.panel.webview.postMessage({ type: "stepsLoading" });
    try {
      const response = await measure({
        file: this.request.file,
        entry: this.request.entry,
        inputs: this.request.inputs,
        stepSize: size
      });
      this.panel.webview.postMessage({ type: "steps", trace: response.steps, errors: response.errors });
      observed.steps = response.steps ?? undefined;
    } catch (error) {
      const message = error instanceof Error ? error.message : String(error);
      this.panel.webview.postMessage({ type: "stepsError", message });
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
    decorations.clearStep();
    ReportPanel.current = undefined;
    this.panel.dispose();
    while (this.disposables.length) this.disposables.pop()?.dispose();
  }
}
