// For tests of buttons that wait for a request, such as Print: a request the test finishes when it chooses, and focus
// where a browser leaves it while the button waits.

/** A promise the test resolves when it chooses. */
export function deferred<T = void>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((done) => {
    resolve = done;
  });
  return { promise, resolve };
}

/**
 * Does what a browser does when the focused button becomes disabled: focus falls to the page, with a focusout whose
 * relatedTarget is null (inside an open dialog, Fluent then moves it behind the dialog). jsdom keeps focus on the
 * disabled button, and its blur() ignores a disabled button, so the button is enabled for as long as blur() takes to
 * send the event a browser sends.
 */
export function dropFocusIfDisabled(element: HTMLElement): void {
  if (element instanceof HTMLButtonElement && element.disabled && element === element.ownerDocument.activeElement) {
    element.disabled = false;
    element.blur();
    element.disabled = true;
  }
}
