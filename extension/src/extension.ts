import * as fs from "fs";
import * as os from "os";
import * as path from "path";
import * as vscode from "vscode";
import { EntryPointLenses } from "./codeLens";
import { CostDecorations } from "./decorations";
import {
  configureEngine, EngineCancelledError, EngineFailedError, EngineNotFoundError, EntryListError, listEntryPoints, measure,
  resolveEnginePath
} from "./engine";
import { log, recentLog, showLog } from "./log";
import { offerPython, pythonInterpreter } from "./python";
import { RatingPrompt, RatingState } from "./rating";
import { issueUrl } from "./report";
import { explainStep, LanguageModels, StepToExplain } from "./explain";
import { EntryPoint, ExampleReport, InputSpec, JobRequest, JobResponse, StepTrace } from "./types";

let decorations: CostDecorations;
let rating: RatingPrompt;

/** The language of the file last run, for problem reports. */
let lastLanguage: string | undefined;

const RATING_KEY = "dsaPlayback.rating";
const EXTENSION_ID = "codewrapper12.dsa-playback";

/** Samples offered by the walkthrough, shipped inside the extension under samples/<language>. */
const SAMPLES = new Map([
  ["TwoSumSorted.cs", "csharp"], ["LevelOrder.cs", "csharp"], ["ClimbStairs.cs", "csharp"],
  ["two_sum_sorted.py", "python"], ["level_order.py", "python"], ["climb_stairs.py", "python"]
]);

const LANGUAGES = ["csharp", "python"];

/**
 * What the integration tests can observe. Decorations and webviews are not readable
 * through VS Code's API, so without this the only way to check the extension really works
 * is to look at it — which is how criterion 1 stayed unverified for three milestones.
 */
export interface TestApi {
  lastResponse(): JobResponse | undefined;
  lastSteps(): StepTrace | undefined;
  lastError(): string | undefined;
  lastExamples(): ExampleReport | undefined;
  /** The last answer sent to the panel for "Explain this step". */
  lastExplanation(): Record<string, unknown> | undefined;
  /** The id of the recorded run on screen, as the panel would send it back. */
  currentTraceId(): number | undefined;
  linesDecoratedIn(uri: vscode.Uri): number;
  /** The address "Report a Problem" opens, without opening it. */
  problemReportUrl(): string;
  sendPanelMessage(message: unknown): Promise<void>;
  lenses(document: vscode.TextDocument): Promise<vscode.CodeLens[]>;
  enginePath(): string;
}

const observed: {
  response?: JobResponse; steps?: StepTrace; error?: string; examples?: ExampleReport; explanation?: Record<string, unknown>
} = {};

