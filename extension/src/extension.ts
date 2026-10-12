import * as fs from "fs";
import * as path from "path";
import * as vscode from "vscode";
import { EntryPointLenses } from "./codeLens";
import { CostDecorations } from "./decorations";
import {
  configureEngine, EngineCancelledError, EngineFailedError, EngineNotFoundError, listEntryPoints, measure,
  resolveEnginePath
} from "./engine";
import { log, showLog } from "./log";
import { EntryPoint, InputSpec, JobRequest, JobResponse, StepTrace } from "./types";

let decorations: CostDecorations;

/** Samples offered by the walkthrough, shipped inside the extension under samples/csharp. */
const SAMPLES = new Set(["TwoSumSorted.cs", "LevelOrder.cs", "ClimbStairs.cs"]);

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
  lenses(document: vscode.TextDocument): Promise<vscode.CodeLens[]>;
  enginePath(): string;
}

const observed: { response?: JobResponse; steps?: StepTrace; error?: string } = {};

export function activate(context: vscode.ExtensionContext): TestApi {
  configureEngine(context.extensionPath, context.extensionMode !== vscode.ExtensionMode.Production);
  decorations = new CostDecorations();
  const lenses = new EntryPointLenses();

  context.subscriptions.push(
    decorations,
    vscode.languages.registerCodeLensProvider({ language: "csharp" }, lenses),
    vscode.workspace.onDidCloseTextDocument(document => lenses.forget(document.uri)),
    vscode.workspace.onDidChangeConfiguration(change => {
      if (change.affectsConfiguration("dsaViz.enginePath")) lenses.refresh();
    }),
    vscode.commands.registerCommand("dsaViz.visualize", (uri?: vscode.Uri, entry?: string) =>
      start(context, "steps", uri, entry)),
    vscode.commands.registerCommand("dsaViz.measure", (uri?: vscode.Uri, entry?: string) =>
      start(context, "cost", uri, entry)),
    vscode.commands.registerCommand("dsaViz.compare", () => start(context, "compare")),
    vscode.commands.registerCommand("dsaViz.clearAnnotations", () => decorations.clear()),
    vscode.commands.registerCommand("dsaViz.openSample", (name: string) => openSample(context, name)),
    vscode.commands.registerCommand("dsaViz.showLog", () => showLog()));

  return {
    lastResponse: () => observed.response,
    lastSteps: () => observed.steps,
    lastError: () => observed.error,
    linesDecoratedIn: uri => decorations.linesDecoratedIn(uri),
    sendPanelMessage: async message => { await ReportPanel.deliver(message); },
    lenses: document => lenses.provideCodeLenses(document, new vscode.CancellationTokenSource().token),
    enginePath: () => resolveEnginePath()
  };
}

export function deactivate() {}

type Tab = "steps" | "cost" | "compare";

async function start(context: vscode.ExtensionContext, tab: Tab, uri?: vscode.Uri, entryName?: string) {
  const document = uri
    ? await vscode.workspace.openTextDocument(uri)
    : vscode.window.activeTextEditor?.document;

  if (!document || document.languageId !== "csharp") {
    vscode.window.showWarningMessage("Open a C# file first, then press ▶ Visualize above a method.");
    return;
  }

  // The engine reads the file from disk, so what runs must be what is on screen.
  if (document.isDirty) await document.save();
  const file = document.fileName;

  try {
    const entry = entryName ?? (await pickEntryPoint(file, "Which method?"))?.qualified;
    if (!entry) return;

    let compareFile: string | undefined;
    let compareEntry: string | undefined;

    if (tab === "compare") {
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
      entry,
      sizes: configuredSizes.length > 0 ? configuredSizes : undefined,
      budget: vscode.workspace.getConfiguration("dsaViz").get<number>("budget"),
      compareFile,
      compareEntry
    };

    ReportPanel.show(context, request, tab === "steps" ? "steps" : "cost");
  } catch (error) {
    reportError(error);
  }
}

async function pickEntryPoint(file: string, title: string): Promise<EntryPoint | undefined> {
  const entries = await listEntryPoints(file);

  if (entries.length === 0) {
    vscode.window.showWarningMessage(`${path.basename(file)} has no public methods to visualize.`);
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
    vscode.window.showWarningMessage(`${picked.entry.name} cannot be run: ${picked.entry.reason}`);
    return undefined;
  }

  return picked?.entry;
}

