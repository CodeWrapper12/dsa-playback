import * as vscode from "vscode";
import { annotationText, classLabel, fastest, formatBytes, formatCount, heatIntensity } from "./costFormat";
import { Derivation, LibraryCall, StatementCost, StatementGrowth, TraceResult } from "./types";

const HOT = "rgba(224, 108, 43, 0.85)";

/**
 * The editor column to show the student's file in, never the panel's own: one already
 * showing the file, else a group holding it as a tab, else any other group. Given no
 * column, VS Code uses the active group — the panel's, once the student has clicked in
 * it — and the file would open as a tab over the panel, hiding the run.
 */
export function codeColumn(file: string, panel: vscode.ViewColumn | undefined): vscode.ViewColumn {
  const uri = vscode.Uri.file(file).toString();
  const shown = vscode.window.visibleTextEditors.find(e =>
    e.document.uri.toString() === uri && e.viewColumn !== undefined && e.viewColumn !== panel);
  if (shown?.viewColumn) return shown.viewColumn;

  const groups = vscode.window.tabGroups.all.filter(g => g.viewColumn !== panel);
  const holding = groups.find(g => g.tabs.some(t => t.input instanceof vscode.TabInputText && t.input.uri.toString() === uri));
  return (holding ?? groups[0])?.viewColumn ?? vscode.ViewColumn.Beside;
}

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

  private readonly step: vscode.TextEditorDecorationType;

  private decoratedUri: string | undefined;
  private decoratedLines = 0;
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

    // The line a step-through is currently on, so scrubbing in the panel moves through
    // the real file — the "debugging" half of the original request.
    this.step = vscode.window.createTextEditorDecorationType({
      isWholeLine: true,
      backgroundColor: new vscode.ThemeColor("editor.stackFrameHighlightBackground"),
      borderColor: new vscode.ThemeColor("editorInfo.foreground"),
      borderStyle: "solid",
      borderWidth: "0 0 0 3px",
      overviewRulerColor: new vscode.ThemeColor("editorInfo.foreground"),
      overviewRulerLane: vscode.OverviewRulerLane.Left
    });

    this.watcher = vscode.workspace.onDidChangeTextDocument(event => {
      if (event.document.uri.toString() === this.decoratedUri && event.contentChanges.length) {
        this.clear();
      }
    });
  }

  async apply(file: string, detail: TraceResult, measuredAt: number | null, derivation: Derivation | null | undefined,
    panel: vscode.ViewColumn | undefined): Promise<void> {
    const document = await vscode.workspace.openTextDocument(file);
    const editor = await vscode.window.showTextDocument(document, {
      viewColumn: codeColumn(file, panel),
      preserveFocus: true,
      preview: false
    });

    const statementsByLine = new Map<number, StatementCost[]>();
    for (const statement of detail.statements) {
      const bucket = statementsByLine.get(statement.line);
      if (bucket) bucket.push(statement);
      else statementsByLine.set(statement.line, [statement]);
    }

    const growthByLine = new Map<number, StatementGrowth[]>();
    for (const growth of derivation?.statements ?? []) {
      const bucket = growthByLine.get(growth.line);
      if (bucket) bucket.push(growth);
      else growthByLine.set(growth.line, [growth]);
    }

    const callsByLine = new Map<number, LibraryCall[]>();
    for (const call of detail.libraryCalls ?? []) {
      const bucket = callsByLine.get(call.line);
      if (bucket) bucket.push(call);
      else callsByLine.set(call.line, [call]);
    }

    // Shaded by total cost, not execution count: a List.Contains line that runs 1,000
    // times and scans 499,500 elements is the hottest line in the file, and shading it by
    // its 1,000 would make it look cheap — the exact misreading this tool exists to fix.
    const costOf = (line: { hits: number; libraryWork?: number }) => line.hits + (line.libraryWork ?? 0);
    const maxCost = Math.max(1, ...detail.lines.map(costOf));

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
        renderOptions: {
          after: {
            contentText: annotationText(line.hits, line.bytesAllocated, line.libraryWork ?? 0,
              classLabel(fastest((growthByLine.get(line.line) ?? []).map(g => g.cost)) ?? ""))
          }
        },
        hoverMessage: this.hover(line.line, statementsByLine.get(line.line) ?? [],
          callsByLine.get(line.line) ?? [], growthByLine.get(line.line) ?? [], measuredAt, detail)
      });

      const intensity = heatIntensity(costOf(line), maxCost);
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
    this.decoratedLines = costs.length;
  }

  private hover(
    line: number,
    statements: StatementCost[],
    calls: LibraryCall[],
    growth: StatementGrowth[],
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

    if (growth.length > 0) {
      // The per-statement derivation: how often it runs, what it costs, what it allocates.
      markdown.appendMarkdown("\n**How this line grows**\n\n");
      for (const g of growth) {
        const runs = g.runsFormula ? `runs ${g.runsFormula} times` : "run count does not fit a standard curve";
        const cost = g.cost !== g.runs && g.costFormula ? `, costs ${g.costFormula}` : "";
        const memory = g.memoryFormula ? `, allocates ${g.memoryFormula} in total` : "";
        markdown.appendMarkdown(`- \`${g.snippet.replace(/`/g, "'")}\` ${runs}${cost}${memory}\n`);
      }
    }

    if (calls.length > 0) {
      // Where the hidden work comes from, and why: the per-call cost times the calls.
      markdown.appendMarkdown("\n**Library calls on this line**\n\n");
      for (const call of calls) {
        markdown.appendMarkdown(
          `- \`${call.member}\` is ${call.cost} per call: ${formatCount(call.calls)} calls did ` +
          `${formatCount(call.work)} units of work between them.\n`);
      }
    }

    markdown.appendMarkdown(
      `\n---\n\nMeasured at n = ${measuredAt === null ? "—" : formatCount(measuredAt)}. ` +
      detail.memoryModel);

    return markdown;
  }

  /** Highlights the line a step-through is on and brings it into view without taking focus. */
  async showStep(file: string, line: number, panel: vscode.ViewColumn | undefined): Promise<void> {
    const document = await vscode.workspace.openTextDocument(file);
    const editor = await vscode.window.showTextDocument(document,
      { viewColumn: codeColumn(file, panel), preserveFocus: true, preview: false });
    const index = Math.max(0, Math.min(line - 1, document.lineCount - 1));
    const range = document.lineAt(index).range;
    editor.setDecorations(this.step, [range]);
    editor.revealRange(range, vscode.TextEditorRevealType.InCenterIfOutsideViewport);
  }

  clearStep(): void {
    for (const editor of vscode.window.visibleTextEditors) editor.setDecorations(this.step, []);
  }

  clear(): void {
    for (const editor of vscode.window.visibleTextEditors) {
      editor.setDecorations(this.costText, []);
      editor.setDecorations(this.heat, []);
      editor.setDecorations(this.step, []);
    }
    this.decoratedUri = undefined;
    this.decoratedLines = 0;
  }

  /** How many lines of a file currently carry cost annotations. For integration tests. */
  linesDecoratedIn(uri: vscode.Uri): number {
    return this.decoratedUri === uri.toString() ? this.decoratedLines : 0;
  }

  dispose(): void {
    this.watcher.dispose();
    this.costText.dispose();
    this.heat.dispose();
    this.step.dispose();
  }
}
