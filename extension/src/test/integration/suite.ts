import assert from "node:assert/strict";
import * as fs from "fs";
import * as os from "os";
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

  ["the ▶ Visualize button appears above every method that can run", async api => {
    const document = await open("extension/samples/csharp/TwoSumSorted.cs");
    const titles = (await api.lenses(document)).map(lens => lens.command?.title);
    assert.deepEqual(titles, ["$(play) Visualize", "$(graph) Complexity"]);
  }],

  ["a solution outside the open folder visualizes, using the example in its comment", async api => {
    // The bug a student met first: the engine was only looked for inside the open folder.
    const folder = fs.mkdtempSync(path.join(os.tmpdir(), "dsaviz-elsewhere-"));
    const file = path.join(folder, "TwoSum.cs");
    fs.copyFileSync(path.join(repository, "extension/samples/csharp/TwoSumSorted.cs"), file);

    const before = api.lastSteps();
    await vscode.commands.executeCommand("dsaViz.visualize", vscode.Uri.file(file), "Solution.TwoSum");
    const steps = await waitFor(() => (api.lastSteps() !== before ? api.lastSteps() : undefined), "a recorded run");

    assert.equal(steps.status, "Completed");
    assert.equal(steps.inputs[0].literal, "[2,7,11,15]");
    assert.equal(steps.check?.matches, true);
    assert.equal(api.lastError(), undefined, "no error was shown");
  }],

  ["a typed value is used, and still used when the method is opened again", async api => {
    const file = path.join(repository, "extension/samples/csharp/TwoSumSorted.cs");
    let before = api.lastSteps();
    await vscode.commands.executeCommand("dsaViz.visualize", vscode.Uri.file(file), "Solution.TwoSum");
    await waitFor(() => (api.lastSteps() !== before ? true : undefined), "a first run");

    before = api.lastSteps();
    await api.sendPanelMessage({ type: "run", values: [null, "26"] });
    const typed = await waitFor(() => (api.lastSteps() !== before ? api.lastSteps() : undefined), "a run with the typed value");
    assert.equal(typed.inputs[1].source, "Typed");
    assert.equal(typed.returnValue, "[3,4]");

    before = api.lastSteps();
    await vscode.commands.executeCommand("dsaViz.visualize", vscode.Uri.file(file), "Solution.TwoSum");
    const reopened = await waitFor(() => (api.lastSteps() !== before ? api.lastSteps() : undefined), "a run on reopening");
    assert.equal(reopened.inputs[1].literal, "26");

    // Back to the values in the code, which also clears what was saved.
    before = api.lastSteps();
    await api.sendPanelMessage({ type: "run", example: 0 });
    const fromCode = await waitFor(() => (api.lastSteps() !== before ? api.lastSteps() : undefined), "a run from the code");
    assert.equal(fromCode.inputs[1].source, "Example");
  }],

  ["the walkthrough opens an editable copy of its sample", async () => {
    await vscode.commands.executeCommand("dsaViz.openSample", "LevelOrder.cs");
    const opened = vscode.window.activeTextEditor?.document.fileName ?? "";
    assert.ok(opened.endsWith("LevelOrder.cs"));
    assert.ok(!opened.startsWith(path.join(repository, "extension")), "not the file inside the extension itself");
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
