import assert from "node:assert/strict";
import * as path from "path";
import * as vscode from "vscode";
import type { TestApi } from "../../extension";

// Runs inside a real VS Code. Each check drives the real command against the real engine.

const repository = path.resolve(__dirname, "../../../..");

async function waitFor<T>(read: () => T | undefined, what: string, timeoutMs = 120_000): Promise<T> {
  const started = Date.now();
  for (;;) {
    const value = read();
    if (value !== undefined) return value;
    if (Date.now() - started > timeoutMs) throw new Error(`Timed out waiting for ${what}`);
    await new Promise(resolve => setTimeout(resolve, 200));
  }
}

async function open(relative: string): Promise<vscode.TextDocument> {
  const document = await vscode.workspace.openTextDocument(path.join(repository, relative));
  await vscode.window.showTextDocument(document);
  return document;
}

const checks: [string, (api: TestApi) => Promise<void>][] = [
  ["the measure command runs a LeetCode solution end to end", async api => {
    const document = await open("samples/leetcode/DedupeWithList.cs");
    await vscode.commands.executeCommand("dsaViz.measure");

    const response = await waitFor(() => api.lastResponse(), "a measurement");
    assert.equal(response.primary?.status, "Completed");
    assert.equal(response.primary?.empirical.best, "Quadratic");
    assert.ok(response.primary?.derivation?.summary.includes("List<T>.Contains"), "the derivation names the hidden cost");

    // Annotations land on the user's own file.
    await waitFor(() => api.linesDecoratedIn(document.uri) || undefined, "annotations on the file", 10_000);
  }],

  ["editing the file clears its annotations", async api => {
    const document = await open("samples/leetcode/DedupeWithList.cs");
    assert.ok(api.linesDecoratedIn(document.uri) > 0, "precondition: the file is annotated");

    const edit = new vscode.WorkspaceEdit();
    edit.insert(document.uri, new vscode.Position(0, 0), "// edited\n");
    await vscode.workspace.applyEdit(edit);
    await waitFor(() => (api.linesDecoratedIn(document.uri) === 0 ? true : undefined), "annotations to clear", 5_000);

    await vscode.commands.executeCommand("workbench.action.files.revert");
  }],

  ["the step view records a run when the panel asks for it", async api => {
    await api.sendPanelMessage({ type: "steps", size: 6 });
    const steps = await waitFor(() => api.lastSteps(), "a step recording");
    assert.equal(steps.status, "Completed");
    assert.ok(steps.steps.length > 0);
    assert.ok(steps.steps.some(s => s.variables.some(v => v.name === "seen")), "the steps carry the solution's variables");
  }],

  ["a wrong engine path is reported, not hung on", async api => {
    const settings = vscode.workspace.getConfiguration("dsaViz");
    await settings.update("enginePath", "/nonexistent/DsaViz.Engine", vscode.ConfigurationTarget.Global);
    try {
      await open("samples/leetcode/TwoSum.cs");
      await vscode.commands.executeCommand("dsaViz.measure");
      const error = await waitFor(() => api.lastError()?.includes("does not exist") ? api.lastError() : undefined, "an error", 10_000);
      assert.match(error, /enginePath/);
    } finally {
      await settings.update("enginePath", undefined, vscode.ConfigurationTarget.Global);
    }
  }]
];

export async function run(): Promise<void> {
  const extension = vscode.extensions.getExtension<TestApi>("local.dsa-visualizer");
  assert.ok(extension, "the extension is installed in the development host");
  const api = await extension.activate();

  const failures: string[] = [];
  for (const [name, check] of checks) {
    try {
      await check(api);
      console.log(`  ✔ ${name}`);
    } catch (error) {
      failures.push(name);
      console.log(`  ✖ ${name}\n      ${error instanceof Error ? error.stack ?? error.message : String(error)}`);
    }
  }

  console.log(`\n  ${checks.length - failures.length} passed, ${failures.length} failed`);
  if (failures.length > 0) throw new Error(`${failures.length} integration check(s) failed`);
}
