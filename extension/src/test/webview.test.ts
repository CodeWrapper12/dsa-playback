import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import * as fs from "node:fs";
import * as path from "node:path";
import { test } from "node:test";
import { JSDOM, VirtualConsole } from "jsdom";

// The panel's script is plain JavaScript, so neither the compiler nor `node --check`
// can see a runtime error in it. One reached the rendered panel during development —
// a variable read before its declaration, which crashed every result. These tests load
// the real script into a DOM, feed it real engine output, and click through every
// control, so that class of mistake fails here instead of in the editor.

const root = path.resolve(__dirname, "../../..");
const engine = path.join(root, "engine/DsaViz.Engine/bin/Debug/net8.0/DsaViz.Engine");
const script = fs.readFileSync(path.join(root, "extension/media/main.js"), "utf8");

function measure(request: Record<string, unknown>): unknown {
  // Fail rather than skip: a skipped test reports green while testing nothing.
  assert.ok(fs.existsSync(engine), `Engine not built at ${engine}. Run: dotnet build engine/DsaViz.sln`);
  const output = execFileSync(engine, ["--job", "-"], { input: JSON.stringify(request), encoding: "utf8", timeout: 120_000 });
  return JSON.parse(output);
}

function sample(name: string) {
  return path.join(root, "samples", name);
}

/**
 * Loads the panel script into a DOM. With `connected`, it gets a stand-in for VS Code's
 * webview API that records every message the panel sends to the extension.
 */
function load(connected = false) {
  const errors: string[] = [];
  const posted: Record<string, unknown>[] = [];
  const virtualConsole = new VirtualConsole();
  virtualConsole.on("jsdomError", error => errors.push(String(error.stack ?? error)));
  virtualConsole.on("error", (...args: unknown[]) => errors.push(args.map(String).join(" ")));

  const dom = new JSDOM('<div id="app"></div>', { runScripts: "outside-only", virtualConsole });
  if (connected) {
    // Copied out as plain JSON, as VS Code would: objects made inside the DOM have its own
    // prototypes and never deep-equal one made here.
    (dom.window as unknown as { acquireVsCodeApi(): unknown }).acquireVsCodeApi =
      () => ({ postMessage: (message: Record<string, unknown>) => posted.push(JSON.parse(JSON.stringify(message))) });
  }
  dom.window.eval(script);
  return { dom, errors, posted };
}

/** Renders, then clicks every tab, metric and axis toggle, re-rendering each time. */
function exercise(response: unknown, entry: string, compareFile?: string) {
  const { dom, errors } = load();
  const window = dom.window as unknown as { __render(r: unknown, q: unknown): void };
  const document = dom.window.document;

  window.__render(response, { entry, compareFile });
  assert.deepEqual(errors, [], "initial render");

  const tabs = Array.from(document.querySelectorAll<HTMLElement>(".tab")).map(t => t.dataset.tab!);
  for (const tab of tabs) {
    document.querySelector<HTMLElement>(`.tab[data-tab="${tab}"]`)!.click();
    assert.deepEqual(errors, [], `tab ${tab}`);

    const metrics = Array.from(document.querySelectorAll<HTMLElement>(".metric")).map(m => m.dataset.metric!);
    for (const metric of metrics) {
      document.querySelector<HTMLElement>(`.metric[data-metric="${metric}"]`)!.click();
      assert.deepEqual(errors, [], `tab ${tab}, metric ${metric}`);
      const toggle = document.getElementById("toggle-log");
      toggle?.click();
      toggle && document.getElementById("toggle-log")?.click();
      assert.deepEqual(errors, [], `tab ${tab}, metric ${metric}, log axes`);
    }
  }

  return { document, tabs };
}

test("a single solution renders every tab without error", () => {
  const { document, tabs } = exercise(measure({ file: sample("SortParamSample.cs"), entry: "SortParamSample.Sort" }), "Sort");
  assert.deepEqual(tabs, ["steps", "cost", "growth"]);

  // The derivation and the per-statement formulas live on the cost tab.
  document.querySelector<HTMLElement>('.tab[data-tab="cost"]')!.click();
  assert.match(document.body.textContent ?? "", /0\.5·n²/);
});

