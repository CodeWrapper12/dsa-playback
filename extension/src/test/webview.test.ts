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
const engine = path.join(root, "engine/DsaViz.Engine/bin/Debug/net8.0", process.platform === "win32" ? "DsaViz.Engine.exe" : "DsaViz.Engine");
const script = fs.readFileSync(path.join(root, "extension/media/main.js"), "utf8");
const runScript = fs.readFileSync(path.join(root, "extension/media/run.js"), "utf8");

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
  // Loaded in the same order as the panel loads them.
  dom.window.eval(runScript);
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
  const pointersNow = () => Array.from(document.querySelectorAll("#step-area .cell:not(.more)"))
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

test("run all examples lists each result, and a row opens that example", () => {
  const file = sample("TwoSumExamples.cs");
  const recorded = measure({ file, entry: "Solution.TwoSum", stepSize: 8 }) as { steps: unknown };
  const { dom, document, posted, errors, text } = showRun(recorded.steps);

  document.getElementById("run-examples")!.click();
  assert.deepEqual(posted.at(-1), { type: "runExamples" });
  assert.match(text(), /Running every example/);

  const answer = measure({ file, entry: "Solution.TwoSum", allExamples: true }) as { examples: unknown };
  dom.window.postMessage({ type: "examples", report: answer.examples }, "*");
  return new Promise<void>(resolve => setTimeout(() => {
    assert.deepEqual(errors, []);
    const rows = Array.from(document.querySelectorAll<HTMLElement>(".example-row"));
    assert.deepEqual(rows.map(r => r.querySelector(".mark")!.textContent), ["✓", "✗", "✗"]);
    assert.match(rows[1].textContent!, /\[2,1\][\s\S]*\[1,2\]/);
    assert.match(rows[2].textContent!, /Your code threw IndexOutOfRangeException/);
    assert.match(text(), /1 of 3 examples match the expected output/);

    rows[1].click();
    assert.deepEqual(posted.at(-1), { type: "run", example: 1 });
    resolve();
  }, 0));
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

// ---- the animated run view -------------------------------------------------------------

type RunPanel = Panel & { __goto(index: number): void };

/** Records a run with the real engine and shows it, ready to step through. */
function run(file: string, entry: string, extra: Record<string, unknown> = {}) {
  const recorded = measure({ file, entry, stepSize: 8, ...extra }) as { steps: { steps: unknown[]; status: string } };
  assert.equal(recorded.steps.status, "Completed");
  const loaded = load(true);
  const window = loaded.dom.window as unknown as RunPanel;
  window.__steps(recorded.steps, { file, entry });
  const area = () => loaded.dom.window.document.getElementById("step-area")!;
  const go = (index: number) => { window.__goto(index); assert.deepEqual(loaded.errors, [], `step ${index}`); };
  return { ...loaded, trace: recorded.steps, area, go, count: recorded.steps.steps.length };
}

const leetcode = (name: string) => sample(`leetcode/${name}`);

test("every step of every practice structure draws without error", () => {
  const cases: [string, string, Record<string, unknown>][] = [
    [leetcode("ReverseList.cs"), "Solution.ReverseList", {}],
    [walkthrough("LevelOrder.cs"), "Solution.LevelOrder", {}],
    [leetcode("TwoSum.cs"), "Solution.TwoSum", {}],
    [leetcode("KthLargest.cs"), "Solution.FindKthLargest", {}],
    [leetcode("NumIslands.cs"), "Solution.NumIslands", { stepSize: 4 }],
    [leetcode("CountComponents.cs"), "Solution.CountComponents", { stepSize: 5 }],
    [walkthrough("ClimbStairs.cs"), "Solution.ClimbStairs", { example: 1 }],
    [leetcode("MaxDepth.cs"), "Solution.MaxDepth", {}]
  ];
  for (const [file, entry, extra] of cases) {
    const { go, count } = run(file, entry, extra);
    for (let i = 0; i < count; i++) go(i);
    for (let i = count - 1; i >= 0; i -= 7) go(i);   // backwards and jumping too
  }
});

test("a linked list is one drawing, labelled by every variable that points into it", () => {
  const { go, area } = run(leetcode("ReverseList.cs"), "Solution.ReverseList", { stepSize: 4 });
  go(12);
  const labels = Array.from(area().querySelectorAll(".node-label")).map(l => l.textContent);
  assert.deepEqual(labels.sort(), ["head", "next", "previous"].filter(n => labels.includes(n)).sort());
  assert.equal(area().querySelectorAll("svg[aria-label='linked list']").length, 1, "one list, not one per variable");
  assert.match(area().querySelector(".var-name")!.textContent!, /head/);
});

test("a rewired link bends from its old target to the new one", () => {
  const { go, area, trace } = run(leetcode("ReverseList.cs"), "Solution.ReverseList", { stepSize: 4 });
  const steps = trace.steps as { heap?: { kind: string }[] }[];
  const rewiring = steps.findIndex((s, i) => i > 0 && (s.heap || []).some(o => o.kind === "listnode"));
  go(rewiring - 1);
  go(rewiring);
  const rewired = area().querySelector("path.link.rewired, text.null-mark");
  assert.ok(rewired, "the changed link is marked");
  go(rewiring + 2);
  go(rewiring + 3);
  assert.ok(area().querySelector("path.link.rewired animate[attributeName='d']") || area().querySelector("path.link"),
    "links are drawn as paths that can bend");
});

test("swapped values fly past each other and a moving pointer slides", () => {
  const swap = path.join(root, "extension/.devharness/swap.cs");
  fs.writeFileSync(swap, [
    "// Input: values = [5,1,4,2,8]",
    "public class Solution {",
    "    public int[] Sort(int[] values) {",
    "        for (int i = 0; i < values.Length; i++)",
    "            for (int j = 0; j + 1 < values.Length - i; j++)",
    "                if (values[j] > values[j + 1])",
    "                    (values[j], values[j + 1]) = (values[j + 1], values[j]);",
    "        return values;",
    "    }",
    "}"
  ].join("\n"));
  const { go, area, count } = run(swap, "Solution.Sort");

  let flew = 0, slid = 0;
  for (let i = 1; i < count; i++) {
    go(i);
    const fly = Array.from(area().querySelectorAll(".value.fly"));
    if (fly.length === 2) {
      flew++;
      // One moves right by a cell and the other left by one: they trade places.
      assert.deepEqual(fly.map(f => f.getAttribute("style")).sort(), ["--from:-40px", "--from:40px"]);
    }
    if (area().querySelector(".pointer.slide")) slid++;
  }
  assert.ok(flew >= 4, `[5,1,4,2,8] takes four swaps; ${flew} animated`);
  assert.ok(slid > 0, "pointers slide as they move");
});

test("a value written into a cell flashes, and a step back draws without animating", () => {
  const { go, area, count } = run(walkthrough("TwoSumSorted.cs"), "Solution.TwoSum");
  go(3);
  go(count - 1);   // a jump: nothing is adjacent, so nothing animates
  assert.equal(area().querySelectorAll(".slide, .fly, .written").length, 0);
});

test("map entries animate in as they are added", () => {
  const { go, area, count } = run(leetcode("TwoSum.cs"), "Solution.TwoSum");
  let added = 0;
  for (let i = 1; i < count; i++) { go(i); added += area().querySelectorAll(".entry.added").length; }
  assert.ok(added >= 2, "each `seen[...] = i` animates its entry in");
});

test("a queue shows what joined and what left", () => {
  const { go, area, count } = run(walkthrough("LevelOrder.cs"), "Solution.LevelOrder");
  let joined = 0, left = 0;
  for (let i = 1; i < count; i++) {
    go(i);
    joined += area().querySelectorAll(".lane.queue .slot.added").length;
    left += area().querySelectorAll(".lane.queue .slot.removed").length;
  }
  // [3,9,20,null,null,15,7]: five nodes go through the queue, the root included.
  assert.equal(joined, 5, "every node is enqueued");
  assert.equal(left, 5, "every node is dequeued");
});

test("a heap is drawn level by level, smallest at the top", () => {
  const { go, area, count } = run(leetcode("KthLargest.cs"), "Solution.FindKthLargest");
  go(count - 1);
  const levels = area().querySelectorAll(".heap-level");
  assert.ok(levels.length >= 1);
  assert.match(area().textContent ?? "", /four-way heap/);
});

test("a recursive tree walk always shows the whole tree, with the path of calls", () => {
  const { go, area, count, dom } = run(leetcode("MaxDepth.cs"), "Solution.MaxDepth", { strategy: "Balanced", stepSize: 7 });
  let deepest = 0;
  for (let i = 0; i < count; i++) {
    go(i);
    assert.equal(area().querySelectorAll("svg[aria-label='tree'] .node").length, 7, `the tree is drawn at step ${i}`);
    deepest = Math.max(deepest, area().querySelectorAll(".node.path").length);
  }
  assert.ok(deepest >= 2, "the nodes being worked on further up the stack are marked");
  assert.ok(dom.window.document.querySelector("svg[aria-label='recursion tree']"), "recursion gets a recursion tree");
});

test("the recursion tree shows each call with what it returned", () => {
  const { go, area, count } = run(walkthrough("ClimbStairs.cs"), "Solution.ClimbStairs", { example: 1 });
  go(count - 1);
  const calls = Array.from(area().querySelectorAll("svg[aria-label='recursion tree'] .call"));
  assert.equal(calls.length, 7, "ClimbStairs(5) with a memo makes 7 calls");
  const returned = area().querySelectorAll("svg[aria-label='recursion tree'] .returned");
  assert.ok(returned.length >= 6, "calls that finished show their return value");
  assert.match(area().querySelector(".call-stack")!.textContent!, /ClimbStairs\(5\)/);
});

test("a graph is drawn from its edge list, with visited, waiting and current nodes", () => {
  const { go, area, count } = run(leetcode("CountComponents.cs"), "Solution.CountComponents", { stepSize: 6 });
  let sawWaiting = false, sawVisited = false;
  for (let i = 0; i < count; i++) {
    go(i);
    assert.equal(area().querySelectorAll("svg[aria-label='graph'] .node").length, 6);
    sawWaiting ||= !!area().querySelector("svg[aria-label='graph'] .node.waiting");
    sawVisited ||= !!area().querySelector("svg[aria-label='graph'] .node.visited");
  }
  assert.ok(sawWaiting && sawVisited);
});

test("memory shows this step, the total so far, and each structure's size", () => {
  const { go, area } = run(leetcode("TwoSum.cs"), "Solution.TwoSum");
  go(2);
  const text = area().querySelector(".memory")!.textContent!;
  assert.match(text, /this line allocates/);
  assert.match(text, /allocated so far/);
  assert.match(text, /seen\s*≈\s*\d+ B/);
  assert.ok(area().querySelector(".memory .sparkline polyline"));
});

test("a long array is windowed around its pointers", () => {
  const { go, area } = run(sample("SortParamSample.cs"), "SortParamSample.Sort", { stepSize: 1000, maxSteps: 60 });
  go(50);
  const cells = area().querySelectorAll(".cell:not(.more)");
  assert.equal(cells.length, 64);
  assert.ok(area().querySelector(".cell.more"), "what is left out is counted");
  assert.ok(area().querySelector(".cell.pointed"), "the pointers stay on screen");
});

test("a capped run says so and stays navigable", () => {
  const { go, dom, count } = run(sample("SortParamSample.cs"), "SortParamSample.Sort", { stepSize: 30, maxSteps: 50 });
  assert.equal(count, 50);
  go(49);
  assert.match(dom.window.document.body.textContent ?? "", /Showing the first 50 steps/);
});

test("speed changes how fast it plays", () => {
  const { dom } = run(walkthrough("TwoSumSorted.cs"), "Solution.TwoSum");
  const document = dom.window.document;
  const speed = document.querySelector<HTMLSelectElement>("#speed")!;
  assert.deepEqual(Array.from(speed.options).map(o => o.value), ["0.25", "0.5", "1", "2", "4"]);
  speed.value = "4";
  speed.dispatchEvent(new dom.window.Event("change"));
  assert.match(document.querySelector(".run")!.getAttribute("style")!, /--anim:/);
});

// ---- Python, through the same panel --------------------------------------------------------

const pythonSample = (name: string) => path.join(root, "samples/python", name);

test("every step of every Python sample draws without error", () => {
  const cases: [string, string][] = [
    ["two_sum.py", "Solution.twoSum"], ["two_sum_sorted.py", "Solution.twoSum"], ["reverse_list.py", "Solution.reverseList"],
    ["max_depth.py", "Solution.maxDepth"], ["level_order.py", "Solution.levelOrder"], ["climb_stairs.py", "Solution.climbStairs"],
    ["fib.py", "fib"], ["num_islands.py", "Solution.numIslands"], ["count_components.py", "Solution.countComponents"],
    ["kth_largest.py", "Solution.findKthLargest"], ["valid_parentheses.py", "Solution.isValid"]
  ];
  for (const [file, entry] of cases) {
    const { go, count, dom } = run(pythonSample(file), entry);
    for (let i = 0; i < count; i++) go(i);
    for (let i = count - 1; i >= 0; i -= 5) go(i);
    assert.match(dom.window.document.body.textContent ?? "", /Matches the expected output/, file);
  }
});

test("a heapq list is drawn as a two-way heap and a list used as a stack as a stack", () => {
  const heap = run(pythonSample("kth_largest.py"), "Solution.findKthLargest");
  heap.go(heap.count - 2);
  assert.match(heap.area().textContent ?? "", /two children, at 2i\+1 and 2i\+2/);

  const stack = run(pythonSample("valid_parentheses.py"), "Solution.isValid", { values: ['"(([]"'] });
  let sawStack = false;
  for (let i = 0; i < stack.count; i++) { stack.go(i); sawStack ||= !!stack.area().querySelector(".lane.stack .slot"); }
  assert.ok(sawStack, "the stack is drawn as a stack");
});

test("Python's memory figures are labelled approximate in the run view", () => {
  const { go, area } = run(pythonSample("two_sum.py"), "Solution.twoSum");
  go(2);
  assert.match(area().querySelector(".memory")!.textContent!, /Approximate, from Python's tracemalloc/);
});

test("a Python file's hint names its own way of calling a function", () => {
  const recorded = measure({ file: pythonSample("dedupe_with_list.py"), entry: "Solution.dedupe", stepSize: 4 }) as { steps: unknown };
  const loaded = load(true);
  (loaded.dom.window as unknown as Panel).__steps(recorded.steps, { file: pythonSample("dedupe_with_list.py"), entry: "Solution.dedupe" });
  assert.match(loaded.dom.window.document.body.textContent ?? "", /if __name__ == "__main__":/);
});

test("a measured Python solution renders every tab", () => {
  const { document } = exercise(measure({ file: pythonSample("dedupe_with_list.py"), entry: "Solution.dedupe", strategy: "Distinct" }), "dedupe");
  document.querySelector<HTMLElement>('.tab[data-tab="cost"]')!.click();
  assert.match(document.body.textContent ?? "", /in \(on a list, tuple or string\)/, "the hidden scan is named");
  assert.match(document.body.textContent ?? "", /tracemalloc/, "Python's memory is labelled approximate");
});

test("a keyboard user keeps focus on the transport button they pressed", () => {
  const { dom } = run(walkthrough("TwoSumSorted.cs"), "Solution.TwoSum");
  const document = dom.window.document;
  document.querySelector<HTMLElement>('[data-step="next"]')!.focus();
  document.querySelector<HTMLElement>('[data-step="next"]')!.click();
  assert.equal((document.activeElement as HTMLElement).dataset.step, "next");
});

test("rows that go to a line can be reached and used from the keyboard", () => {
  const { dom, posted } = load(true);
  const window = dom.window as unknown as { __render(r: unknown, q: unknown): void };
  window.__render(measure({ file: sample("SortParamSample.cs"), entry: "SortParamSample.Sort" }), { entry: "Sort" });
  const row = dom.window.document.querySelector<HTMLElement>(".hot-row")!;
  assert.equal(row.getAttribute("tabindex"), "0");
  row.dispatchEvent(new dom.window.KeyboardEvent("keydown", { key: "Enter" }));
  assert.equal(posted.filter(m => m.type === "reveal").length, 1);
});

// ---- plain words ---------------------------------------------------------------------

type Plain = {
  time(cls: string, derivation: unknown, source?: string): string;
  space(cls: string, source?: string): string;
  classes: string[];
};

const NOTATION = /O\(|n²|n³|2ⁿ|log n|·|\bn\b/;

test("every complexity class has a plain sentence for time and for space, with no notation", () => {
  const plain = (load().dom.window as unknown as { __plain: Plain }).__plain;
  assert.deepEqual([...plain.classes],
    ["Constant", "Logarithmic", "Linear", "Linearithmic", "Quadratic", "Cubic", "Exponential", "Unknown"]);

  const seen = new Set<string>();
  for (const cls of plain.classes) {
    for (const sentence of [plain.time(cls, null), plain.time(cls, null, "code"), plain.space(cls), plain.space(cls, "code")]) {
      assert.ok(sentence.length > 20, `${cls}: "${sentence}"`);
      assert.doesNotMatch(sentence, NOTATION, `${cls}: "${sentence}"`);
      assert.match(sentence, /\.$/, `${cls}: "${sentence}"`);
      seen.add(sentence);
    }
  }
  // Each class says something different: the same sentence for two would teach nothing.
  assert.equal(seen.size, 7 * 2 + 4, "distinct sentences");
  assert.equal(plain.time("Quadratic", null), "When the input doubles, this takes about four times as long.");
  assert.equal(plain.time("Logarithmic", null), "Doubling the input adds only about one more step.");
  assert.equal(plain.space("Linear"), "It holds about twice as much when the input doubles.");
});

test("the measured time says which line causes it", () => {
  const { document } = exercise(measure({ file: sample("SortParamSample.cs"), entry: "SortParamSample.Sort" }), "Sort");
  document.querySelector<HTMLElement>('.tab[data-tab="cost"]')!.click();
  const sentences = Array.from(document.querySelectorAll(".verdict .plain")).map(p => p.textContent);
  assert.equal(sentences.length, 4, "time and space, measured and read from the code");
  assert.equal(sentences[0],
    "When the input doubles, this takes about four times as long, because line 10 runs once for every pair of items.");
});

test("a line that costs more than it runs blames the library call inside it", () => {
  const { document } = exercise(measure({ file: sample("leetcode/DedupeWithList.cs"), entry: "Solution.CountDistinct" }), "CountDistinct");
  document.querySelector<HTMLElement>('.tab[data-tab="cost"]')!.click();
  assert.equal(document.querySelector(".verdict .plain")!.textContent,
    "When the input doubles, this takes about four times as long, because line 8 runs once for every item, " +
    "and each time it calls a library method that does more work as the input grows.");
});

test("a measured Python solution gets the same plain sentences", () => {
  const { document } = exercise(measure({ file: pythonSample("dedupe_with_list.py"), entry: "Solution.dedupe", strategy: "Distinct" }), "dedupe");
  document.querySelector<HTMLElement>('.tab[data-tab="cost"]')!.click();
  const sentences = Array.from(document.querySelectorAll(".verdict .plain")).map(p => p.textContent ?? "");
  assert.equal(sentences.length, 4);
  assert.match(sentences[0], /^When the input doubles, this takes about four times as long, because line \d+ runs/);
  for (const sentence of sentences) assert.doesNotMatch(sentence, NOTATION, sentence);
});

// ---- design problems -------------------------------------------------------------------

for (const [file, calls] of [["design/LRUCache.cs", ["LRUCache", "Put", "Get"]], ["design/lru_cache.py", ["__init__", "put", "get"]]] as const) {
  test(`a design problem plays every call, naming the one running (${file})`, () => {
    const { dom, go, count, area, trace } = run(sample(file), "LRUCache");
    const text = () => dom.window.document.body.textContent ?? "";
    assert.match(text(), /✓ Matches the expected output/);
    assert.match(text(), /operations/);
    assert.match(text(), /arguments/);

    const seen = new Set<string>();
    for (let i = 0; i < count; i++) {
      go(i);
      for (const call of calls) if (new RegExp(`\\b${call}\\b`).test(area().textContent ?? "")) seen.add(call);
    }
    assert.deepEqual([...seen].sort(), [...calls].sort(), "every call appears on the stack as it runs");

    // The fourth call, get(1), is named as it starts — after two puts in a row, which
    // depth alone could not tell apart.
    const fourth = (trace as unknown as { calls: { step: number }[] }).calls[3];
    go(fourth.step);
    assert.match(area().textContent ?? "", /Call 4 of 10: get/);
    assert.match(area().textContent ?? "", /(Get|get)\(1\)/);
  });
}

test("a design problem's calls Python could not make are explained above the run", () => {
  const recorded = measure({
    file: sample("design/lru_cache.py"), entry: "LRUCache", stepSize: 8,
    values: ['["LRUCache","put","gett"]', "[[1],[1,1],[1]]"]
  }) as { steps: unknown };
  const { text, errors } = showRun(recorded.steps);
  assert.deepEqual(errors, []);
  assert.match(text(), /Fix the input/);
  assert.match(text(), /Operation 3 is "gett", but LRUCache has no method by that name\./);
});

// ---- explain this step ------------------------------------------------------------------

test("Explain this step is absent until the setting is on, then asks for the current step only when pressed", () => {
  const recorded = measure({ file: walkthrough("TwoSumSorted.cs"), entry: "Solution.TwoSum", stepSize: 8 }) as { steps: unknown };
  const { dom, document, posted, errors } = showRun(recorded.steps);
  const send = (message: unknown) => dom.window.postMessage(message, "*");
  assert.equal(document.getElementById("explain"), null, "off by default");

  return new Promise<void>(resolve => {
    send({ type: "settings", explain: true });
    setTimeout(() => {
      const before = posted.length;
      assert.ok(document.getElementById("explain"), "shown once turned on");
      assert.equal(posted.filter(m => m.type === "explain").length, 0, "nothing is sent until pressed");

      (dom.window as unknown as { __goto(i: number): void }).__goto(2);
      document.getElementById("explain")!.click();
      const asked = posted.slice(before).find(m => m.type === "explain")!;
      assert.equal(asked.index, 2);
      assert.ok(Array.isArray(asked.callStack) && (asked.callStack as string[]).includes("TwoSum"));
      assert.ok(Array.isArray(asked.changed));

      // The answer streams in; text from the model is shown as text, never as markup.
      send({ type: "explanation", trace: asked.trace, index: 2, text: "left moves <b>right</b>", done: false });
      setTimeout(() => {
        send({ type: "explanation", trace: asked.trace, index: 2, text: "left moves <b>right</b>.", done: true, model: "Stand-in" });
        setTimeout(() => {
          const shown = document.querySelector("#explain-area .explanation")!;
          assert.match(shown.textContent ?? "", /left moves <b>right<\/b>\./);
          assert.equal(shown.querySelector("b"), null);
          assert.match(shown.textContent ?? "", /Written by Stand-in/);

          // Another step has its own answer, or none.
          (dom.window as unknown as { __goto(i: number): void }).__goto(3);
          assert.equal(document.querySelector("#explain-area .explanation"), null);
          assert.deepEqual(errors, []);
          resolve();
        }, 0);
      }, 0);
    }, 0);
  });
});

test("an answer from an earlier run is not shown on a new one", () => {
  const recorded = measure({ file: walkthrough("TwoSumSorted.cs"), entry: "Solution.TwoSum", stepSize: 8 }) as { steps: unknown };
  const { dom, document } = showRun(recorded.steps);
  return new Promise<void>(resolve => {
    dom.window.postMessage({ type: "settings", explain: true }, "*");
    dom.window.postMessage({ type: "explanation", trace: 999, index: 0, text: "stale", done: true }, "*");
    setTimeout(() => {
      assert.equal(document.querySelector("#explain-area .explanation"), null);
      resolve();
    }, 0);
  });
});
