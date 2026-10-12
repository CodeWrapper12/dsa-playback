import * as vscode from "vscode";

/**
 * The "DSA Playback" output channel. Everything a failure needs to be diagnosed later
 * goes here — which engine ran, with what arguments, how it exited, and anything it
 * wrote to stderr — so the student sees a plain sentence and the details are still
 * one click away.
 */
let channel: vscode.OutputChannel | undefined;

export function log(message: string) {
  channel ??= vscode.window.createOutputChannel("DSA Playback");
  channel.appendLine(`[${new Date().toISOString()}] ${message}`);
}

export function showLog() {
  channel ??= vscode.window.createOutputChannel("DSA Playback");
  channel.show(true);
}