test("a comparison renders every tab and metric without error", () => {
  const { tabs } = exercise(measure({
    file: sample("leetcode/TwoSum.cs"), entry: "Solution.TwoSum", strategy: "Distinct",
    compareFile: sample("leetcode/TwoSumBrute.cs"), compareEntry: "Solution.TwoSum"
  }), "TwoSum", sample("leetcode/TwoSumBrute.cs"));
  assert.deepEqual(tabs, ["steps", "cost", "growth", "compare"]);
});

test("an exponential solution on small sizes renders", () => {
  exercise(measure({ file: sample("FibonacciParamSample.cs"), entry: "Fib" }), "Fib");
});

test("a recursive tree solution renders its call depth", () => {
  const { document } = exercise(measure({ file: sample("leetcode/MaxDepth.cs"), entry: "Solution.MaxDepth", strategy: "Skewed" }), "MaxDepth");
  document.querySelector<HTMLElement>('.tab[data-tab="growth"]')!.click();
  assert.ok(document.querySelector('.metric[data-metric="depth"]'), "a call-depth metric is offered for a recursive solution");
});

test("a compile error renders as an explanation, not a crash", () => {
  const broken = path.join(root, "extension/.devharness/broken.cs");
  fs.mkdirSync(path.dirname(broken), { recursive: true });
  fs.writeFileSync(broken, "public class Solution { public int Go(int[] v) { return missing; } }");
  const { document } = exercise(measure({ file: broken, entry: "Solution.Go" }), "Go");
  assert.match(document.body.textContent ?? "", /Nothing could be measured/);
  assert.match(document.body.textContent ?? "", /line 1/);
});

test("a missing base case renders as an explanation, not a crash", () => {
  const recursive = path.join(root, "extension/.devharness/overflow.cs");
  fs.writeFileSync(recursive, "public class Solution { public int Go(int n) { return Go(n + 1) + 1; } }");
  const { document } = exercise(measure({ file: recursive, entry: "Solution.Go" }), "Go");
  assert.match(document.body.textContent ?? "", /base case/);
});

test("the step view draws pointers and steps through a recorded run", () => {
  const measured = measure({ file: sample("leetcode/TwoSumSorted.cs"), entry: "Solution.TwoSum", strategy: "Sorted" });
  const recorded = measure({
    file: sample("leetcode/TwoSumSorted.cs"), entry: "Solution.TwoSum", stepSize: 8,
    inputs: [
      { shape: "IntArray", strategy: "Sorted", minValue: 0, maxValue: 1000, scalesWithSize: true, fixedValue: 0, seed: 20260909 },
      { shape: "IntScalar", strategy: "Random", minValue: 0, maxValue: 1000, scalesWithSize: false, fixedValue: 9, seed: 20260909 }
    ]
  }) as { steps: { steps: unknown[]; returnValue: string } };

  assert.equal(recorded.steps.returnValue, "[3,8]");

  const { dom, errors } = load();
  const window = dom.window as unknown as { __render(r: unknown, q: unknown): void; __steps(t: unknown): void };
  const document = dom.window.document;

  window.__render(measured, { entry: "TwoSum" });
  document.querySelector<HTMLElement>('.tab[data-tab="steps"]')!.click();
  window.__steps(recorded.steps);
  assert.deepEqual(errors, []);

  // Which cell each pointer sits under, e.g. "1:left 7:right" — positions, not just labels.
  const pointersNow = () => Array.from(document.querySelectorAll(".cell"))
    .map(cell => [cell.querySelector(".cell-index")!.textContent!.trim(), cell.querySelector(".cell-pointers")!.textContent!.trim()])
    .filter(([, labels]) => labels)
    .map(([index, labels]) => `${index}:${labels}`);

  // Walk the whole run with the transport, checking it never errors and the pointers move.
  const seen = new Set<string>();
  for (let i = 0; i < recorded.steps.steps.length; i++) {
    seen.add(pointersNow().join(","));
    document.querySelector<HTMLElement>('[data-step="next"]')!.click();
    assert.deepEqual(errors, [], `step ${i}`);
  }

  assert.ok(seen.has("0:left,7:right"), "left and right start at the two ends of the array");
  assert.ok(seen.size > 2, "the pointers move as the run proceeds");
  assert.match(document.body.textContent ?? "", /Returned/);

  document.querySelector<HTMLElement>('[data-step="first"]')!.click();
  document.querySelector<HTMLElement>('[data-step="last"]')!.click();
  assert.deepEqual(errors, []);
});

