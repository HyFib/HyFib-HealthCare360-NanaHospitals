import { useUnsavedChanges, type Place, type UnsavedChangesGuard } from '@hc360/ui';

/** The record an editor shows, from the screen's `?edit=` search parameter. */
function editing(search: unknown): unknown {
  return typeof search === 'object' && search !== null ? (search as { edit?: unknown }).edit : undefined;
}

/** Leaving the screen loses the editor, and so does Back while it is open: that closes it (`?edit=` changes). */
function leavesEditor(current: Place, next: Place): boolean {
  return current.pathname !== next.pathname || editing(current.search) !== editing(next.search);
}

export type EditorGuard = UnsavedChangesGuard;

/**
 * Keeps typing from being lost (SRS UX-08): closing an editor with unsaved changes asks first, and so does leaving the
 * screen (a menu link, Back) or closing the window. Pressing Back while an editor is open closes the editor, so that is
 * asked too.
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
  return useUnsavedChanges({ dirty, busy, onClose, leaves: leavesEditor });
}
