import * as vscode from "vscode";
import { annotationText, formatBytes, formatCount, heatIntensity } from "./costFormat";
import { StatementCost, TraceResult } from "./types";

const HOT = "rgba(224, 108, 43, 0.85)";

/**
 * Lays measured costs over the user's actual file rather than a copy of it.
 *
 * The costs stop being true the moment the file is edited — line 10 is no longer the
 * line that ran 749,625 times once a line is inserted above it — so any edit clears
 * them. A stale annotation is worse than none, because it looks authoritative.
 */
export class CostDecorations {
  private readonly costText: vscode.TextEditorDecorationType;
  private readonly heat: vscode.TextEditorDecorationType;

  private decoratedUri: string | undefined;
  private readonly watcher: vscode.Disposable;

  constructor() {
    this.costText = vscode.window.createTextEditorDecorationType({
      after: {
        margin: "0 0 0 2.5em",
        color: new vscode.ThemeColor("editorCodeLens.foreground")
      },
      rangeBehavior: vscode.DecorationRangeBehavior.ClosedClosed
    });

    this.heat = vscode.window.createTextEditorDecorationType({
      isWholeLine: true,
      overviewRulerLane: vscode.OverviewRulerLane.Right,
      rangeBehavior: vscode.DecorationRangeBehavior.ClosedClosed
    });

    this.watcher = vscode.workspace.onDidChangeTextDocument(event => {
      if (event.document.uri.toString() === this.decoratedUri && event.contentChanges.length) {
        this.clear();
      }
    });
  }

  async apply(file: string, detail: TraceResult, measuredAt: number | null): Promise<void> {
    const document = await vscode.workspace.openTextDocument(file);
    const editor = await vscode.window.showTextDocument(document, {
      preserveFocus: true,
      preview: false
    });

    const statementsByLine = new Map<number, StatementCost[]>();
    for (const statement of detail.statements) {
      const bucket = statementsByLine.get(statement.line);
      if (bucket) bucket.push(statement);
      else statementsByLine.set(statement.line, [statement]);
    }

    const maxHits = Math.max(1, ...detail.lines.map(line => line.hits));

    const costs: vscode.DecorationOptions[] = [];
    const heat: vscode.DecorationOptions[] = [];

    for (const line of detail.lines) {
      // Engine lines are 1-based; the editor's API is 0-based, and a measurement can
      // outlive a small edit that shortened the file.
      const index = line.line - 1;
      if (index < 0 || index >= document.lineCount) continue;

      const range = document.lineAt(index).range;

      costs.push({
        range,
        renderOptions: { after: { contentText: annotationText(line.hits, line.bytesAllocated) } },
        hoverMessage: this.hover(line.line, statementsByLine.get(line.line) ?? [], measuredAt, detail)
      });

      const intensity = heatIntensity(line.hits, maxHits);
      if (intensity > 0.05) {
        heat.push({
          range,
          renderOptions: { backgroundColor: `rgba(224, 108, 43, ${(intensity * 0.16).toFixed(3)})` },
          ...(intensity > 0.75 ? { overviewRulerColor: HOT } : {})
        } as vscode.DecorationOptions);
      }
    }

    editor.setDecorations(this.costText, costs);
    editor.setDecorations(this.heat, heat);
    this.decoratedUri = document.uri.toString();
  }

  private hover(
    line: number,
    statements: StatementCost[],
    measuredAt: number | null,
    detail: TraceResult): vscode.MarkdownString {

    const markdown = new vscode.MarkdownString();
    markdown.isTrusted = false;

    if (statements.length > 1) {
      // The reason this hover exists: a line total is a sum, and on a line like
      // `if (a > b) swap;` the split between the test and the work is the question.
      markdown.appendMarkdown(`**${statements.length} statements on line ${line}**\n\n`);
      markdown.appendMarkdown("| executions | memory | statement |\n|---:|---:|:---|\n");
      for (const statement of statements) {
        markdown.appendMarkdown(
          `| ${formatCount(statement.hits)} | ${formatBytes(statement.bytesAllocated) || "0"} | \`${
            statement.snippet.replace(/\|/g, "\\|")}\` |\n`);
      }
    } else if (statements.length === 1) {
      markdown.appendMarkdown(
        `\`${statements[0].snippet}\`\n\n${formatCount(statements[0].hits)} executions`);
      const bytes = formatBytes(statements[0].bytesAllocated);
      if (bytes) markdown.appendMarkdown(`, ${bytes} allocated`);
      markdown.appendMarkdown("\n");
    }

    markdown.appendMarkdown(
      `\n---\n\nMeasured at n = ${measuredAt === null ? "—" : formatCount(measuredAt)}. ` +
      detail.memoryModel);

    return markdown;
  }

  clear(): void {
    for (const editor of vscode.window.visibleTextEditors) {
      editor.setDecorations(this.costText, []);
      editor.setDecorations(this.heat, []);
    }
    this.decoratedUri = undefined;
  }

  dispose(): void {
    this.watcher.dispose();
    this.costText.dispose();
    this.heat.dispose();
  }
}
