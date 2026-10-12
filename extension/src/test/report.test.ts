import assert from "node:assert/strict";
import { test } from "node:test";
import { issueUrl, MAX_URL_LENGTH, ProblemFacts, redactHome, REPORTED_LOG_LINES } from "../report";

const facts = (log: string[]): ProblemFacts => ({
  extensionVersion: "1.1.0", vscodeVersion: "1.95.0", os: "darwin 25.0.0 arm64", language: "python", log
});

const bodyOf = (url: string) => new URL(url).searchParams.get("body") ?? "";

test("the report names the versions, the system and the language", () => {
  const url = issueUrl(facts(["engine --job -"]), "/Users/student");
  assert.ok(url.startsWith("https://github.com/CodeWrapper12/dsa-playback/issues/new?title="));
  const body = bodyOf(url);
  for (const fact of ["DSA Playback 1.1.0", "VS Code 1.95.0", "darwin 25.0.0 arm64", "Language: python", "engine --job -"]) {
    assert.ok(body.includes(fact), fact);
  }
});

test("the home folder is shown as ~, in either slash direction", () => {
  assert.equal(redactHome("/Users/student/code/a.py failed", "/Users/student"), "~/code/a.py failed");
  assert.equal(redactHome("C:\\Users\\student\\a.cs and C:/Users/student/b.cs", "C:\\Users\\student"), "~\\a.cs and ~/b.cs");

  const body = bodyOf(issueUrl(facts(["engine --file /Users/student/two_sum.py"]), "/Users/student"));
  assert.ok(!body.includes("/Users/student"), body);
  assert.ok(body.includes("~/two_sum.py"));
});

test("only the last 50 log lines are included", () => {
  const log = Array.from({ length: 80 }, (_, i) => `line ${i}`);
  const body = bodyOf(issueUrl(facts(log), "/home/s"));
  assert.ok(!body.includes("line 29\n"), "older lines are left out");
  assert.ok(body.includes("line 30\n"));
  assert.ok(body.includes("line 79\n"));
  assert.equal(REPORTED_LOG_LINES, 50);
});

test("a long log is shortened from the oldest end until the address fits", () => {
  const log = Array.from({ length: 50 }, (_, i) => `line ${i} ` + "x".repeat(300));
  const url = issueUrl(facts(log), "/home/s");
  assert.ok(url.length <= MAX_URL_LENGTH, `${url.length}`);
  assert.ok(bodyOf(url).includes("line 49 "), "the newest line survives");
});

test("characters that mean something in an address survive the round trip", () => {
  const line = "a & b = c # d % e ? f\nnext";
  assert.ok(bodyOf(issueUrl(facts([line]), "/home/s")).includes(line));
});
