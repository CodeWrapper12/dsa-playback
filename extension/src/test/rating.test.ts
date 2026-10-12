import assert from "node:assert/strict";
import { test } from "node:test";
import { ASK_AGAIN_AFTER, FIRST_ASK_AFTER, LATER, NEVER, RATE, RatingPrompt, RatingState } from "../rating";

/** A prompt whose storage, question and browser are recorded instead of real. */
function harness(answer: string | undefined) {
  let stored: RatingState | undefined;
  const asked: string[][] = [];
  let opened = 0;
  const prompt = new RatingPrompt({
    read: () => stored,
    write: async state => { stored = state; },
    ask: async (message, ...choices) => { asked.push([message, ...choices]); return answer; },
    openReviews: () => { opened++; }
  });
  const runs = async (count: number) => { for (let i = 0; i < count; i++) await prompt.succeeded(); };
  return { runs, asked, opened: () => opened, stored: () => stored };
}

test("nobody is asked before five successful runs", async () => {
  const h = harness(LATER);
  await h.runs(FIRST_ASK_AFTER - 1);
  assert.equal(h.asked.length, 0);
  await h.runs(1);
  assert.equal(h.asked.length, 1);
  assert.deepEqual(h.asked[0].slice(1), [RATE, LATER, NEVER]);
});

test("Rate opens the review page and never asks again", async () => {
  const h = harness(RATE);
  await h.runs(FIRST_ASK_AFTER + 100);
  assert.equal(h.asked.length, 1);
  assert.equal(h.opened(), 1);
  assert.equal(h.stored()!.done, true);
});

test("Later asks again after twenty more runs", async () => {
  const h = harness(LATER);
  await h.runs(FIRST_ASK_AFTER);
  await h.runs(ASK_AGAIN_AFTER - 1);
  assert.equal(h.asked.length, 1);
  await h.runs(1);
  assert.equal(h.asked.length, 2);
  assert.equal(h.opened(), 0);
});

test("closing the question without choosing counts as Later", async () => {
  const h = harness(undefined);
  await h.runs(FIRST_ASK_AFTER + ASK_AGAIN_AFTER);
  assert.equal(h.asked.length, 2);
});

test("Don't ask again stops it for good, and opens nothing", async () => {
  const h = harness(NEVER);
  await h.runs(FIRST_ASK_AFTER + 200);
  assert.equal(h.asked.length, 1);
  assert.equal(h.opened(), 0);
});

test("runs finishing while the question is open do not ask twice", async () => {
  let release: (answer: string) => void = () => {};
  let stored: RatingState | undefined;
  let asked = 0;
  const prompt = new RatingPrompt({
    read: () => stored,
    write: async state => { stored = state; },
    ask: () => { asked++; return new Promise<string>(resolve => { release = resolve; }); },
    openReviews: () => {}
  });
  for (let i = 0; i < FIRST_ASK_AFTER - 1; i++) await prompt.succeeded();
  const open = prompt.succeeded();
  await prompt.succeeded();
  await prompt.succeeded();
  release(LATER);
  await open;
  assert.equal(asked, 1);
  // The runs made while it was open still count toward the next ask.
  assert.equal(stored!.runs, FIRST_ASK_AFTER + 2);
  assert.equal(stored!.askAt, FIRST_ASK_AFTER + 2 + ASK_AGAIN_AFTER);
});
