import * as vscode from "vscode";
import { log } from "./log";

/** Where students get Python, offered when none is found. */
export const PYTHON_DOWNLOAD = "https://www.python.org/downloads/";

/**
 * The Python to run a student's file with. An explicit dsaViz.pythonPath wins; otherwise
 * the interpreter selected in VS Code's Python extension, so a student's virtual
 * environment is the one used. Undefined means "let the engine find python3 on the path".
 */
export async function pythonInterpreter(resource?: vscode.Uri): Promise<string | undefined> {
  const configured = vscode.workspace.getConfiguration("dsaViz").get<string>("pythonPath")?.trim();
  if (configured) return configured;

  const extension = vscode.extensions.getExtension("ms-python.python");
  if (!extension) return undefined;

  try {
    const api = extension.isActive ? extension.exports : await extension.activate();
    const environments = api?.environments;
    const active = environments?.getActiveEnvironmentPath?.(resource);
    if (!active?.path) return undefined;
    const resolved = await environments.resolveEnvironment?.(active);
    return resolved?.executable?.uri?.fsPath ?? active.path;
  } catch (error) {
    // The Python extension's API is optional: if it cannot answer, the path is searched.
    log(`Could not ask the Python extension for its interpreter: ${error instanceof Error ? error.message : String(error)}`);
    return undefined;
  }
}

/** Shown when Python is missing or too old: the two ways to fix it, as buttons. */
export async function offerPython(message: string) {
  const hasPythonExtension = !!vscode.extensions.getExtension("ms-python.python");
  const actions = hasPythonExtension ? ["Install Python", "Select Interpreter"] : ["Install Python"];
  const choice = await vscode.window.showErrorMessage(message, ...actions);
  if (choice === "Install Python") await vscode.env.openExternal(vscode.Uri.parse(PYTHON_DOWNLOAD));
  if (choice === "Select Interpreter") await vscode.commands.executeCommand("python.setInterpreter");
}