/**
 * Copies a walkthrough sample somewhere the student can edit it, then opens it. Opening
 * it in place would mean editing files inside the extension's own installation.
 */
async function openSample(context: vscode.ExtensionContext, name: string) {
  if (!SAMPLES.has(name)) {
    log(`Unknown sample requested: ${name}`);
    return;
  }
  const source = path.join(context.extensionPath, "samples", "csharp", name);
  const folder = path.join(context.globalStorageUri.fsPath, "samples");
  const target = path.join(folder, name);

  try {
    await fs.promises.mkdir(folder, { recursive: true });
    // An earlier copy may hold the student's own edits, so it is never overwritten.
    if (!fs.existsSync(target)) await fs.promises.copyFile(source, target);
    const document = await vscode.workspace.openTextDocument(target);
    await vscode.window.showTextDocument(document);
  } catch (error) {
    reportError(error);
  }
}

function reportError(error: unknown) {
  if (error instanceof EngineCancelledError) return;
  const message = error instanceof Error ? error.message : String(error);
  observed.error = message;
  log(`Shown to the student: ${message}`);

  if (error instanceof EngineNotFoundError || error instanceof EngineFailedError) {
    vscode.window.showErrorMessage(message, "Show Details").then(choice => {
      if (choice === "Show Details") showLog();
    });
    return;
  }
  vscode.window.showErrorMessage(`DSA Visualizer: ${message}`);
}

// ---- the panel --------------------------------------------------------------------

interface RerunMessage { type: "rerun"; inputs?: InputSpec[]; sizes?: number[] }
interface MeasureMessage { type: "measure" }
interface RevealMessage { type: "reveal"; line: number }
interface StepAtMessage { type: "stepAt"; line: number }
interface ShowLogMessage { type: "showLog" }

/** Record a run: with typed values, a chosen example, and a size for anything generated. */
interface RunMessage {
  type: "run";
  values?: (string | null)[];
  example?: number;
  size?: number;
  /** Run with what was saved for this method, as when the run view is first opened. */
  useSaved?: boolean;
}

type PanelMessage = RerunMessage | MeasureMessage | RevealMessage | StepAtMessage | ShowLogMessage | RunMessage;

/** What the student chose for one method, kept across restarts. */
interface SavedInputs {
  values?: (string | null)[];
  example?: number;
  size?: number;
}

const DEFAULT_STEP_SIZE = 8;
const MAX_STEP_SIZE = 64;

class ReportPanel {
  private static current: ReportPanel | undefined;

  private readonly disposables: vscode.Disposable[] = [];
  private measuring: AbortController | undefined;
  private recording: AbortController | undefined;

  private constructor(
    private readonly panel: vscode.WebviewPanel,
    private readonly context: vscode.ExtensionContext,
    private request: JobRequest) {

    this.panel.webview.html = this.render();
    this.panel.webview.onDidReceiveMessage(message => this.onMessage(message), null, this.disposables);
    this.panel.onDidDispose(() => this.dispose(), null, this.disposables);
  }

  /** Routes a message as if the webview had sent it. For integration tests. */
  static async deliver(message: unknown) {
    await ReportPanel.current?.onMessage(message as PanelMessage);
  }

  static show(context: vscode.ExtensionContext, request: JobRequest, tab: "steps" | "cost") {
    const column = vscode.ViewColumn.Beside;

    let panel = ReportPanel.current;
    if (panel) {
      panel.request = request;
      panel.panel.reveal(column, true);
    } else {
      const webview = vscode.window.createWebviewPanel(
        "dsaVizReport", "DSA Visualizer", { viewColumn: column, preserveFocus: true },
        {
          enableScripts: true,
          retainContextWhenHidden: true,
          localResourceRoots: [vscode.Uri.joinPath(context.extensionUri, "media")]
        });
      panel = new ReportPanel(webview, context, request);
      ReportPanel.current = panel;
    }

    panel.panel.title = `DSA: ${path.basename(request.file, path.extname(request.file))}`;
    panel.panel.webview.postMessage({ type: "open", request, tab });

    // Only what is on screen is computed. Opening on the run view records a run, which
    // takes a moment; measuring growth takes several seconds and waits until asked for.
    if (tab === "steps") void panel.record(panel.saved());
    else void panel.measure();
  }

