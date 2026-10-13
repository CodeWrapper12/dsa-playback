import assert from "node:assert/strict";
import { test } from "node:test";
import { buildPrompt, ChatModel, explainStep, JUSTIFICATION, LanguageModels, NO_API, NO_MODEL, StepToExplain } from "../explain";

const step: StepToExplain = {
  language: "python",
  code: "1  def twoSum(nums, target):\n2      seen = {}\n3      for i, x in enumerate(nums):",
  line: 3,
  lineText: "    for i, x in enumerate(nums):",
  callStack: ["twoSum"],
  variables: [{ name: "i", value: "1" }, { name: "seen", value: "{2: 0}" }],
  changed: ["i"]
};

/** A stand-in for Copilot: records what it was sent and answers in fragments. */
function model(fragments: string[], fail?: unknown): { lm: LanguageModels; sent: unknown[][]; options: unknown[] } {
  const sent: unknown[][] = [];
  const options: unknown[] = [];
  const chat: ChatModel = {
    name: "stand-in",
    sendRequest: async (messages, opts) => {
      sent.push(messages);
      options.push(opts);
      if (fail) throw fail;
      return { text: (async function* () { for (const f of fragments) yield f; })() };
    }
  };
  return { lm: { selectChatModels: async () => [chat] }, sent, options };
}

test("the prompt carries the line, the call stack, the variables and the code — and asks for plain words", () => {
  const prompt = buildPrompt(step);
  for (const part of ["Current line 3: for i, x in enumerate(nums):", "Call stack (outermost first): twoSum",
                      "- i = 1   (just changed)", "- seen = {2: 0}", "def twoSum(nums, target):", "do not use big-O notation"]) {
    assert.ok(prompt.includes(part), part);
  }
});

test("the answer streams in and is returned whole", async () => {
  const { lm, sent, options } = model(["At this step ", "i is 1."]);
  const seen: string[] = [];
  const result = await explainStep(lm, text => ({ role: "user", text }), step, soFar => seen.push(soFar));

  assert.deepEqual(result, { ok: true, text: "At this step i is 1.", model: "stand-in" });
  assert.deepEqual(seen, ["At this step ", "At this step i is 1."]);
  assert.equal(sent.length, 1, "one request per press");
  assert.deepEqual(options[0], { justification: JUSTIFICATION });
});

test("an editor without the API says so, and sends nothing", async () => {
  assert.deepEqual(await explainStep(undefined, t => t, step, () => {}), { ok: false, reason: NO_API });
});

test("no Copilot model says how to get one", async () => {
  const lm: LanguageModels = { selectChatModels: async () => [] };
  assert.deepEqual(await explainStep(lm, t => t, step, () => {}), { ok: false, reason: NO_MODEL });
});

test("a declined permission is explained, not thrown", async () => {
  const { lm } = model([], Object.assign(new Error("denied"), { code: "NoPermissions" }));
  const result = await explainStep(lm, t => t, step, () => {});
  assert.equal(result.ok, false);
  assert.match((result as { reason: string }).reason, /not allowed to use Copilot/);
});

test("any other failure is explained with its cause", async () => {
  const { lm } = model([], new Error("network down"));
  const result = await explainStep(lm, t => t, step, () => {});
  assert.deepEqual(result, { ok: false, reason: "Copilot could not explain this step: network down" });
});
