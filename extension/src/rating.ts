// The one request for a rating. Kept free of the vscode module so the rules can be
// unit-tested; extension.ts supplies the storage, the question and the browser.

/** Successful runs before the first ask. */
export const FIRST_ASK_AFTER = 5;

/** More successful runs before asking again, after "Later". */
export const ASK_AGAIN_AFTER = 20;

export const RATE = "Rate";
export const LATER = "Later";
export const NEVER = "Don't ask again";

/** Counted on the student's machine only. Nothing is sent anywhere. */
export interface RatingState {
  runs: number;
  /** The run count at which to ask. */
  askAt: number;
  /** Rated, or told not to ask again. */
  done: boolean;
}

export const INITIAL: RatingState = { runs: 0, askAt: FIRST_ASK_AFTER, done: false };

export interface RatingHost {
  read(): RatingState | undefined;
  write(state: RatingState): PromiseLike<void>;
  ask(message: string, ...choices: string[]): PromiseLike<string | undefined>;
  openReviews(): void;
}

/** What happens after an answer. Closing the notification without choosing counts as Later. */
export function afterAnswer(state: RatingState, answer: string | undefined): RatingState {
  if (answer === RATE || answer === NEVER) return { ...state, done: true };
  return { ...state, askAt: state.runs + ASK_AGAIN_AFTER };
}

export class RatingPrompt {
  /** One question on screen at a time, however many runs finish while it is open. */
  private asking = false;

  constructor(private readonly host: RatingHost) {}

  /** Called after every run that completed. Resolves once any question is answered. */
  async succeeded(): Promise<void> {
    const before = this.host.read() ?? INITIAL;
    if (before.done) return;

    const state = { ...before, runs: before.runs + 1 };
    await this.host.write(state);
    if (state.runs < state.askAt || this.asking) return;

    this.asking = true;
    try {
      const answer = await this.host.ask(
        "Is DSA Playback helping you learn? A rating helps other students find it.", RATE, LATER, NEVER);
      if (answer === RATE) this.host.openReviews();
      // Re-read: runs that finished while the question was open were counted meanwhile.
      await this.host.write(afterAnswer(this.host.read() ?? state, answer));
    } finally {
      this.asking = false;
    }
  }
}
