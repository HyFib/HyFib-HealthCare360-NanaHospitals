import {
  askToLeaveWith,
  ConfirmDialog,
  LEAVE_WITHOUT_SAVING,
  leaveQuestionFor,
  useReturnFocus,
  wouldLoseChanges,
  type LeaveQuestion,
} from '@hc360/ui';
import { useBlocker, type ShouldBlockFn } from '@tanstack/react-router';
import { useCallback, useEffect, useState } from 'react';

const beforeUnload = () => wouldLoseChanges();

/**
 * Asks "Leave without saving?" before the app leaves a screen with unsaved changes (a menu link, Back, a link inside a
 * dialog, signing out), and has the browser ask before the window closes or reloads (SRS UX-08). Forms register
 * themselves through `useUnsavedChanges` or `useLeaveGuard` (@hc360/ui); this one blocker, in the root route, asks for
 * all of them.
 */
export function LeaveGuard() {
  const [question, setQuestion] = useState<LeaveQuestion>(LEAVE_WITHOUT_SAVING);
  const [asked, setAsked] = useState<{ answer: (leave: boolean) => void } | null>(null);
  const returnFocus = useReturnFocus();

  const shouldBlockFn = useCallback<ShouldBlockFn>(
    async ({ current, next }) => {
      if (!wouldLoseChanges({ current, next })) {
        return false;
      }
      setQuestion(leaveQuestionFor({ current, next }) ?? LEAVE_WITHOUT_SAVING);
      // The question opens once the element the user was on is where focus returns to on "Stay".
      await returnFocus();
      return true;
    },
    [returnFocus],
  );
  const blocker = useBlocker({ shouldBlockFn, enableBeforeUnload: beforeUnload, withResolver: true });

  // Signing out and switching facility ask the same question before they start (confirmLeaving).
  useEffect(
    () =>
      askToLeaveWith(async (navigation) => {
        setQuestion(leaveQuestionFor(navigation) ?? LEAVE_WITHOUT_SAVING);
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
      title={question.title}
      confirmLabel={question.confirmLabel}
      cancelLabel="Stay"
      destructive={question.destructive ?? true}
      onConfirm={() => answer(true)}
      onCancel={() => answer(false)}
    >
      {question.body}
    </ConfirmDialog>
  );
}
