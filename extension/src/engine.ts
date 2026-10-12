import { spawn } from "child_process";
import * as fs from "fs";
import * as path from "path";
import * as vscode from "vscode";
import { log } from "./log";
import { EntryPoint, JobRequest, JobResponse } from "./types";

const EXECUTABLE = process.platform === "win32" ? "DsaViz.Engine.exe" : "DsaViz.Engine";

/** Where the packaging script puts the self-contained engine inside the extension. */
const BUNDLED = ["engine", EXECUTABLE];

/** The engine's debug build, relative to the extension folder in this repository. */
const DEVELOPMENT_BUILD = ["..", "engine", "DsaViz.Engine", "bin", "Debug", "net8.0", EXECUTABLE];

export class EngineNotFoundError extends Error {}

/** The engine ran but stopped without an answer. The message is for the student; details go to the log. */
export class EngineFailedError extends Error {}

let location: { root: string; development: boolean } | undefined;

/** Called once on activation: where the extension is installed, and whether it runs from source. */
export function configureEngine(extensionRoot: string, development: boolean) {
  location = { root: extensionRoot, development };
}

/**
 * The places the engine is looked for, in order. Installed, the bundled engine is the
 * only one. Run from source (F5), the debug build comes first, so a stale bundled copy
 * left by an earlier packaging run can never hide the code being worked on.
 */
export function engineCandidates(): string[] {
  if (!location) return [];
  const bundled = path.join(location.root, ...BUNDLED);
  const development = path.join(location.root, ...DEVELOPMENT_BUILD);
  return location.development ? [development, bundled] : [bundled, development];
}

/**
 * Locates the engine executable. An explicit setting always wins; otherwise it is found
 * relative to the extension itself, never relative to whatever folder is open — which is
 * what used to make the extension fail in any folder but this repository.
 */
export function resolveEnginePath(): string {
  const configured = vscode.workspace.getConfiguration("dsaViz").get<string>("enginePath")?.trim();
  if (configured) {
    if (!fs.existsSync(configured)) {
      throw new EngineNotFoundError(
        `dsaViz.enginePath points at '${configured}', which does not exist.`);
    }
    return configured;
  }

  const candidates = engineCandidates();
  const found = candidates.find(candidate => fs.existsSync(candidate));
  if (found) return found;

  log(`Engine not found. Looked in:\n  ${candidates.join("\n  ")}`);
  throw new EngineNotFoundError(
    "The DSA Visualizer engine is missing from this installation. Reinstalling the extension restores it.");
}

export class EngineCancelledError extends Error {}

/**
 * Runs the engine and returns what it printed.
 *
 * The engine has its own per-run time limit, but this process-level limit is the one
 * that guarantees the editor never waits forever: whatever happens inside the engine,
 * after `timeoutSeconds` it is killed and the user is told. Without it a hung engine
 * left the panel showing "Measuring…" indefinitely.
 */
function run(executable: string, args: string[], stdin?: string, signal?: AbortSignal): Promise<string> {
  const timeoutSeconds = vscode.workspace.getConfiguration("dsaViz").get<number>("timeoutSeconds") ?? 120;

  const started = Date.now();
  log(`engine ${args.join(" ")}`);

  return new Promise((resolve, reject) => {
    const child = spawn(executable, args);
    let settled = false;
    const settle = (action: () => void) => { if (!settled) { settled = true; clearTimeout(timer); action(); } };

    const timer = setTimeout(() => {
      child.kill("SIGKILL");
      settle(() => reject(new Error(
        `The engine did not finish within ${timeoutSeconds} seconds and was stopped. ` +
        "Try fewer or smaller sizes, or raise dsaViz.timeoutSeconds.")));
    }, timeoutSeconds * 1000);

    signal?.addEventListener("abort", () => {
      child.kill("SIGKILL");
      settle(() => reject(new EngineCancelledError("Superseded by a newer measurement.")));
    });

    let stdout = "";
    let stderr = "";
    child.stdout.on("data", chunk => (stdout += chunk));
    child.stderr.on("data", chunk => (stderr += chunk));

    child.on("error", error => settle(() => {
      log(`  could not start ${executable}: ${error.message}`);
      reject(new EngineFailedError(`The visualizer's engine could not be started: ${error.message}`));
    }));

    child.on("close", code => settle(() => {
      log(`  exit ${code} after ${Date.now() - started} ms, ${stdout.length} chars of output`);
      if (stderr.trim()) log(`  stderr:\n${stderr.trim()}`);

      // The engine uses a non-zero exit code to report that a measurement did not
      // complete, and still writes a JSON explanation. Only treat it as a failure when
      // there is genuinely nothing to read.
      if (stdout.trim()) { resolve(stdout); return; }

      // Exit code 2 is the engine refusing a request, with a sentence meant to be read.
      // Anything else is the engine itself breaking, and its stderr is a stack trace —
      // kept in the log, never shown.
      if (code === 2 && stderr.trim()) reject(new Error(stderr.trim()));
      else reject(new EngineFailedError(
        `The visualizer stopped unexpectedly (exit code ${code}). This is a bug in the visualizer, not in your code.`));
    }));

    // An engine that exits before reading its input makes this write fail with EPIPE.
    // The exit itself is what gets reported, from the close handler above; without a
    // listener the stream error would surface as an uncaught exception in the editor.
    child.stdin.on("error", error => log(`  could not send input to the engine: ${error.message}`));
    if (stdin !== undefined) { child.stdin.write(stdin); child.stdin.end(); }
  });
}

/**
 * The methods in a file that can be run. Given `source`, reads that text instead of the
 * file on disk, so a file with unsaved changes is listed as the student sees it.
 */
export async function listEntryPoints(file: string, source?: string, signal?: AbortSignal): Promise<EntryPoint[]> {
  const args = source === undefined ? ["--file", file, "--list-entries"] : ["--file", "-", "--list-entries"];
  const output = await run(resolveEnginePath(), args, source, signal);
  return (JSON.parse(output) as { entries: EntryPoint[] }).entries ?? [];
}

export async function measure(request: JobRequest, signal?: AbortSignal): Promise<JobResponse> {
  const output = await run(resolveEnginePath(), ["--job", "-"], JSON.stringify(request), signal);
  return JSON.parse(output) as JobResponse;
}
