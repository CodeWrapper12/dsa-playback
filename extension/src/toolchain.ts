import * as vscode from "vscode";
import { offerPython } from "./python";

/** VS Code's own guides to installing a C++ compiler, one per system. */
const CPP_GUIDES: Record<string, string> = {
  darwin: "https://code.visualstudio.com/docs/cpp/config-clang-mac",
  linux: "https://code.visualstudio.com/docs/cpp/config-linux",
  win32: "https://code.visualstudio.com/docs/cpp/config-mingw"
};

/** The command that installs a compiler, where one command does it. */
const CPP_COMMANDS: Record<string, string> = {
  darwin: "xcode-select --install",
  linux: "sudo apt install g++"
};

/**
 * Shown when nothing can run because a tool is missing: Python, or a C++ compiler. Each
 * offers what fixes it, as buttons. Nothing is installed without the student doing it.
 */
export async function offerToolchain(code: string | undefined, message: string) {
  if (code === "cpp-missing") {
    await offerCompiler(message);
    return;
  }
  await offerPython(message);
}

async function offerCompiler(message: string) {
  const command = CPP_COMMANDS[process.platform];
  const actions = [...(command ? ["Copy Install Command"] : []), "How to Install", "Set Compiler Path"];
  const choice = await vscode.window.showErrorMessage(message, ...actions);
  if (choice === "Copy Install Command" && command) {
    await vscode.env.clipboard.writeText(command);
    void vscode.window.showInformationMessage(`Copied: ${command} — paste it into a terminal, then try again.`);
  }
  if (choice === "How to Install") {
    await vscode.env.openExternal(vscode.Uri.parse(CPP_GUIDES[process.platform] ?? CPP_GUIDES.linux));
  }
  if (choice === "Set Compiler Path") {
    await vscode.commands.executeCommand("workbench.action.openSettings", "dsaPlayback.cppCompiler");
  }
}