export function activate(context: vscode.ExtensionContext): TestApi {
  configureEngine(context.extensionPath, context.extensionMode !== vscode.ExtensionMode.Production);
  decorations = new CostDecorations();
  const lenses = new EntryPointLenses();
  rating = new RatingPrompt({
    read: () => context.globalState.get<RatingState>(RATING_KEY),
    write: state => context.globalState.update(RATING_KEY, state),
    ask: (message, ...choices) => vscode.window.showInformationMessage(message, ...choices),
    openReviews: () => void vscode.env.openExternal(vscode.Uri.parse(reviewsUrl()))
  });

  context.subscriptions.push(
    decorations,
    vscode.languages.registerCodeLensProvider(LANGUAGES.map(language => ({ language })), lenses),
    vscode.workspace.onDidCloseTextDocument(document => lenses.forget(document.uri)),
    vscode.workspace.onDidChangeConfiguration(change => {
      if (change.affectsConfiguration("dsaPlayback.enginePath")) lenses.refresh();
    }),
    vscode.commands.registerCommand("dsaPlayback.visualize", (uri?: unknown, entry?: unknown) =>
      start(context, "steps", ...target(uri, entry))),
    vscode.commands.registerCommand("dsaPlayback.measure", (uri?: unknown, entry?: unknown) =>
      start(context, "cost", ...target(uri, entry))),
    vscode.commands.registerCommand("dsaPlayback.compare", () => start(context, "compare")),
    vscode.commands.registerCommand("dsaPlayback.clearAnnotations", () => decorations.clear()),
    vscode.commands.registerCommand("dsaPlayback.openSample", (name: string) => openSample(context, name)),
    vscode.commands.registerCommand("dsaPlayback.showLog", () => showLog()),
    vscode.commands.registerCommand("dsaPlayback.reportProblem", () => reportProblem(context)),
    vscode.commands.registerCommand("dsaPlayback.installPython", (message?: string) =>
      offerPython(message ?? "DSA Playback needs Python 3.9 or later to run Python files.")));

  return {
    lastResponse: () => observed.response,
    lastSteps: () => observed.steps,
    lastError: () => observed.error,
    lastExamples: () => observed.examples,
    lastExplanation: () => observed.explanation,
    currentTraceId: () => ReportPanel.currentTraceId(),
    linesDecoratedIn: uri => decorations.linesDecoratedIn(uri),
    problemReportUrl: () => problemReportUrl(context),
    sendPanelMessage: async message => { await ReportPanel.deliver(message); },
    lenses: document => lenses.provideCodeLenses(document, new vscode.CancellationTokenSource().token),
    enginePath: () => resolveEnginePath()
  };
}

export function deactivate() {}

type Tab = "steps" | "cost" | "compare";

/**
 * The file and method a command was asked about, keeping only what really is one. The
 * buttons above a method pass both. The ▶ in the editor's title bar passes the file and
 * then an object saying which editor group was clicked — taken as a method name, it
 * reached the engine as "[object Object]" and every run from that button failed.
 */
function target(uri: unknown, entry: unknown): [vscode.Uri | undefined, string | undefined] {
  return [uri instanceof vscode.Uri ? uri : undefined, typeof entry === "string" && entry.length > 0 ? entry : undefined];
}

async function start(context: vscode.ExtensionContext, tab: Tab, uri?: vscode.Uri, entryName?: string) {
  const document = uri
    ? await vscode.workspace.openTextDocument(uri)
    : vscode.window.activeTextEditor?.document;

  if (!document || !LANGUAGES.includes(document.languageId)) {
    vscode.window.showWarningMessage("Open a C# or Python file first, then press ▶ Visualize above a method.");
    return;
  }
  const python = document.languageId === "python" ? await pythonInterpreter(document.uri) : undefined;
  lastLanguage = document.languageId;

  // The engine reads the file from disk, so what runs must be what is on screen.
  if (document.isDirty) await document.save();
  const file = document.fileName;

  try {
    const entry = entryName ?? (await pickEntryPoint(file, "Which method?", python))?.qualified;
    if (!entry) return;

    let compareFile: string | undefined;
    let compareEntry: string | undefined;

    if (tab === "compare") {
      const picked = await vscode.window.showOpenDialog({
        canSelectMany: false,
        filters: { "Solutions": ["cs", "py"] },
        openLabel: "Compare against",
        title: "Pick the second solution"
      });
      if (!picked?.length) return;

      compareFile = picked[0].fsPath;
      compareEntry = (await pickEntryPoint(compareFile, "Which method in the second file?", python))?.qualified;
      if (!compareEntry) return;
    }

    // An empty setting means "let the engine choose", which is what lets it fall back
    // to small sizes for an expensive algorithm. Sending the default list explicitly
    // used to switch that fallback off and leave exponential solutions unmeasured.
    const configuredSizes = vscode.workspace.getConfiguration("dsaPlayback").get<number[]>("sizes") ?? [];

    const request: JobRequest = {
      file,
      entry,
      sizes: configuredSizes.length > 0 ? configuredSizes : undefined,
      budget: vscode.workspace.getConfiguration("dsaPlayback").get<number>("budget"),
      compareFile,
      compareEntry,
      python
    };

    ReportPanel.show(context, request, tab === "steps" ? "steps" : "cost");
  } catch (error) {
    reportError(error);
  }
}

