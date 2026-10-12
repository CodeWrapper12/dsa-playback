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
  assert.deepEqual(tabs, ["cost", "growth"]);

  // The derivation and the per-statement formulas live on the cost tab.
  document.querySelector<HTMLElement>('.tab[data-tab="cost"]')!.click();
  assert.match(document.body.textContent ?? "", /0\.5·n²/);
});

test("a comparison renders every tab and metric without error", () => {
  const { tabs } = exercise(measure({
    file: sample("leetcode/TwoSum.cs"), entry: "Solution.TwoSum", strategy: "Distinct",
    compareFile: sample("leetcode/TwoSumBrute.cs"), compareEntry: "Solution.TwoSum"
  }), "TwoSum");
  assert.deepEqual(tabs, ["cost", "growth", "compare"]);
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
