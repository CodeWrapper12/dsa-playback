import * as vscode from "vscode";
import { REPORTED_LOG_LINES } from "./report";

/**
 * The "DSA Playback" output channel. Everything a failure needs to be diagnosed later
 * goes here — which engine ran, with what arguments, how it exited, and anything it
 * wrote to stderr — so the student sees a plain sentence and the details are still
 * one click away.
 */
let channel: vscode.OutputChannel | undefined;

/** The latest lines, for "Report a Problem". An output channel cannot be read back. */
const recent: string[] = [];

export function log(message: string) {
  channel ??= vscode.window.createOutputChannel("DSA Playback");
  const entry = `[${new Date().toISOString()}] ${message}`;
  channel.appendLine(entry);

  recent.push(...entry.split(/\r?\n/));
  if (recent.length > REPORTED_LOG_LINES) recent.splice(0, recent.length - REPORTED_LOG_LINES);
}

export function recentLog(): string[] {
  return [...recent];
}

export function showLog() {
  channel ??= vscode.window.createOutputChannel("DSA Playback");
  channel.show(true);
}
