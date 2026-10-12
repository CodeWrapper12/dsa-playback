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

function load() {
  const errors: string[] = [];
  const virtualConsole = new VirtualConsole();
  virtualConsole.on("jsdomError", error => errors.push(String(error.stack ?? error)));
  virtualConsole.on("error", (...args: unknown[]) => errors.push(args.map(String).join(" ")));

  const dom = new JSDOM('<div id="app"></div>', { runScripts: "outside-only", virtualConsole });
  dom.window.eval(script);
  return { dom, errors };
}

/** Renders, then clicks every tab, metric and axis toggle, re-rendering each time. */
function exercise(response: unknown, entry: string) {
  const { dom, errors } = load();
  const window = dom.window as unknown as { __render(r: unknown, q: unknown): void };
  const document = dom.window.document;

  window.__render(response, { entry });
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
  assert.deepEqual(tabs, ["cost", "growth", "steps"]);

  // The derivation and the per-statement formulas live on the cost tab.
  document.querySelector<HTMLElement>('.tab[data-tab="cost"]')!.click();
  assert.match(document.body.textContent ?? "", /0\.5·n²/);
});

test("a comparison renders every tab and metric without error", () => {
  const { tabs } = exercise(measure({
    file: sample("leetcode/TwoSum.cs"), entry: "Solution.TwoSum", strategy: "Distinct",
    compareFile: sample("leetcode/TwoSumBrute.cs"), compareEntry: "Solution.TwoSum"
  }), "TwoSum");
  assert.deepEqual(tabs, ["cost", "growth", "steps", "compare"]);
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

  assert.equal(recorded.steps.returnValue, "[3, 8]");

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