test("the step view shows a flood fill's position on its grid", () => {
  const recorded = measure({ file: sample("leetcode/NumIslands.cs"), entry: "Solution.NumIslands", stepSize: 4 }) as {
    steps: unknown;
  };
  const { dom, errors } = load();
  const window = dom.window as unknown as { __render(r: unknown, q: unknown): void; __steps(t: unknown): void };
  window.__render(measure({ file: sample("leetcode/NumIslands.cs"), entry: "Solution.NumIslands" }), { entry: "NumIslands" });
  dom.window.document.querySelector<HTMLElement>('.tab[data-tab="steps"]')!.click();
  window.__steps(recorded.steps);

  for (let i = 0; i < 40; i++) dom.window.document.querySelector<HTMLElement>('[data-step="next"]')!.click();
  assert.deepEqual(errors, []);
  assert.ok(dom.window.document.querySelector(".grid"), "the grid is drawn");
});

// ---- the run view's inputs ----------------------------------------------------------

const walkthrough = (name: string) => path.join(root, "extension/samples/csharp", name);

type Panel = { __steps(t: unknown, q?: unknown): void; dispatchEvent(e: Event): boolean };

function showRun(trace: unknown) {
  const loaded = load(true);
  (loaded.dom.window as unknown as Panel).__steps(trace, { file: "TwoSumSorted.cs", entry: "Solution.TwoSum" });
  return { ...loaded, document: loaded.dom.window.document, text: () => loaded.dom.window.document.body.textContent ?? "" };
}

test("values found in the code fill the boxes, and the result is checked", () => {
  const recorded = measure({ file: walkthrough("TwoSumSorted.cs"), entry: "Solution.TwoSum", stepSize: 8 }) as { steps: unknown };
  const { document, errors, text } = showRun(recorded.steps);
  assert.deepEqual(errors, []);

  const boxes = Array.from(document.querySelectorAll<HTMLInputElement>(".value-box")).map(b => b.value);
  assert.deepEqual(boxes, ["[2,7,11,15]", "9"]);
  assert.match(text(), /from Example 1/);
  assert.match(text(), /✓ Matches the expected output/);

  const options = Array.from(document.querySelectorAll<HTMLOptionElement>("#example option")).map(o => o.textContent);
  assert.deepEqual(options, ["Example 1", "Example 2", "None — generate values"]);
});

test("a typed value is sent with the next run, and only that one", () => {
  const recorded = measure({ file: walkthrough("TwoSumSorted.cs"), entry: "Solution.TwoSum", stepSize: 8 }) as { steps: unknown };
  const { dom, document, posted } = showRun(recorded.steps);

  const target = document.querySelector<HTMLInputElement>('.value-box[data-index="1"]')!;
  target.value = "18";
  target.dispatchEvent(new dom.window.Event("input"));
  target.dispatchEvent(new dom.window.KeyboardEvent("keydown", { key: "Enter" }));

  const run = posted.find(m => m.type === "run");
  assert.deepEqual(run, { type: "run", values: [null, "18"], example: 0 });
});

test("choosing another example runs it, setting typed values aside", () => {
  const recorded = measure({ file: walkthrough("TwoSumSorted.cs"), entry: "Solution.TwoSum", stepSize: 8 }) as { steps: unknown };
  const { dom, document, posted } = showRun(recorded.steps);

  const select = document.querySelector<HTMLSelectElement>("#example")!;
  select.value = "1";
  select.dispatchEvent(new dom.window.Event("change"));
  assert.deepEqual(posted.find(m => m.type === "run"), { type: "run", example: 1 });
});

