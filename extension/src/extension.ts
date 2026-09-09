import * as path from "path";
import * as vscode from "vscode";
import { EngineNotFoundError, listEntryPoints, measure } from "./engine";
import { EntryPoint, GenerationStrategy, JobRequest, JobResponse } from "./types";

export function activate(context: vscode.ExtensionContext) {
  context.subscriptions.push(
    vscode.commands.registerCommand("dsaViz.measure", () => start(context, false)),
    vscode.commands.registerCommand("dsaViz.compare", () => start(context, true)));
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
      compareEntry = (await pickEntryPoint(compareFile, "Which method in the second file?"))?.name;
      if (!compareEntry) return;
    }

    const request: JobRequest = {
      file,
      entry: entry.name,
      sizes: vscode.workspace.getConfiguration("dsaViz").get<number[]>("sizes"),
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
    vscode.window.showWarningMessage(
      `${path.basename(file)} has no public static methods to measure.`);
    return undefined;
  }

  // One measurable candidate is the common case for a solution file, and stopping to
  // ask about it would be friction for no decision.
  const measurable = entries.filter(e => e.measurable);
  if (measurable.length === 1 && entries.length === 1) return measurable[0];

  const picked = await vscode.window.showQuickPick(
    entries.map(entry => ({
      label: entry.measurable ? entry.name : `$(warning) ${entry.name}`,
      description: entry.signature,
      detail: entry.measurable
        ? undefined
        : "Takes a parameter type the engine cannot generate: " +
          entry.parameters.filter(p => !p.supported).map(p => p.type).join(", "),
      entry
    })),
    { title, matchOnDescription: true });

  if (picked && !picked.entry.measurable) {
    vscode.window.showWarningMessage(
      `${picked.entry.name} cannot be measured: ${picked.detail}`);
    return undefined;
  }

  return picked?.entry;
}

function reportError(error: unknown) {
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

class ReportPanel {
  private static current: ReportPanel | undefined;

  private readonly disposables: vscode.Disposable[] = [];

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

  private async onMessage(message: { type: string; strategy?: GenerationStrategy; sizes?: number[] }) {
    if (message.type !== "rerun") return;

    // Re-running with a different arrangement is the main thing anyone does with this
    // view, so it re-measures in place rather than sending the user back to the
    // command palette. Scalars keep their configuration: a sort order means nothing
    // for a search target.
    if (message.strategy) {
      this.request = {
        ...this.request,
        inputs: undefined,
        sizes: message.sizes ?? this.request.sizes
      };
      this.pendingStrategy = message.strategy;
    } else if (message.sizes) {
      this.request = { ...this.request, sizes: message.sizes };
    }

    await this.run();
  }

  private pendingStrategy: GenerationStrategy | undefined;

  private async run() {
    this.panel.webview.postMessage({ type: "loading" });

    try {
      const request: JobRequest = { ...this.request };

      if (this.pendingStrategy) {
        const entries = await listEntryPoints(request.file);
        const chosen = entries.find(e => e.name === request.entry);
        if (chosen) {
          request.inputs = chosen.parameters.map(parameter => ({
            shape: shapeFor(parameter.type),
            strategy: isScalar(parameter.type) ? "Random" : this.pendingStrategy!,
            minValue: 0,
            maxValue: 1000,
            scalesWithSize: !isScalar(parameter.type) || chosen.parameters.length === 1,
            fixedValue: 0,
            seed: 20260909
          }));
        }
      }

      const response: JobResponse = await measure(request);
      this.panel.title = response.label ? `DSA: ${response.label}` : "DSA Visualizer";
      this.panel.webview.postMessage({ type: "result", response, request });
    } catch (error) {
      const message = error instanceof Error ? error.message : String(error);
      this.panel.webview.postMessage({ type: "error", message });
      reportError(error);
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
    ReportPanel.current = undefined;
    this.panel.dispose();
    while (this.disposables.length) this.disposables.pop()?.dispose();
  }
}

function isScalar(type: string): boolean {
  return type === "int" || type === "bool";
}

function shapeFor(type: string) {
  switch (type) {
    case "int[]": return "IntArray" as const;
    case "List<int>": return "IntList" as const;
    case "string": return "StringValue" as const;
    case "int[][]": return "Matrix" as const;
    case "bool": return "BoolScalar" as const;
    default: return "IntScalar" as const;
  }
}
