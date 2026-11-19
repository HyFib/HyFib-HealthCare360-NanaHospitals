/**
 * The Damm check digit (H. Michael Damm, 2004), as the server works it out (`Platform/Numbering/DammCheckDigit.cs`):
 * one digit from the digits before it, which catches every single wrong digit and every swap of two neighbouring
 * digits. Tube numbers end in one, and so does any number format with {CHK}.
 */

// Damm's quasigroup of order 10: row the digit so far, column the next digit.
const TABLE: readonly (readonly number[])[] = [
  [0, 3, 1, 7, 5, 9, 8, 6, 4, 2],
  [7, 0, 9, 2, 1, 5, 4, 8, 6, 3],
  [4, 2, 0, 6, 8, 7, 1, 3, 5, 9],
  [1, 7, 5, 0, 9, 8, 3, 4, 2, 6],
  [6, 1, 2, 3, 0, 4, 5, 9, 7, 8],
  [3, 6, 7, 4, 2, 0, 9, 5, 8, 1],
  [5, 8, 6, 9, 7, 2, 0, 1, 3, 4],
  [8, 9, 4, 5, 3, 6, 2, 0, 1, 7],
  [9, 4, 3, 8, 6, 1, 7, 2, 0, 5],
  [2, 5, 8, 1, 4, 3, 6, 7, 9, 0],
];

function run(text: string): number {
  let interim = 0;
  for (const c of text) {
    if (c >= '0' && c <= '9') {
      interim = TABLE[interim]![c.charCodeAt(0) - 48]!;
    }
  }
  return interim;
}

/** The check digit of the digits in `text`, in order; anything else in it is passed over. */
export function dammCheckDigit(text: string): string {
  return String(run(text));
}

/** True when the digits of `text` (at least two) end in the check digit of the digits before them. */
export function dammVerifies(text: string): boolean {
  const digits = [...text].filter((c) => c >= '0' && c <= '9').length;
  return digits >= 2 && run(text) === 0;
}
