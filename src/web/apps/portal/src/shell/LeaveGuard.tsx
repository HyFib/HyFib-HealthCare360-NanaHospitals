import { askToLeaveWith, useReturnFocus, wouldLoseChanges } from '@hc360/ui';
import { useBlocker, type ShouldBlockFn } from '@tanstack/react-router';
import { useCallback, useEffect, useState } from 'react';
import { ConfirmDialog } from '@/components/ConfirmDialog';
import { useT } from '@/i18n';

const beforeUnload = () => wouldLoseChanges();

/**
 * Asks "Leave without saving?" before the portal leaves a page with unsaved changes (a link, Back, signing out), and
 * has the browser ask before the window closes or reloads (SRS UX-08). Forms register themselves through
 * `useUnsavedChanges` (@/lib/unsavedChanges) or `useLeaveGuard` (@hc360/ui); this one blocker, in the root route, asks
 * for all of them.
 */
export function LeaveGuard() {
  const t = useT();
  const [asked, setAsked] = useState<{ answer: (leave: boolean) => void } | null>(null);
  const returnFocus = useReturnFocus();

  const shouldBlockFn = useCallback<ShouldBlockFn>(
    async ({ current, next, action }) => {
      if (!wouldLoseChanges({ current, next, action })) {
        return false;
      }
      // The question opens once the element the user was on is where focus returns to on "Stay".
      await returnFocus();
      return true;
    },
    [returnFocus],
  );
  const blocker = useBlocker({ shouldBlockFn, enableBeforeUnload: beforeUnload, withResolver: true });

  // Signing out asks the same question before it starts (confirmLeaving).
  useEffect(
    () =>
      askToLeaveWith(async () => {
        await returnFocus();
        return new Promise<boolean>((resolve) => setAsked({ answer: resolve }));
      }),
    [returnFocus],
  );

  const answer = (leave: boolean) => {
    if (blocker.status === 'blocked') {
      if (leave) {
        blocker.proceed();
      } else {
        blocker.reset();
      }
    }
    asked?.answer(leave);
    setAsked(null);
  };

  return (
    <ConfirmDialog
      open={blocker.status === 'blocked' || asked !== null}
      title={t('common.unsaved.leaveTitle')}
      confirmLabel={t('common.unsaved.discard')}
      dismissLabel={t('common.unsaved.stay')}
      destructive
      onConfirm={() => answer(true)}
      onDismiss={() => answer(false)}
    >
      {t('common.unsaved.body')}
    </ConfirmDialog>
  );
}
