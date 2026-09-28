import { useNotify } from '@hc360/ui';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useState, type ReactNode } from 'react';
import { usePermission } from '@/lib/permissions';
import { telehealthApi, telehealthKeys, type TelehealthLinkIssued } from './api';
import { CancelSessionDialog, LinkIssuedDialog, type SessionRef } from './SessionDialogs';

export interface SessionActions {
  canManage: boolean;
  resendLink: (sessionId: string) => void;
  resending: boolean;
  cancel: (session: SessionRef) => void;
  /** Link and cancel dialogs; render once on the screen. */
  dialogs: ReactNode;
}

/** Re-send the join link and cancel a consultation — the same way from the list and from the console. */
export function useSessionActions(): SessionActions {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const canManage = usePermission('telehealth.sessions.manage');
  const [issued, setIssued] = useState<TelehealthLinkIssued | null>(null);
  const [cancelling, setCancelling] = useState<SessionRef | null>(null);

  const resend = useMutation({
    mutationFn: (sessionId: string) => telehealthApi.resendLink(sessionId),
    onSuccess: (link) => {
      setIssued(link);
      void queryClient.invalidateQueries({ queryKey: telehealthKeys.all });
    },
    onError: (error) => notify.error(error, 'Could not send a new link'),
  });

  return {
    canManage,
    resendLink: (sessionId) => resend.mutate(sessionId),
    resending: resend.isPending,
    cancel: setCancelling,
    dialogs: (
      <>
        <LinkIssuedDialog link={issued} onClose={() => setIssued(null)} />
        <CancelSessionDialog session={cancelling} onClose={() => setCancelling(null)} />
      </>
    ),
  };
}
