import type { UserEvent } from '@testing-library/user-event';

/*
 * The Keystroke-Level Model (Card, Moran & Newell, "The Psychology of Human-Computer Interaction", 1983): the time an
 * expert takes for a task without errors is the sum of its operators' times. The tallies come from the interaction
 * itself: every click, key and selection the test performs through `keystrokeLevelModel` is counted as the operators a
 * person would need for it, and nothing else is. See docs/testing/QUICK-REGISTRATION-KLM.md.
 */

/** Seconds per operator: K for an average non-secretary typist (40 wpm). */
export const KLM_SECONDS = { K: 0.28, P: 1.1, B: 0.1, H: 0.4, M: 1.35 } as const;

export type KlmOperator = keyof typeof KLM_SECONDS;
type Device = 'keyboard' | 'mouse';

export interface KlmStep {
  /** What the person does, e.g. "Field: first name". */
  label: string;
  /** Its operators in order, repeats counted, e.g. "M H P 2B 6K". */
  operators: string;
  seconds: number;
}

/**
 * Wraps user-event so that each action is also counted as KLM operators:
 *
 * - `step(label, kind)`: one mental operator M, for a field to fill or a decision to make (CMN's rule 0, with rules
 *   1–4 removing the M's inside one cognitive unit: a name or a number typed in one go has one M).
 * - `click(element)`: H to the mouse if the hand is on the keyboard, P to point at it, B B to press and release.
 * - `selectOption(select, value)`: a click on the list and a click on the option (P B B, P B B).
 * - `keyboard(text)`: H to the keyboard if the hand is on the mouse, and one K per key: each character, each `{Key}`,
 *   and a held key (`{Control>}`) once.
 */
export function keystrokeLevelModel(user: UserEvent, { handOn = 'keyboard' as Device } = {}) {
  const counts: Record<KlmOperator, number> = { K: 0, P: 0, B: 0, H: 0, M: 0 };
  const steps: KlmStep[] = [];
  let hand: Device = handOn;
  let current: KlmOperator[] | null = null;

  const add = (operator: KlmOperator, times = 1) => {
    counts[operator] += times;
    for (let i = 0; i < times; i++) {
      current?.push(operator);
    }
  };
  const moveHandTo = (device: Device) => {
    if (hand !== device) {
      add('H');
      hand = device;
    }
  };

  return {
    /** A field to fill or a decision to make (one M), then what the person does for it. */
    async step(label: string, run: () => Promise<void>) {
      current = [];
      add('M');
      await run();
      steps.push({
        label,
        operators: grouped(current),
        seconds: round(current.reduce((sum, operator) => sum + KLM_SECONDS[operator], 0)),
      });
      current = null;
    },

    async click(element: Element) {
      moveHandTo('mouse');
      add('P');
      add('B', 2);
      await user.click(element);
    },

    async selectOption(select: HTMLSelectElement, value: string) {
      moveHandTo('mouse');
      add('P');
      add('B', 2);
      add('P');
      add('B', 2);
      await user.selectOptions(select, value);
    },

    async keyboard(text: string) {
      moveHandTo('keyboard');
      add('K', keystrokes(text));
      await user.keyboard(text);
    },

    counts: () => ({ ...counts }),
    steps: () => [...steps],
    /** The modelled time in seconds. */
    seconds: () => round(seconds(counts)),
    /** The operators, their times and the total, as a table for the test's output. */
    report(title: string): string {
      const lines = [
        title,
        ...steps.map((s) => `  ${s.label.padEnd(36)} ${s.operators.padEnd(22)} ${s.seconds.toFixed(2)} s`),
      ];
      lines.push(`  ${'-'.repeat(68)}`);
      for (const operator of Object.keys(KLM_SECONDS) as KlmOperator[]) {
        const n = counts[operator];
        lines.push(
          `  ${operator}  ${String(n).padStart(3)} × ${KLM_SECONDS[operator].toFixed(2)} s = ${(n * KLM_SECONDS[operator]).toFixed(2)} s`,
        );
      }
      lines.push(`  Total ${round(seconds(counts)).toFixed(2)} s`);
      return lines.join('\n');
    },
  };
}

/** Keys pressed for a user-event keyboard text: characters, `{Key}` and held keys once; releases (`{/Key}`) are free. */
export function keystrokes(text: string): number {
  return (text.match(/\{[^}]*\}|\[[^\]]*\]|./g) ?? []).filter((token) => !/^[{[]\//.test(token)).length;
}

/** "M K K K" → "M 3K". */
function grouped(operators: readonly KlmOperator[]): string {
  const runs: string[] = [];
  for (let i = 0; i < operators.length;) {
    let j = i;
    while (j < operators.length && operators[j] === operators[i]) {
      j++;
    }
    runs.push(j - i > 1 ? `${j - i}${operators[i]}` : operators[i]!);
    i = j;
  }
  return runs.join(' ');
}

function seconds(counts: Record<KlmOperator, number>): number {
  return (Object.keys(counts) as KlmOperator[]).reduce((sum, op) => sum + counts[op] * KLM_SECONDS[op], 0);
}

function round(value: number): number {
  return Math.round(value * 100) / 100;
}
