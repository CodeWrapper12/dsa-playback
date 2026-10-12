import assert from "node:assert/strict";
import { test } from "node:test";
import { annotationText, classLabel, fastest, formatBytes, formatCount, heatIntensity } from "../costFormat";

test("counts are grouped so long numbers stay readable", () => {
  assert.equal(formatCount(749625), "749,625");
  assert.equal(formatCount(1), "1");
});

test("byte sizes step up through units", () => {
  assert.equal(formatBytes(424), "424 B");
  assert.equal(formatBytes(4024), "3.9 KB");
  assert.equal(formatBytes(5 * 1024 * 1024), "5.0 MB");
});

test("a statement that allocated nothing shows no memory at all", () => {
  // An in-place sort would otherwise carry "0 B" on every line, which is noise on the
  // lines it is true of and distracting on the one line where it is not.
  assert.equal(formatBytes(0), "");
  assert.equal(annotationText(999, 0), "999×");
});

test("an allocating statement shows both figures", () => {
  assert.equal(annotationText(3, 32), "3×   ·   32 B");
});

test("library work is shown beside the count, not folded into it", () => {
  assert.equal(annotationText(1000, 0, 499500), "1,000×   ·   +499,500 library work");
  assert.equal(annotationText(3, 32, 10), "3×   ·   +10 library work   ·   32 B");
});

test("heat is logarithmic so a cold line stays visible beside a hot one", () => {
  const hottest = heatIntensity(749625, 749625);
  const coldest = heatIntensity(1, 749625);

  assert.equal(hottest, 1);
  // Linear scaling would put this at roughly 0.0000013 and render it as nothing.
  assert.ok(coldest > 0.04, `a line that ran once scored ${coldest}`);
  assert.ok(coldest < hottest);
});

test("heat is ordered by cost", () => {
  const max = 1000;
  const values = [1, 10, 100, 1000].map(hits => heatIntensity(hits, max));
  for (let i = 1; i < values.length; i++) {
    assert.ok(values[i] > values[i - 1], `intensity did not increase at index ${i}`);
  }
});

test("a statement that never ran gets no shading", () => {
  assert.equal(heatIntensity(0, 1000), 0);
});

test("a single-statement measurement does not divide by zero", () => {
  assert.equal(heatIntensity(1, 1), 1);
});

test("a line's own class sits beside its count", () => {
  assert.equal(annotationText(499500, 0, 0, "O(n²)"), "499,500×  O(n²)");
  assert.equal(annotationText(1000, 0, 499500, "O(n²)"), "1,000×  O(n²)   ·   +499,500 library work");
});

test("a line holding several statements grows as its fastest one", () => {
  assert.equal(fastest(["Linear", "Quadratic", "Constant"]), "Quadratic");
  assert.equal(fastest(["Unknown"]), undefined);
  assert.equal(classLabel("Linearithmic"), "O(n log n)");
  assert.equal(classLabel("Unknown"), undefined);
});