  private async onMessage(message: PanelMessage) {
    switch (message.type) {
      case "run": {
        if (message.useSaved) { await this.record(this.saved()); return; }
        const choice: SavedInputs = { values: message.values, example: message.example, size: message.size };
        await this.save(choice);
        await this.record(choice);
        return;
      }
      case "measure": await this.measure(); return;
      case "stepAt": await decorations.showStep(this.request.file, message.line); return;
      case "showLog": showLog(); return;
      case "reveal": await this.reveal(message.line); return;
      case "rerun":
        // The panel sends back the engine's own resolved inputs with the user's edits
        // applied, so this side never has to reproduce the engine's inference rules.
        this.request = {
          ...this.request,
          inputs: message.inputs ?? this.request.inputs,
          sizes: message.sizes ?? this.request.sizes
        };
        await this.measure();
        return;
    }
  }

  private async reveal(line: number) {
    const document = await vscode.workspace.openTextDocument(this.request.file);
    const editor = await vscode.window.showTextDocument(document, { preview: false, viewColumn: vscode.ViewColumn.One });
    const target = Math.max(0, Math.min(line - 1, document.lineCount - 1));
    const range = document.lineAt(target).range;
    editor.selection = new vscode.Selection(range.start, range.start);
    editor.revealRange(range, vscode.TextEditorRevealType.InCenterIfOutsideViewport);
  }

  private get savedKey() {
    return `dsaViz.inputs:${this.request.file}::${this.request.entry}`;
  }

  private saved(): SavedInputs {
    return this.context.globalState.get<SavedInputs>(this.savedKey) ?? {};
  }

  private async save(choice: SavedInputs) {
    // Nothing typed and nothing chosen is the default, which needs no entry at all.
    const typed = choice.values?.some(value => value !== null && value !== undefined);
    const empty = !typed && choice.example === undefined && choice.size === undefined;
    await this.context.globalState.update(this.savedKey, empty ? undefined : choice);
  }

  private async measure() {
    // Re-measuring while a measurement is still running replaces it rather than queueing.
    this.measuring?.abort();
    const controller = new AbortController();
    this.measuring = controller;
    this.panel.webview.postMessage({ type: "loading" });

    try {
      const request: JobRequest = { ...this.request };
      const response = await measure(request, controller.signal);
      if (controller.signal.aborted) return;

      this.panel.webview.postMessage({ type: "result", response, request });
      observed.response = response;

      // The costs belong on the student's own file, not on a copy of it inside a panel.
      const detail = response.primary?.detail;
      if (detail && response.primary?.status === "Completed") {
        const points = response.primary.points;
        const measuredAt = points.length ? points[points.length - 1].size : null;
        await decorations.apply(request.file, detail, measuredAt, response.primary.derivation);
      } else {
        decorations.clear();
      }
    } catch (error) {
      this.fail("error", error);
    } finally {
      if (this.measuring === controller) this.measuring = undefined;
    }
  }

  /**
   * Records a run for the run view. A second build and a second run, kept apart from
   * measurement so recording every variable can never show up in a measured number.
   */
  private async record(choice: SavedInputs) {
    this.recording?.abort();
    const controller = new AbortController();
    this.recording = controller;
    this.panel.webview.postMessage({ type: "stepsLoading", choice });

    const size = Math.max(0, Math.min(choice.size ?? DEFAULT_STEP_SIZE, MAX_STEP_SIZE));
    try {
      const response = await measure({
        file: this.request.file,
        entry: this.request.entry,
        stepSize: size,
        values: choice.values,
        example: choice.example
      }, controller.signal);
      if (controller.signal.aborted) return;

      this.panel.webview.postMessage({ type: "steps", trace: response.steps, errors: response.errors, choice });
      observed.steps = response.steps ?? undefined;
    } catch (error) {
      this.fail("stepsError", error);
    } finally {
      if (this.recording === controller) this.recording = undefined;
    }
  }

  private fail(type: "error" | "stepsError", error: unknown) {
    if (error instanceof EngineCancelledError) return;
    const message = error instanceof Error ? error.message : String(error);
    const internal = error instanceof EngineFailedError || error instanceof EngineNotFoundError;
    this.panel.webview.postMessage({ type, message, internal });
    reportError(error);
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
<div id="app"><p class="placeholder">Loading…</p></div>
<script nonce="${nonce}" src="${media("main.js")}"></script>
</body>
</html>`;
  }

  private dispose() {
    this.measuring?.abort();
    this.recording?.abort();
    decorations.clearStep();
    ReportPanel.current = undefined;
    this.panel.dispose();
    while (this.disposables.length) this.disposables.pop()?.dispose();
  }
}