async function pickEntryPoint(file: string, title: string, python?: string): Promise<EntryPoint | undefined> {
  const entries = await listEntryPoints(file, { python });

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
  const language = SAMPLES.get(name);
  if (!language) {
    log(`Unknown sample requested: ${name}`);
    return;
  }
  const source = path.join(context.extensionPath, "samples", language, name);
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

/**
 * Where a rating is left: the Marketplace for VS Code itself, Open VSX for editors that
 * install from there (VSCodium, Cursor and others).
 */
function reviewsUrl(): string {
  return vscode.env.uriScheme === "vscode" || vscode.env.uriScheme === "vscode-insiders"
    ? `https://marketplace.visualstudio.com/items?itemName=${EXTENSION_ID}&ssr=false#review-details`
    : "https://open-vsx.org/extension/codewrapper12/dsa-playback/reviews";
}

function problemReportUrl(context: vscode.ExtensionContext): string {
  return issueUrl({
    extensionVersion: String(context.extension.packageJSON.version),
    vscodeVersion: vscode.version,
    os: `${process.platform} ${os.release()} ${process.arch}`,
    language: lastLanguage ?? vscode.window.activeTextEditor?.document.languageId ?? "none",
    log: recentLog()
  }, os.homedir());
}

/**
 * Opens a new GitHub issue filled in with what is needed to diagnose a problem. Nothing
 * is sent from here: the student reads it in GitHub's own form and decides.
 */
async function reportProblem(context: vscode.ExtensionContext) {
  const url = problemReportUrl(context);
  log("Opened a problem report in the browser.");
  // Given a vscode.Uri, VS Code re-encodes the query, turning the & and = between the
  // title and the body into %26 and %3D, and GitHub then sees one long title. Its
  // extension host also takes the address as a string and hands that on untouched
  // (openUri in VS Code's extHostWindow), so the string is what is passed.
  await vscode.env.openExternal(url as unknown as vscode.Uri);
}

function explainEnabled(): boolean {
  return vscode.workspace.getConfiguration("dsaPlayback").get<boolean>("explainWithAI") === true;
}

/**
 * The solution's own lines, numbered, for Copilot to read: from the first statement the
 * run could reach to the last, with a little around them. Not the whole file, which may
 * hold a long problem description or other solutions.
 */
function solutionCode(lines: string[], trace: StepTrace): string {
  const recorded = trace.statements.map(s => s.line);
  if (recorded.length === 0) return lines.map((text, i) => `${i + 1}  ${text}`).join("\n");
  const CONTEXT = 3;
  const first = Math.max(1, Math.min(...recorded) - CONTEXT);
  const last = Math.min(lines.length, Math.max(...recorded) + CONTEXT);
  return lines.slice(first - 1, last).map((text, i) => `${first + i}  ${text}`).join("\n");
}

function reportError(error: unknown) {
  if (error instanceof EngineCancelledError) return;
  const message = error instanceof Error ? error.message : String(error);
  observed.error = message;
  log(`Shown to the student: ${message}`);

  if (error instanceof EntryListError && error.code) {
    void offerPython(message);
    return;
  }
  if (error instanceof EngineNotFoundError || error instanceof EngineFailedError) {
    vscode.window.showErrorMessage(message, "Show Details", "Report a Problem").then(choice => {
      if (choice === "Show Details") showLog();
      if (choice === "Report a Problem") void vscode.commands.executeCommand("dsaPlayback.reportProblem");
    });
    return;
  }
  vscode.window.showErrorMessage(`DSA Playback: ${message}`);
}

// ---- the panel --------------------------------------------------------------------

interface RerunMessage { type: "rerun"; inputs?: InputSpec[]; sizes?: number[] }
interface MeasureMessage { type: "measure" }
interface RevealMessage { type: "reveal"; line: number }
interface StepAtMessage { type: "stepAt"; line: number }
interface ShowLogMessage { type: "showLog" }
interface InstallPythonMessage { type: "installPython"; message?: string }
interface RunExamplesMessage { type: "runExamples" }
interface ReportProblemMessage { type: "reportProblem" }
interface ExplainMessage { type: "explain"; trace: number; index: number; callStack?: string[]; changed?: string[] }

/** Record a run: with typed values, a chosen example, and a size for anything generated. */
interface RunMessage {
  type: "run";
  values?: (string | null)[];
  example?: number;
  size?: number;
  /** Run with what was saved for this method, as when the run view is first opened. */
  useSaved?: boolean;
}

type PanelMessage = RerunMessage | MeasureMessage | RevealMessage | StepAtMessage | ShowLogMessage | RunMessage
  | InstallPythonMessage | RunExamplesMessage | ReportProblemMessage | ExplainMessage;

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
  private checkingExamples: AbortController | undefined;
  private explaining: vscode.CancellationTokenSource | undefined;

  /** The recorded run on screen, and the file text it ran, for explaining a step. */
  private shown: { id: number; trace: StepTrace; source: string } | undefined;
  private static traces = 0;

  private constructor(
    private readonly panel: vscode.WebviewPanel,
    private readonly context: vscode.ExtensionContext,
    private request: JobRequest) {

    this.panel.webview.html = this.render();
    this.panel.webview.onDidReceiveMessage(message => this.onMessage(message), null, this.disposables);
    vscode.workspace.onDidChangeConfiguration(change => {
      if (change.affectsConfiguration("dsaPlayback.explainWithAI")) {
        this.panel.webview.postMessage({ type: "settings", explain: explainEnabled() });
      }
    }, null, this.disposables);
    this.panel.onDidDispose(() => this.dispose(), null, this.disposables);
  }

  static currentTraceId(): number | undefined {
    return ReportPanel.current?.shown?.id;
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
        "dsaPlaybackReport", "DSA Playback", { viewColumn: column, preserveFocus: true },
        {
          enableScripts: true,
          retainContextWhenHidden: true,
          localResourceRoots: [vscode.Uri.joinPath(context.extensionUri, "media")]
        });
      panel = new ReportPanel(webview, context, request);
      ReportPanel.current = panel;
    }

    panel.panel.title = `DSA: ${path.basename(request.file, path.extname(request.file))}`;
    panel.panel.webview.postMessage({ type: "open", request, tab, explain: explainEnabled() });

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
      case "runExamples": await this.runExamples(); return;
      case "stepAt": await decorations.showStep(this.request.file, message.line); return;
      case "showLog": showLog(); return;
      case "reportProblem": await vscode.commands.executeCommand("dsaPlayback.reportProblem"); return;
      case "explain": await this.explain(message); return;
      case "installPython": await offerPython(message.message ?? "DSA Playback needs Python 3.9 or later."); return;
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
    return `dsaPlayback.inputs:${this.request.file}::${this.request.entry}`;
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

      observed.response = response;
      if (response.errorCode) {
        this.panel.webview.postMessage({ type: "error", message: response.errors.join(" "), code: response.errorCode });
        void offerPython(response.errors.join(" "));
        return;
      }
      this.panel.webview.postMessage({ type: "result", response, request });
      if (response.primary?.status === "Completed") void rating.succeeded();

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
        example: choice.example,
        python: this.request.python
      }, controller.signal);
      if (controller.signal.aborted) return;

      if (response.errorCode) {
        this.panel.webview.postMessage({ type: "stepsError", message: response.errors.join(" "), code: response.errorCode });
        void offerPython(response.errors.join(" "));
        return;
      }

      const traceId = ++ReportPanel.traces;
      this.shown = response.steps ? { id: traceId, trace: response.steps, source: response.source ?? "" } : undefined;
      this.panel.webview.postMessage({ type: "steps", trace: response.steps, traceId, errors: response.errors, choice });
      observed.steps = response.steps ?? undefined;
      if (response.steps?.status === "Completed") void rating.succeeded();
    } catch (error) {
      this.fail("stepsError", error);
    } finally {
      if (this.recording === controller) this.recording = undefined;
    }
  }

  /**
   * Asks the student's own Copilot to explain one step. Only when the setting is on and the
   * button was pressed; what is sent is the solution's code, the line, the call stack and
   * the variables at that step.
   */
  private async explain(message: ExplainMessage) {
    const reply = (fields: Record<string, unknown>) => {
      observed.explanation = fields;
      return this.panel.webview.postMessage({ type: "explanation", trace: message.trace, index: message.index, ...fields });
    };

    if (!explainEnabled()) {
      await reply({ done: true, error: "Explaining steps is turned off. Turn on dsaPlayback.explainWithAI to use it." });
      return;
    }
    const shown = this.shown;
    const step = shown?.id === message.trace ? shown.trace.steps[message.index] : undefined;
    if (!shown || !step) {
      await reply({ done: true, error: "This run has been replaced. Press Explain again." });
      return;
    }

    this.explaining?.cancel();
    const cancel = new vscode.CancellationTokenSource();
    this.explaining = cancel;

    const lines = shown.source.split(/\r?\n/);
    const toExplain: StepToExplain = {
      language: path.extname(this.request.file).toLowerCase() === ".py" ? "Python" : "C#",
      code: solutionCode(lines, shown.trace),
      line: step.line,
      lineText: lines[step.line - 1] ?? "",
      callStack: message.callStack ?? [step.method],
      variables: step.variables.map(v => ({ name: v.name, value: v.display })),
      changed: message.changed ?? []
    };

    log(`Explaining step ${message.index + 1} with Copilot (line ${step.line}).`);
    const lm = (vscode as unknown as { lm?: LanguageModels }).lm;
    const result = await explainStep(lm, text => vscode.LanguageModelChatMessage.User(text), toExplain,
      soFar => { if (!cancel.token.isCancellationRequested) void reply({ text: soFar, done: false }); },
      cancel.token);
    if (cancel.token.isCancellationRequested) return;

    if (result.ok) await reply({ text: result.text, done: true, model: result.model });
    else {
      log(`Explaining failed: ${result.reason}`);
      await reply({ done: true, error: result.reason });
    }
    if (this.explaining === cancel) this.explaining = undefined;
  }

  /** Runs every example in the file and lists each result against its expected output. */
  private async runExamples() {
    this.checkingExamples?.abort();
    const controller = new AbortController();
    this.checkingExamples = controller;

    try {
      const response = await measure({
        file: this.request.file,
        entry: this.request.entry,
        allExamples: true,
        budget: this.request.budget,
        python: this.request.python
      }, controller.signal);
      if (controller.signal.aborted) return;

      if (response.errorCode || !response.examples) {
        const message = response.errors.join(" ") || "The engine returned no results.";
        this.panel.webview.postMessage({ type: "examplesError", message, code: response.errorCode });
        if (response.errorCode) void offerPython(message);
        return;
      }
      observed.examples = response.examples;
      this.panel.webview.postMessage({ type: "examples", report: response.examples });
    } catch (error) {
      this.fail("examplesError", error);
    } finally {
      if (this.checkingExamples === controller) this.checkingExamples = undefined;
    }
  }

  private fail(type: "error" | "stepsError" | "examplesError", error: unknown) {
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
<title>DSA Playback</title>
</head>
<body>
<div id="app"><p class="placeholder">Loading…</p></div>
<script nonce="${nonce}" src="${media("run.js")}"></script>
<script nonce="${nonce}" src="${media("main.js")}"></script>
</body>
</html>`;
  }

  private dispose() {
    this.measuring?.abort();
    this.recording?.abort();
    this.checkingExamples?.abort();
    this.explaining?.cancel();
    decorations.clearStep();
    ReportPanel.current = undefined;
    this.panel.dispose();
    while (this.disposables.length) this.disposables.pop()?.dispose();
  }
}
