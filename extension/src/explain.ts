// "Explain this step": what is sent to the student's own Copilot, and how its answer comes
// back. Kept free of the vscode module so it can be unit-tested with a stand-in model;
// extension.ts supplies VS Code's language-model API.

/** What a step is explained from. Nothing else about the student or their machine is sent. */
export interface StepToExplain {
  language: string;
  /** The solution's code, with line numbers, so the answer can refer to lines. */
  code: string;
  line: number;
  lineText: string;
  /** Outermost first. */
  callStack: string[];
  /** Each visible variable as the run view shows it. */
  variables: { name: string; value: string }[];
  /** The variables whose values changed since the step before, by name. */
  changed: string[];
}

/** The parts of VS Code's language-model API this uses. */
export interface ChatModel {
  readonly name: string;
  sendRequest(messages: unknown[], options: { justification: string }, token?: unknown): PromiseLike<{ text: AsyncIterable<string> }>;
}

export interface LanguageModels {
  selectChatModels(selector: { vendor: string }): PromiseLike<ChatModel[]>;
}

export type Explained =
  | { ok: true; text: string; model: string }
  | { ok: false; reason: string };

export const NO_API =
  "This editor has no language-model API, so steps cannot be explained here. VS Code 1.90 or later with GitHub Copilot has one.";
export const NO_MODEL =
  "No GitHub Copilot model is available. Sign in to GitHub Copilot in VS Code, or turn off dsaPlayback.explainWithAI.";
export const JUSTIFICATION =
  "DSA Playback sends the code you are stepping through, the current line and its variables, to explain this step.";

/** The longest code sent. A solution is far shorter; this only stops a huge file going whole. */
export const MAX_CODE_CHARACTERS = 12_000;

export function buildPrompt(step: StepToExplain): string {
  const code = step.code.length > MAX_CODE_CHARACTERS ? step.code.slice(0, MAX_CODE_CHARACTERS) + "\n…" : step.code;
  const variables = step.variables.length
    ? step.variables.map(v => `- ${v.name} = ${v.value}${step.changed.includes(v.name) ? "   (just changed)" : ""}`).join("\n")
    : "(none)";
  return [
    "You are helping a student learn data structures and algorithms by stepping through their own code.",
    "Explain, in two to four short sentences of plain English, what the current line is doing at this moment",
    "and why, using the actual values below. Say what just changed and what will happen next if it is useful.",
    "Do not rewrite the code, do not give a different solution, and do not use big-O notation.",
    "",
    `Language: ${step.language}`,
    `Current line ${step.line}: ${step.lineText.trim()}`,
    `Call stack (outermost first): ${step.callStack.join(" → ") || "(top level)"}`,
    "Variables at this step:",
    variables,
    "",
    "The code:",
    "```",
    code,
    "```"
  ].join("\n");
}

/**
 * Asks the student's Copilot to explain one step, passing the answer to `onText` as it
 * arrives. Every failure comes back as a sentence the panel can show; none is thrown.
 */
export async function explainStep(
  lm: LanguageModels | undefined,
  userMessage: (text: string) => unknown,
  step: StepToExplain,
  onText: (soFar: string) => void,
  token?: unknown
): Promise<Explained> {
  if (!lm || typeof lm.selectChatModels !== "function") return { ok: false, reason: NO_API };

  let models: ChatModel[];
  try {
    models = await lm.selectChatModels({ vendor: "copilot" });
  } catch (error) {
    return { ok: false, reason: `Copilot could not be reached: ${message(error)}` };
  }
  const model = models[0];
  if (!model) return { ok: false, reason: NO_MODEL };

  try {
    const response = await model.sendRequest([userMessage(buildPrompt(step))], { justification: JUSTIFICATION }, token);
    let text = "";
    for await (const fragment of response.text) {
      text += fragment;
      onText(text);
    }
    return text.trim() ? { ok: true, text, model: model.name } : { ok: false, reason: "Copilot gave an empty answer. Try again." };
  } catch (error) {
    return { ok: false, reason: refusal(error) };
  }
}

/** VS Code's own reasons, said plainly: the student declined, or Copilot refused. */
function refusal(error: unknown): string {
  const code = (error as { code?: string } | undefined)?.code;
  if (code === "NoPermissions") return "DSA Playback was not allowed to use Copilot. You can allow it the next time you press Explain.";
  if (code === "Blocked") return "Copilot is not taking requests right now (too many, or blocked). Try again in a minute.";
  if (code === "NotFound") return NO_MODEL;
  return `Copilot could not explain this step: ${message(error)}`;
}

const message = (error: unknown) => (error instanceof Error ? error.message : String(error));
