import { onPrintQueued } from '@hc360/core';
import { useNotify } from '@hc360/ui';
import { useEffect } from 'react';

/** What the toast says when HC360 Desktop could not tell why (an outcome with no message of its own). */
const FALLBACK = 'The printer is paused, offline or has a problem, so the document is waiting in its queue.';

/**
 * Tells the person when a print was accepted but is waiting behind a paused, offline or faulty printer, instead of the
 * "printed" every screen would otherwise imply. Mounted once in the root route: the print goes out through `printHtml` from
 * some forty screens, and each would have to read the outcome.
 */
export function PrintQueuedNotice() {
  const notify = useNotify();

  useEffect(
    () => onPrintQueued((outcome) => notify.warning('Not printed yet', outcome.message ?? FALLBACK)),
    // `notify` is rebuilt on every render and only dispatches to the app-wide toaster.
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [],
  );

  return null;
}
