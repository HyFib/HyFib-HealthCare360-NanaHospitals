/**
 * The card/UPI terminal wired to a counter (pharmacy, canteen), remembered on this computer. Storage can be
 * unavailable (private mode, blocked): the id is then simply asked for again.
 */
const key = (counter: string) => `hc360.terminal.${counter}`;

export function loadTerminalId(counter: string): string {
  try {
    return localStorage.getItem(key(counter)) ?? '';
  } catch {
    return '';
  }
}

export function saveTerminalId(counter: string, terminalId: string): void {
  try {
    localStorage.setItem(key(counter), terminalId);
  } catch {
    // Not remembered; asked for again next time.
  }
}
