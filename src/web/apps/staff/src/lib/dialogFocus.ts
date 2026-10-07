import type { KeyboardEvent } from 'react';

/**
 * Keeps keyboard focus inside an open dialog or drawer when the element holding it goes away: a button disabled while
 * its request runs, an element removed or hidden. Focus would otherwise fall to the page, and Tabster (Fluent's focus
 * manager) then returns it to the last restore target the user focused, such as a menu button behind the dialog: the
 * dialog stops being modal and screen readers no longer see it. The dialog itself takes focus instead, and Tab moves on
 * from there. Closing a dialog is not affected: its surface is gone by then, and Fluent returns focus to the opener.
 */
export function keepFocusInOpenDialogs(doc: Document = document): () => void {
  const onFocusOut = (event: FocusEvent) => {
    const target = event.target;
    if (event.relatedTarget !== null || !(target instanceof Element)) {
      return;
    }
    const dialog = target.closest<HTMLElement>('[aria-modal="true"]');
    if (!dialog || dialog === target) {
      return;
    }
    // Tabster restores focus on a timer, after this.
    queueMicrotask(() => {
      if (dialog.isConnected && doc.activeElement === doc.body) {
        dialog.focus();
      }
    });
  };
  doc.addEventListener('focusout', onFocusOut, true);
  return () => doc.removeEventListener('focusout', onFocusOut, true);
}

/**
 * Key handler for the content of a dialog whose request runs in that content: Escape does not close the dialog while
 * `busy`. (Fluent closes a dialog only on an Escape that nothing inside it handled.)
 */
export function ignoreEscapeWhile(busy: boolean | undefined) {
  return (event: KeyboardEvent<HTMLElement>) => {
    if (busy && event.key === 'Escape') {
      event.preventDefault();
    }
  };
}