test("an input that cannot be read is marked under its own box, and nothing runs", () => {
  const recorded = measure({
    file: walkthrough("TwoSumSorted.cs"), entry: "Solution.TwoSum", stepSize: 8, values: ["[2,7,", null]
  }) as { steps: { status: string } };
  assert.equal(recorded.steps.status, "InvalidInput");

  const { document, errors, text } = showRun(recorded.steps);
  assert.deepEqual(errors, []);
  const invalid = document.querySelector('.value-box[data-index="0"]')!;
  assert.equal(invalid.getAttribute("aria-invalid"), "true");
  assert.match(document.querySelector(".input-error")!.textContent!, /expected a value at the end/);
  assert.match(text(), /typed by you/);
  assert.match(text(), /Fix the highlighted input/);
  assert.ok(document.querySelector("#use-code"), "a typed value can be undone");
  assert.equal(document.querySelector(".transport"), null, "no run is shown");
});

test("an exception in the student's code names the line, and the run up to it still plays", () => {
  const throwing = path.join(root, "extension/.devharness/throws.cs");
  fs.mkdirSync(path.dirname(throwing), { recursive: true });
  fs.writeFileSync(throwing, [
    "// Input: nums = [1,2,3]",
    "public class Solution {",
    "    public int Last(int[] nums) {",
    "        int i = 0;",
    "        while (true) {",
    "            i += nums[i];",
    "        }",
    "    }",
    "}"
  ].join("\n"));
  const recorded = measure({ file: throwing, entry: "Solution.Last", stepSize: 8 }) as {
    steps: { status: string; errorLine: number; errors: string[]; steps: unknown[] }
  };
  assert.equal(recorded.steps.status, "RuntimeError");
  assert.equal(recorded.steps.errorLine, 6);
  assert.match(recorded.steps.errors[0], /^Your code threw IndexOutOfRangeException at line 6: /);
  assert.doesNotMatch(recorded.steps.errors[0], /\n\s*at /, "no stack trace reaches the student");

  const { document, errors, posted, text } = showRun(recorded.steps);
  assert.deepEqual(errors, []);
  assert.match(text(), /Your code threw an exception/);
  assert.ok(document.querySelector(".transport"), "the steps before the exception can still be played");

  document.querySelector<HTMLElement>(".show-line")!.click();
  assert.deepEqual(posted.find(m => m.type === "reveal"), { type: "reveal", line: 6 });
});

test("with no example in the code, values are generated at a size the student picks", () => {
  const recorded = measure({ file: sample("SortParamSample.cs"), entry: "SortParamSample.Sort", stepSize: 5 }) as { steps: unknown };
  const { document, text } = showRun(recorded.steps);
  assert.match(text(), /No example found in your code/);
  assert.match(text(), /generated at n = 5/);
  assert.ok(document.querySelector("#step-size"), "the size can be changed");
});

test("Space plays and pauses the run", () => {
  const recorded = measure({ file: walkthrough("TwoSumSorted.cs"), entry: "Solution.TwoSum", stepSize: 8 }) as { steps: unknown };
  const { dom, document } = showRun(recorded.steps);
  const space = () => document.dispatchEvent(new dom.window.KeyboardEvent("keydown", { key: " " }));

  space();
  assert.equal(document.querySelector('[data-step="play"]')!.textContent, "⏸");
  space();
  assert.equal(document.querySelector('[data-step="play"]')!.textContent, "▶");
});

test("complexity is measured only once its tab is opened", () => {
  const { dom, posted } = load(true);
  dom.window.dispatchEvent(new dom.window.MessageEvent("message", {
    data: { type: "open", request: { file: "/x/TwoSumSorted.cs", entry: "Solution.TwoSum" }, tab: "steps" }
  }));
  assert.equal(posted.filter(m => m.type === "measure").length, 0, "opening the run view measures nothing");

  dom.window.document.querySelector<HTMLElement>('.tab[data-tab="cost"]')!.click();
  assert.equal(posted.filter(m => m.type === "measure").length, 1);
  assert.match(dom.window.document.body.textContent ?? "", /Measuring/);
});

test("the visualizer's own failure offers its details, never a stack trace", () => {
  const { dom, posted } = load(true);
  const send = (data: unknown) => dom.window.dispatchEvent(new dom.window.MessageEvent("message", { data }));
  send({ type: "open", request: { file: "/x/A.cs", entry: "Solution.A" }, tab: "steps" });
  send({ type: "stepsError", message: "The visualizer stopped unexpectedly (exit code 134).", internal: true });

  dom.window.document.getElementById("show-log")!.click();
  assert.deepEqual(posted.find(m => m.type === "showLog"), { type: "showLog" });
});
