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
    await vscode.commands.executeCommand("dsaPlayback.measure");

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
    await vscode.commands.executeCommand("dsaPlayback.visualize", vscode.Uri.file(file), "Solution.TwoSum");
    const steps = await waitFor(() => (api.lastSteps() !== before ? api.lastSteps() : undefined), "a recorded run");

    assert.equal(steps.status, "Completed");
    assert.equal(steps.inputs[0].literal, "[2,7,11,15]");
    assert.equal(steps.check?.matches, true);
    assert.equal(api.lastError(), undefined, "no error was shown");
  }],

  ["a typed value is used, and still used when the method is opened again", async api => {
    const file = path.join(repository, "extension/samples/csharp/TwoSumSorted.cs");
    let before = api.lastSteps();
    await vscode.commands.executeCommand("dsaPlayback.visualize", vscode.Uri.file(file), "Solution.TwoSum");
    await waitFor(() => (api.lastSteps() !== before ? true : undefined), "a first run");

    before = api.lastSteps();
    await api.sendPanelMessage({ type: "run", values: [null, "26"] });
    const typed = await waitFor(() => (api.lastSteps() !== before ? api.lastSteps() : undefined), "a run with the typed value");
    assert.equal(typed.inputs[1].source, "Typed");
    assert.equal(typed.returnValue, "[3,4]");

    before = api.lastSteps();
    await vscode.commands.executeCommand("dsaPlayback.visualize", vscode.Uri.file(file), "Solution.TwoSum");
    const reopened = await waitFor(() => (api.lastSteps() !== before ? api.lastSteps() : undefined), "a run on reopening");
    assert.equal(reopened.inputs[1].literal, "26");

    // Back to the values in the code, which also clears what was saved.
    before = api.lastSteps();
    await api.sendPanelMessage({ type: "run", example: 0 });
    const fromCode = await waitFor(() => (api.lastSteps() !== before ? api.lastSteps() : undefined), "a run from the code");
    assert.equal(fromCode.inputs[1].source, "Example");
  }],

  ["a recorded run carries objects, their identity, and what each call returned", async api => {
    const file = path.join(repository, "extension/samples/csharp/LevelOrder.cs");
    const before = api.lastSteps();
    await vscode.commands.executeCommand("dsaPlayback.visualize", vscode.Uri.file(file), "Solution.LevelOrder");
    const steps = await waitFor(() => (api.lastSteps() !== before ? api.lastSteps() : undefined), "a recorded run");

    const kinds = new Set(steps.steps.flatMap(s => (s.heap ?? []).map(o => o.kind)));
    for (const kind of ["tree", "queue", "list"]) assert.ok(kinds.has(kind), `a ${kind} is recorded`);
    assert.equal(steps.returns.at(-1)?.value.text.startsWith("List"), true, "the method's return is recorded");
    assert.ok(steps.steps.every(s => typeof s.allocated === "number"), "every step carries the memory so far");
  }],

  ["a Python solution gets the buttons and visualizes from its example", async api => {
    const file = path.join(repository, "extension/samples/python/level_order.py");
    const document = await vscode.workspace.openTextDocument(file);
    await vscode.window.showTextDocument(document);
    assert.equal(document.languageId, "python");
    const titles = (await api.lenses(document)).map(lens => lens.command?.title);
    assert.deepEqual(titles, ["$(play) Visualize", "$(graph) Complexity"]);

    const before = api.lastSteps();
    await vscode.commands.executeCommand("dsaPlayback.visualize", document.uri, "Solution.levelOrder");
    const steps = await waitFor(() => (api.lastSteps() !== before ? api.lastSteps() : undefined), "a recorded Python run");
    assert.equal(steps.status, "Completed");
    assert.equal(steps.check?.matches, true);
  }],

  ["the ▶ in the editor title bar works, though VS Code passes it an editor group, not a method", async api => {
    const file = path.join(repository, "extension/samples/csharp/TwoSumSorted.cs");
    const document = await vscode.workspace.openTextDocument(file);
    await vscode.window.showTextDocument(document);

    const before = api.lastSteps();
    // Exactly what VS Code passes from an editor/title menu item: the file, then the group.
    await vscode.commands.executeCommand("dsaPlayback.visualize", document.uri, { groupId: 1 });
    // A run of this file: one still finishing from the check before must not be taken for it.
    const steps = await waitFor(() => (api.lastSteps() !== before && api.lastStepsFile() === file ? api.lastSteps() : undefined),
      "a run from the title bar");
    assert.equal(steps.status, "Completed");
    assert.equal(steps.check?.matches, true);
  }],

  ["a method taking a double runs from its example, with Visualize but no Complexity button", async api => {
    const folder = fs.mkdtempSync(path.join(os.tmpdir(), "dsaviz-example-only-"));
    const file = path.join(folder, "Average.cs");
    fs.writeFileSync(file, [
      "public class Solution",
      "{",
      "    // Input: nums = [1.5,2.5], scale = 2.0",
      "    // Output: 4.0",
      "    public double Average(double[] nums, double scale) => (nums[0] + nums[1]) / 2 * scale;",
      "}"
    ].join("\n"));

    const document = await vscode.workspace.openTextDocument(file);
    await vscode.window.showTextDocument(document);
    assert.deepEqual((await api.lenses(document)).map(lens => lens.command?.title), ["$(play) Visualize"]);

    const before = api.lastSteps();
    await vscode.commands.executeCommand("dsaPlayback.visualize", vscode.Uri.file(file), "Solution.Average");
    const steps = await waitFor(() => (api.lastSteps() !== before ? api.lastSteps() : undefined), "a recorded run");
    assert.equal(steps.status, "Completed");
    assert.equal(steps.check?.matches, true, `returned ${steps.returnValue}`);
  }],

  ["Run all examples lists every example's result", async api => {
    await vscode.commands.executeCommand("dsaPlayback.visualize",
      vscode.Uri.file(path.join(repository, "samples/TwoSumExamples.cs")), "Solution.TwoSum");
    await waitFor(() => api.lastSteps(), "a recorded run");

    await api.sendPanelMessage({ type: "runExamples" });
    const report = await waitFor(() => api.lastExamples(), "every example's result");
    assert.deepEqual(report.runs.map(run => run.matches), [true, false, null]);
    assert.equal(report.runs[2].status, "RuntimeError");
  }],

  ["a problem report carries the versions and the recent log, never the home folder", async api => {
    const url = new URL(api.problemReportUrl());
    const body = url.searchParams.get("body") ?? "";
    assert.equal(url.origin + url.pathname, "https://github.com/CodeWrapper12/dsa-playback/issues/new");
    assert.match(body, /DSA Playback \d+\.\d+\.\d+/);
    assert.match(body, /engine --job -/, "the log lines of the runs above");
    assert.ok(!body.includes(os.homedir()), "the home folder is never included");
  }],

  ["a C# file made by the LeetCode extension gets its buttons and runs its example", async api => {
    // No using lines, and TreeNode left commented out, as LeetCode's template has it.
    const document = await open("samples/leetcode-extension/104.maximum-depth-of-binary-tree.cs");
    assert.deepEqual((await api.lenses(document)).map(lens => lens.command?.title), ["$(play) Visualize", "$(graph) Complexity"]);

    const before = api.lastSteps();
    await vscode.commands.executeCommand("dsaPlayback.visualize", document.uri, "Solution.MaxDepth");
    const steps = await waitFor(() => (api.lastSteps() !== before ? api.lastSteps() : undefined), "a recorded run");
    assert.equal(steps.status, "Completed", steps.errors.join("; "));
    assert.equal(steps.check?.matches, true);
  }],

  ["a design problem gets one ▶ Visualize on its class, and its calls run and are checked", async api => {
    const document = await open("samples/design/LRUCache.cs");
    assert.deepEqual((await api.lenses(document)).map(lens => lens.command?.title), ["$(play) Visualize"]);

    const before = api.lastSteps();
    await vscode.commands.executeCommand("dsaPlayback.visualize", document.uri, "LRUCache");
    const steps = await waitFor(() => (api.lastSteps() !== before ? api.lastSteps() : undefined), "a recorded run");
    assert.equal(steps.status, "Completed", steps.errors.join("; "));
    assert.equal(steps.check?.matches, true);
    assert.equal(steps.calls.length, 10);
  }],

  ["Explain this step sends nothing while off, and explains why when no Copilot is available", async api => {
    const settings = vscode.workspace.getConfiguration("dsaPlayback");
    await vscode.commands.executeCommand("dsaPlayback.visualize",
      vscode.Uri.file(path.join(repository, "extension/samples/csharp/TwoSumSorted.cs")), "Solution.TwoSum");
    await waitFor(() => api.lastSteps(), "a recorded run");

    await api.sendPanelMessage({ type: "explain", trace: -1, index: 0 });
    assert.match(String(api.lastExplanation()?.error), /turned off/);

    await settings.update("explainWithAI", true, vscode.ConfigurationTarget.Global);
    try {
      await api.sendPanelMessage({ type: "explain", trace: -1, index: 0 });
      assert.match(String(api.lastExplanation()?.error), /replaced/, "an answer is only for the run on screen");
      // The test editor has no Copilot sign-in: the real API is asked, and the answer says so.
      const traceId = await waitFor(() => api.currentTraceId(), "the run's id", 5_000);
      await api.sendPanelMessage({ type: "explain", trace: traceId, index: 0 });
      const answer = await waitFor(() => api.lastExplanation()?.done ? api.lastExplanation() : undefined, "an answer", 30_000);
      assert.match(String(answer.error), /Copilot|language-model API/);
    } finally {
      await settings.update("explainWithAI", undefined, vscode.ConfigurationTarget.Global);
    }
  }],

  ["a C++ file gets its buttons, runs its example and is measured with the compiler on this machine", async api => {
    const document = await open("samples/cpp/two_sum.cpp");
    assert.equal(document.languageId, "cpp");
    assert.deepEqual((await api.lenses(document)).map(lens => lens.command?.title), ["$(play) Visualize", "$(graph) Complexity"]);

    const before = api.lastSteps();
    await vscode.commands.executeCommand("dsaPlayback.visualize", document.uri, "Solution.twoSum");
    const steps = await waitFor(() => (api.lastSteps() !== before ? api.lastSteps() : undefined), "a recorded run");
    assert.equal(steps.status, "Completed", steps.errors.join("; "));
    assert.equal(steps.check?.matches, true);

    const measured = await open("samples/cpp/dedupe_with_find.cpp");
    const previous = api.lastResponse();
    await vscode.commands.executeCommand("dsaPlayback.measure", measured.uri, "Solution.countDistinct");
    const response = await waitFor(() => (api.lastResponse() !== previous ? api.lastResponse() : undefined), "a measurement");
    assert.equal(response.primary?.status, "Completed", response.primary?.errors.join("; "));
    assert.ok(["Quadratic", "Unknown"].includes(response.primary!.empirical.best), response.primary!.empirical.best);
    assert.ok(api.linesDecoratedIn(measured.uri) > 0, "costs are written beside the C++ lines");
  }],

  ["a Rust file gets its buttons, runs its example and is measured with the rustc on this machine", async api => {
    const document = await open("samples/rust/two_sum.rs");
    assert.equal(document.languageId, "rust");
    assert.deepEqual((await api.lenses(document)).map(lens => lens.command?.title), ["$(play) Visualize", "$(graph) Complexity"]);

    const before = api.lastSteps();
    await vscode.commands.executeCommand("dsaPlayback.visualize", document.uri, "Solution.two_sum");
    const steps = await waitFor(() => (api.lastSteps() !== before ? api.lastSteps() : undefined), "a recorded run", 120_000);
    assert.equal(steps.status, "Completed", steps.errors.join("; "));
    assert.equal(steps.check?.matches, true);

    const measured = await open("samples/rust/dedupe_with_contains.rs");
    const previous = api.lastResponse();
    await vscode.commands.executeCommand("dsaPlayback.measure", measured.uri, "Solution.count_distinct");
    const response = await waitFor(() => (api.lastResponse() !== previous ? api.lastResponse() : undefined), "a measurement", 120_000);
    assert.equal(response.primary?.status, "Completed", response.primary?.errors.join("; "));
    assert.ok(api.linesDecoratedIn(measured.uri) > 0, "costs are written beside the Rust lines");
  }],

  ["the walkthrough opens an editable copy of its sample", async () => {
    await vscode.commands.executeCommand("dsaPlayback.openSample", "LevelOrder.cs");
    const opened = vscode.window.activeTextEditor?.document.fileName ?? "";
    assert.ok(opened.endsWith("LevelOrder.cs"));
    assert.ok(!opened.startsWith(path.join(repository, "extension")), "not the file inside the extension itself");
  }],

  ["a wrong engine path is reported, not hung on", async api => {
    const settings = vscode.workspace.getConfiguration("dsaPlayback");
    await settings.update("enginePath", "/nonexistent/DsaViz.Engine", vscode.ConfigurationTarget.Global);
    try {
      await open("samples/leetcode/TwoSum.cs");
      await vscode.commands.executeCommand("dsaPlayback.measure");
      const error = await waitFor(() => api.lastError()?.includes("does not exist") ? api.lastError() : undefined, "an error", 10_000);
      assert.match(error, /enginePath/);
    } finally {
      await settings.update("enginePath", undefined, vscode.ConfigurationTarget.Global);
    }
  }]
];

export async function run(): Promise<void> {
  const extension = vscode.extensions.getExtension<TestApi>("codewrapper12.dsa-playback");
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
