import { ConfirmDialog } from '@hc360/ui';
import { useBlocker } from '@tanstack/react-router';
import { useRef, useState, type ReactNode } from 'react';

/** The record an editor shows, from the screen's `?edit=` search parameter. */
function editing(search: unknown): unknown {
  return typeof search === 'object' && search !== null ? (search as { edit?: unknown }).edit : undefined;
}

export interface EditorGuard {
  /** Closes without asking: after a save, or when nothing was changed. */
  close: () => void;
  /** Closes, asking first when there are unsaved changes (the close button, Cancel, Esc, a click outside). */
  requestClose: () => void;
  dialogs: ReactNode;
}

/**
 * Keeps typing from being lost: closing an editor with unsaved changes asks first, and so does leaving the screen (a menu
 * link, Back) or closing the window. Pressing Back while an editor is open closes the editor, so that is asked too.
 */
export function useEditorGuard({
  dirty,
  busy,
  onClose,
}: {
  dirty: boolean;
  busy: boolean;
  onClose: () => void;
}): EditorGuard {
  const leaving = useRef(false);
  const [confirming, setConfirming] = useState(false);
  const blocker = useBlocker({
    shouldBlockFn: ({ current, next }) =>
      dirty &&
      !leaving.current &&
      (current.pathname !== next.pathname || editing(current.search) !== editing(next.search)),
    enableBeforeUnload: () => dirty && !leaving.current,
    withResolver: true,
  });

  const close = () => {
    leaving.current = true;
    onClose();
  };

  const requestClose = () => {
    if (busy) {
      return;
    }
    if (dirty) {
      setConfirming(true);
    } else {
      close();
    }
  };

  const dialogs = (
    <>
      <ConfirmDialog
        open={confirming}
        title="Discard your changes?"
        confirmLabel="Discard changes"
        destructive
        onConfirm={() => {
          setConfirming(false);
          close();
        }}
        onCancel={() => setConfirming(false)}
      >
        What you changed here has not been saved.
      </ConfirmDialog>
      <ConfirmDialog
        open={blocker.status === 'blocked'}
        title="Leave without saving?"
        confirmLabel="Discard changes"
        destructive
        onConfirm={() => blocker.proceed?.()}
        onCancel={() => blocker.reset?.()}
      >
        What you changed in this editor has not been saved.
      </ConfirmDialog>
    </>
  );

  return { close, requestClose, dialogs };
}
