import assert from "node:assert/strict";
import * as fs from "node:fs";
import * as path from "node:path";
import { test } from "node:test";

// The panel's accents carry meaning — a pointer's name is orange text, a returned value
// blue — so each has to be readable on the editor background of every kind of theme.
// Read from the stylesheet itself, so changing a colour cannot quietly break this.

const css = fs.readFileSync(path.resolve(__dirname, "../../media/styles.css"), "utf8");

/** The accents a block of the stylesheet defines, e.g. body.vscode-light { --hot: #b4470f; } */
function accents(selector: string): Record<string, string> {
  const start = css.indexOf(selector + " {");
  assert.ok(start >= 0, `${selector} is styled`);
  const block = css.slice(start, css.indexOf("}", start));
  const found: Record<string, string> = {};
  for (const match of block.matchAll(/--(hot|cool|compare):\s*(?:var\([^,]+,\s*)?(#[0-9a-fA-F]{6})/g)) found[match[1]] = match[2];
  return found;
}

function luminance(hex: string): number {
  const channel = (i: number) => {
    const c = parseInt(hex.slice(1 + i * 2, 3 + i * 2), 16) / 255;
    return c <= 0.03928 ? c / 12.92 : Math.pow((c + 0.055) / 1.055, 2.4);
  };
  return 0.2126 * channel(0) + 0.7152 * channel(1) + 0.0722 * channel(2);
}

function contrast(a: string, b: string): number {
  const [light, dark] = [luminance(a), luminance(b)].sort((x, y) => y - x);
  return (light + 0.05) / (dark + 0.05);
}

// Editor backgrounds of VS Code's default theme of each kind.
const themes: [string, string, string][] = [
  ["dark (Dark Modern)", ":root", "#1f1f1f"],
  ["light (Light Modern)", "body.vscode-light", "#ffffff"],
  ["high contrast", "body.vscode-high-contrast", "#000000"],
  ["high contrast light", "body.vscode-high-contrast-light", "#ffffff"]
];

for (const [name, selector, background] of themes) {
  test(`accents are readable as text in the ${name} theme`, () => {
    const colours = accents(selector);
    for (const [accent, colour] of Object.entries(colours)) {
      // 4.5:1 is WCAG AA for normal text; pointer labels and returned values are small.
      assert.ok(contrast(colour, background) >= 4.5,
        `--${accent} ${colour} on ${background} is ${contrast(colour, background).toFixed(2)}:1`);
    }
    assert.ok(Object.keys(colours).length >= 2, "the theme sets its accents");
  });
}
