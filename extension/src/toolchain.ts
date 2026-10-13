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

/** rustup's installer, which installs rustc and cargo. */
const RUSTUP = "https://rustup.rs";
const RUSTUP_COMMAND = "curl --proto '=https' --tlsv1.2 -sSf https://sh.rustup.rs | sh";

/** The linker rustc needs, where it is missing. */
const LINKER: Record<string, { command?: string; guide: string }> = {
  darwin: { command: "xcode-select --install", guide: "https://code.visualstudio.com/docs/cpp/config-clang-mac" },
  linux: { command: "sudo apt install build-essential", guide: "https://code.visualstudio.com/docs/cpp/config-linux" },
  win32: { guide: "https://rust-lang.github.io/rustup/installation/windows-msvc.html" }
};

/**
 * Shown when nothing can run because a tool is missing: Python, a C++ compiler, Rust, the
 * linker Rust needs, a JDK, or Node.js. Each offers what fixes it, as buttons. Nothing is installed
 * without the student doing it.
 */
export async function offerToolchain(code: string | undefined, message: string) {
  if (code === "cpp-missing") {
    await offerCompiler(message);
    return;
  }
  if (code === "rust-missing" || code === "rust-linker") {
    await offerRust(code, message);
    return;
  }
  if (code === "java-missing" || code === "java-version") {
    await offerJava(message);
    return;
  }
  if (code === "node-missing" || code === "node-version") {
    await offerNode(message);
    return;
  }
  await offerPython(message);
}

/** Node.js's own downloads, for every system. */
const NODE_DOWNLOAD = "https://nodejs.org/en/download";

async function offerNode(message: string) {
  const choice = await vscode.window.showErrorMessage(message, "Download Node.js", "Set Node Path");
  if (choice === "Download Node.js") await vscode.env.openExternal(vscode.Uri.parse(NODE_DOWNLOAD));
  if (choice === "Set Node Path") await vscode.commands.executeCommand("workbench.action.openSettings", "dsaPlayback.node");
}

/** A free, widely used JDK build, for every system. */
const JDK_DOWNLOAD = "https://adoptium.net/temurin/releases/?version=21";

async function offerJava(message: string) {
  const choice = await vscode.window.showErrorMessage(message, "Download a JDK", "Set JDK Path");
  if (choice === "Download a JDK") await vscode.env.openExternal(vscode.Uri.parse(JDK_DOWNLOAD));
  if (choice === "Set JDK Path") await vscode.commands.executeCommand("workbench.action.openSettings", "dsaPlayback.javaHome");
}

async function offerRust(code: string, message: string) {
  const linker = LINKER[process.platform] ?? LINKER.linux;
  const command = code === "rust-missing" ? (process.platform === "win32" ? undefined : RUSTUP_COMMAND) : linker.command;
  const actions = [...(command ? ["Copy Install Command"] : []), "How to Install", ...(code === "rust-missing" ? ["Set Rust Path"] : [])];
  const choice = await vscode.window.showErrorMessage(message, ...actions);
  if (choice === "Copy Install Command" && command) {
    await vscode.env.clipboard.writeText(command);
    void vscode.window.showInformationMessage(`Copied: ${command} — paste it into a terminal, then try again.`);
  }
  if (choice === "How to Install") await vscode.env.openExternal(vscode.Uri.parse(code === "rust-missing" ? RUSTUP : linker.guide));
  if (choice === "Set Rust Path") await vscode.commands.executeCommand("workbench.action.openSettings", "dsaPlayback.rustc");
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
