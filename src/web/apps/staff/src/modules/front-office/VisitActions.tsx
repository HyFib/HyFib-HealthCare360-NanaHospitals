import { Button, Menu, MenuItem, MenuList, MenuPopover, MenuTrigger, Text } from '@fluentui/react-components';
import { CheckmarkCircleRegular, DismissCircleRegular, MoreHorizontalRegular } from '@fluentui/react-icons';
import { ConfirmDialog, useNotify } from '@hc360/ui';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useState, type ReactNode } from 'react';
import { usePermission } from '@/lib/permissions';
import { ReasonDialog } from '@/modules/inpatient/ReasonDialog';
import type { VisitListItem } from '@/modules/patients/api';
import { CANCEL_VISIT_REASON_MAX, visitApi } from './api';

/**
 * A visit the front desk may still end: open or in progress. An in-patient visit belongs to its admission, which the
 * ward discharges or cancels (Inpatient), so it is not ended from a visit list.
 */
export function canEndVisit(visit: Pick<VisitListItem, 'status' | 'type'>): boolean {
  return (visit.status === 'Open' || visit.status === 'InProgress') && visit.type !== 'InPatient';
}

type Ending = { visit: VisitListItem; action: 'cancel' | 'complete'; open: boolean };

/**
 * Cancel or close a visit from a visit list (the Visits screen, the patient's visits): a row menu for staff with
 * `visits.manage`, on visits that are still open, and the dialogs it opens. Cancelling asks why (for example: the wrong
 * patient was checked in); closing asks to confirm. Spread `rowActions` into the DataTable and render `dialogs` beside it.
 */
export function useVisitActions(): {
  rowActions: ((visit: VisitListItem) => ReactNode) | undefined;
  dialogs: ReactNode;
} {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const canManage = usePermission('visits.manage');
  const [ending, setEnding] = useState<Ending | null>(null);
  // The visit is kept while the dialog closes, so its text does not change during the animation.
  const close = () => setEnding((current) => current && { ...current, open: false });

  // The Visits list, the patient's visits (and banner payer), and the queue and dashboard counts all change.
  const refresh = () =>
    Promise.all([
      queryClient.invalidateQueries({ queryKey: ['visits'] }),
      queryClient.invalidateQueries({ queryKey: ['patients', 'visits'] }),
      queryClient.invalidateQueries({ queryKey: ['front-office'] }),
    ]);

  const end = useMutation({
    mutationFn: ({ visit, action, reason }: { visit: VisitListItem; action: Ending['action']; reason?: string }) =>
      action === 'cancel' ? visitApi.cancel(visit.id, reason ?? '') : visitApi.complete(visit.id),
    onSuccess: (_, { visit, action }) => {
      notify.success(
        action === 'cancel' ? 'Visit cancelled' : 'Visit closed',
        `${visit.visitNumber} · ${visit.patientName}`,
      );
      close();
      void refresh();
    },
    // Cancelling shows the server's answer in its dialog; closing has no form to keep, so it says so in a toast.
    onError: (error, { action }) => {
      // Ended by someone else meanwhile: the list behind the message shows it as it is now.
      void refresh();
      if (action === 'complete') {
        close();
        notify.error(error, 'Could not close the visit');
      }
    },
  });

  const start = (visit: VisitListItem, action: Ending['action']) => {
    end.reset();
    setEnding({ visit, action, open: true });
  };

  const rowActions = canManage
    ? (visit: VisitListItem) =>
        canEndVisit(visit) ? (
          <Menu>
            <MenuTrigger disableButtonEnhancement>
              <Button
                size="small"
                appearance="subtle"
                icon={<MoreHorizontalRegular />}
                aria-label={`Actions for visit ${visit.visitNumber}`}
              />
            </MenuTrigger>
            <MenuPopover>
              <MenuList>
                <MenuItem icon={<CheckmarkCircleRegular />} onClick={() => start(visit, 'complete')}>
                  Close visit
                </MenuItem>
                <MenuItem icon={<DismissCircleRegular />} onClick={() => start(visit, 'cancel')}>
                  Cancel visit
                </MenuItem>
              </MenuList>
            </MenuPopover>
          </Menu>
        ) : null
    : undefined;

  const visit = ending?.visit;
  const dialogs = (
    <>
      <ReasonDialog
        open={(ending?.open ?? false) && ending?.action === 'cancel'}
        title={visit ? `Cancel visit ${visit.visitNumber}` : 'Cancel visit'}
        description={
          visit && (
            <>
              <Text weight="semibold">{visit.patientName}</Text> ({visit.uhid}), checked in for{' '}
              {visit.departmentName ?? 'a visit'}. Its queue token is cancelled and its charges not yet billed are
              withdrawn. A cancelled visit cannot be reopened.
            </>
          )
        }
        placeholder="For example: the wrong patient was checked in"
        hint="Kept with the visit, with your name and the time."
        confirmLabel="Cancel visit"
        destructive
        maxLength={CANCEL_VISIT_REASON_MAX}
        busy={end.isPending}
        error={end.error}
        onConfirm={(reason) => visit && end.mutate({ visit, action: 'cancel', reason })}
        onCancel={close}
      />
      <ConfirmDialog
        open={(ending?.open ?? false) && ending?.action === 'complete'}
        title={visit ? `Close visit ${visit.visitNumber}` : 'Close visit'}
        confirmLabel="Close visit"
        busy={end.isPending}
        onConfirm={() => visit && end.mutate({ visit, action: 'complete' })}
        onCancel={close}
      >
        {visit && (
          <>
            Mark the visit of <Text weight="semibold">{visit.patientName}</Text> ({visit.uhid}) as completed. Its queue
            token is finished and leaves the queue.
          </>
        )}
      </ConfirmDialog>
    </>
  );

  return { rowActions, dialogs };
}
