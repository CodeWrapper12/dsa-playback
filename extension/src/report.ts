// What "Report a Problem" sends to GitHub. Kept free of the vscode module so it can be
// unit-tested; the command that gathers these facts and opens the page is in extension.ts.

export const ISSUES_URL = "https://github.com/CodeWrapper12/dsa-playback/issues/new";

/** How many of the most recent log lines go into a report. */
export const REPORTED_LOG_LINES = 50;

/**
 * GitHub refuses very long addresses, and browsers cut them. The log is shortened, oldest
 * lines first, until the whole address fits under this.
 */
export const MAX_URL_LENGTH = 7500;

export interface ProblemFacts {
  extensionVersion: string;
  vscodeVersion: string;
  /** "darwin 25.0.0 arm64" */
  os: string;
  /** The language of the file last run, or of the open editor. */
  language: string;
  /** The most recent log lines, oldest first. */
  log: string[];
}

/**
 * Replaces the home folder with ~ wherever it appears, so a report never carries the
 * student's user name. Both slash directions are replaced: Windows paths reach the log
 * either way.
 */
export function redactHome(text: string, home: string): string {
  if (!home) return text;
  const variants = new Set([home, home.replace(/\\/g, "/"), home.replace(/\//g, "\\")]);
  let result = text;
  for (const variant of variants) result = result.split(variant).join("~");
  return result;
}

function body(facts: ProblemFacts, log: string[]): string {
  return [
    "**What happened?**",
    "",
    "<!-- What you did, and what you saw. A screenshot helps. -->",
    "",
    "**What did you expect?**",
    "",
    "",
    "---",
    `- DSA Playback ${facts.extensionVersion}`,
    `- VS Code ${facts.vscodeVersion}`,
    `- ${facts.os}`,
    `- Language: ${facts.language}`,
    "",
    "<details><summary>Recent log lines (your code is not included)</summary>",
    "",
    "```text",
    ...(log.length ? log : ["(nothing logged yet)"]),
    "```",
    "",
    "</details>"
  ].join("\n");
}

function address(title: string, text: string): string {
  return `${ISSUES_URL}?title=${encodeURIComponent(title)}&body=${encodeURIComponent(text)}`;
}

/**
 * The new-issue address, pre-filled. The student reads and edits all of it in GitHub's
 * own form before anything is submitted.
 */
export function issueUrl(facts: ProblemFacts, home: string): string {
  const title = "Problem: ";
  let log = facts.log.slice(-REPORTED_LOG_LINES).map(line => redactHome(line, home));
  let url = address(title, redactHome(body(facts, log), home));
  while (url.length > MAX_URL_LENGTH && log.length > 0) {
    log = log.slice(1);
    url = address(title, redactHome(body(facts, log), home));
  }
  return url;
}
