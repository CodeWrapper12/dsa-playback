import * as vscode from "vscode";
import { EngineCancelledError, listEntryPoints } from "./engine";
import { log } from "./log";
import { EntryPoint } from "./types";

/**
 * Puts "▶ Visualize" and "Complexity" above every method the engine can run, and the
 * reason above every one it cannot. This is the whole of getting started: no command
 * palette, no picking a method from a list.
 */
export class EntryPointLenses implements vscode.CodeLensProvider {
  private readonly changed = new vscode.EventEmitter<void>();
  readonly onDidChangeCodeLenses = this.changed.event;

  /** Entries per document version, so moving the cursor around never re-runs the engine. */
  private readonly cache = new Map<string, { version: number; entries: EntryPoint[] }>();

  forget(uri: vscode.Uri) {
    this.cache.delete(uri.toString());
  }

  refresh() {
    this.cache.clear();
    this.changed.fire();
  }

  async provideCodeLenses(document: vscode.TextDocument, token: vscode.CancellationToken): Promise<vscode.CodeLens[]> {
    const entries = await this.entriesFor(document, token);
    if (!entries) return [];

    return entries.flatMap(entry => {
      const line = Math.max(0, Math.min(entry.line - 1, document.lineCount - 1));
      const range = document.lineAt(line).range;

      if (!entry.measurable) {
        return [new vscode.CodeLens(range, {
          title: `$(info) Can't visualize ${entry.name}: ${entry.reason}`,
          command: ""
        })];
      }

      const target = [document.uri, entry.qualified];
      return [
        new vscode.CodeLens(range, {
          title: "$(play) Visualize",
          tooltip: `Watch ${entry.name} run, one step at a time`,
          command: "dsaViz.visualize",
          arguments: target
        }),
        new vscode.CodeLens(range, {
          title: "$(graph) Complexity",
          tooltip: `How ${entry.name}'s time and memory grow with the input`,
          command: "dsaViz.measure",
          arguments: target
        })
      ];
    });
  }

  private async entriesFor(document: vscode.TextDocument, token: vscode.CancellationToken) {
    const key = document.uri.toString();
    const cached = this.cache.get(key);
    if (cached?.version === document.version) return cached.entries;

    const abort = new AbortController();
    const subscription = token.onCancellationRequested(() => abort.abort());
    try {
      const entries = await listEntryPoints(document.fileName, document.getText(), abort.signal);
      this.cache.set(key, { version: document.version, entries });
      return entries;
    } catch (error) {
      // A missing button is the right outcome while the engine cannot run, but never a
      // silent one: the reason is logged, and running a command shows it to the student.
      if (!(error instanceof EngineCancelledError)) {
        log(`Could not list methods in ${document.fileName}: ${error instanceof Error ? error.message : String(error)}`);
      }
      return undefined;
    } finally {
      subscription.dispose();
    }
  }
}
