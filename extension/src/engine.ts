import { spawn } from "child_process";
import * as fs from "fs";
import * as path from "path";
import * as vscode from "vscode";
import { EntryPoint, JobRequest, JobResponse } from "./types";

const RELATIVE_BUILD_PATH = path.join(
  "engine", "DsaViz.Engine", "bin", "Debug", "net8.0", "DsaViz.Engine");

export class EngineNotFoundError extends Error {}

/**
 * Locates the engine executable.
 *
 * An explicit setting always wins. Otherwise the open workspace folders are searched
 * for a debug build, which is what makes the extension usable while the engine is
 * still being developed alongside it.
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

  for (const folder of vscode.workspace.workspaceFolders ?? []) {
    const candidate = path.join(folder.uri.fsPath, RELATIVE_BUILD_PATH);
    if (fs.existsSync(candidate)) return candidate;
  }

  throw new EngineNotFoundError(
    "Could not find the DsaViz engine. Build it with `dotnet build` in the engine folder, " +
    "or set `dsaViz.enginePath` to the built executable.");
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

    child.on("error", error =>
      settle(() => reject(new Error(`Could not start the engine: ${error.message}`))));

    child.on("close", code => settle(() => {
      // The engine uses a non-zero exit code to report that a measurement did not
      // complete, and still writes a JSON explanation. Only treat it as a failure when
      // there is genuinely nothing to read.
      if (stdout.trim()) resolve(stdout);
      else reject(new Error(stderr.trim() || `The engine exited with code ${code} and produced no output.`));
    }));

    if (stdin !== undefined) { child.stdin.write(stdin); child.stdin.end(); }
  });
}

export async function listEntryPoints(file: string): Promise<EntryPoint[]> {
  const output = await run(resolveEnginePath(), ["--file", file, "--list-entries"]);
  return (JSON.parse(output) as { entries: EntryPoint[] }).entries ?? [];
}

export async function measure(request: JobRequest, signal?: AbortSignal): Promise<JobResponse> {
  const output = await run(resolveEnginePath(), ["--job", "-"], JSON.stringify(request), signal);
  return JSON.parse(output) as JobResponse;
}
