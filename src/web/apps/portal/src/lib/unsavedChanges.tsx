import { useUnsavedChangesState, type UnsavedChangesOptions, type UnsavedChangesState } from '@hc360/ui';
import type { ReactNode } from 'react';
import { ConfirmDialog } from '@/components/ConfirmDialog';
import { useT } from '@/i18n';

export interface UnsavedChangesGuard extends UnsavedChangesState {
  /** "Discard your changes?": render it next to the sheet (`Sheet` does, given the guard). */
  dialogs: ReactNode;
}

/**
 * The portal's unsaved-change guard (SRS UX-08), with the portal's own translated question: closing a form with unsaved
 * changes asks "Discard your changes?" (Not now, the close button, Esc, a tap outside), and leaving the page or closing
 * the window asks too (LeaveGuard). Pass it to `Sheet` as `guard`.
 */
export function useUnsavedChanges(options: UnsavedChangesOptions): UnsavedChangesGuard {
  const t = useT();
  const state = useUnsavedChangesState(options);
  return {
    ...state,
    dialogs: (
      <ConfirmDialog
        open={state.confirming}
        title={t('common.unsaved.discardTitle')}
        confirmLabel={t('common.unsaved.discard')}
        dismissLabel={t('common.unsaved.keepEditing')}
        destructive
        onConfirm={state.discard}
        onDismiss={state.keepEditing}
      >
        {t('common.unsaved.body')}
      </ConfirmDialog>
    ),
  };
}
