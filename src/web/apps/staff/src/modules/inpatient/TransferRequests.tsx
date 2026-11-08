import { Badge, Button, Caption1, makeStyles, Text, tokens } from '@fluentui/react-components';
import { ArrowSwapRegular } from '@fluentui/react-icons';
import { formatDateTime, formatTime } from '@hc360/core';
import { EmptyState, ErrorState, Section, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { inpatientKeys } from './api';
import { ReasonDialog } from './ReasonDialog';
import {
  TRANSFER_STATUS_LABEL,
  wardCareApi,
  wardCareKeys,
  wardCareQueries,
  type TransferRequestDto,
} from './wardCareApi';

const useStyles = makeStyles({
  list: { display: 'flex', flexDirection: 'column', gap: '10px' },
  card: {
    padding: '10px 12px',
    borderRadius: tokens.borderRadiusMedium,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    display: 'flex',
    flexDirection: 'column',
    gap: '4px',
  },
  overdue: { borderLeft: `4px solid ${tokens.colorPaletteRedBorderActive}` },
  row: { display: 'flex', gap: '8px', alignItems: 'center', flexWrap: 'wrap', justifyContent: 'space-between' },
  actions: { display: 'flex', gap: '6px', flexWrap: 'wrap' },
  summary: { whiteSpace: 'pre-wrap' },
});

type Closing = { request: TransferRequestDto; action: 'decline' | 'cancel' };

/**
 * Transfer requests of a ward (both what it is asked to take and what it asked to send), or of the whole hospital: the
 * receiving ward's nurse in charge or a doctor accepts or declines; the sending ward may cancel. Urgent moves to or from
 * critical care show when their acceptance is due, and are flagged once the hour has passed.
 */
export function TransferRequestList({ wardId, canSend }: { wardId: string | null; canSend: boolean }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const requests = useQuery(wardCareQueries.transfers(wardId));
  const [closing, setClosing] = useState<Closing | null>(null);
  // The server says whether this user may decide; the menu's permissions say whether the screen offers it at all.
  const inCharge = usePermission('nursing.in_charge');
  const mayDecide = inCharge || canSend;

  const refresh = () => {
    void queryClient.invalidateQueries({ queryKey: wardCareKeys.all });
    void queryClient.invalidateQueries({ queryKey: inpatientKeys.all });
  };
  const accept = useMutation({
    mutationFn: (r: TransferRequestDto) =>
      wardCareApi.acceptTransfer(r.id, { note: null, transferredAt: null, version: r.version }),
    onSuccess: (r) => {
      notify.success(
        r.isUrgent ? 'Acceptance recorded' : 'Transfer accepted',
        `${r.patientName} — ${r.toWardName}, bed ${r.toBedLabel}.`,
      );
      refresh();
    },
    onError: (error) => notify.error(error, 'Could not accept the transfer'),
  });
  const close = useMutation({
    mutationFn: ({ request, action, reason }: Closing & { reason: string }) =>
      action === 'decline'
        ? wardCareApi.declineTransfer(request.id, { reason, version: request.version })
        : wardCareApi.cancelTransfer(request.id, { reason, version: request.version }),
    onSuccess: (r) => {
      notify.success(
        r.status === 'Declined' ? 'Transfer declined' : 'Transfer cancelled',
        'The held bed is free again.',
      );
      setClosing(null);
      refresh();
    },
  });

  if (requests.error) {
    return <ErrorState error={requests.error} onRetry={() => void requests.refetch()} />;
  }
  const rows = requests.data ?? [];
  return (
    <Section title="Transfer requests" description="Moves between wards waiting for the receiving ward.">
      {requests.isLoading ? null : rows.length === 0 ? (
        <EmptyState
          icon={<ArrowSwapRegular />}
          title="No transfers waiting"
          description="Requests to and from this ward appear here."
        />
      ) : (
        <div className={styles.list}>
          {rows.map((r) => (
            <div key={r.id} className={`${styles.card} ${r.isAcceptanceOverdue ? styles.overdue : ''}`}>
              <div className={styles.row}>
                <Text weight="semibold">
                  {r.patientName} <Caption1>({r.uhid})</Caption1>
                </Text>
                <span className={styles.actions}>
                  {r.isUrgent && (
                    <Badge appearance="filled" color="danger">
                      Urgent
                    </Badge>
                  )}
                  <Badge appearance="tint" color={r.isAcceptanceOverdue ? 'danger' : 'informative'}>
                    {r.isAcceptanceOverdue ? 'Acceptance overdue' : TRANSFER_STATUS_LABEL[r.status]}
                  </Badge>
                </span>
              </div>
              <Caption1>
                {r.fromWardName ?? '—'}
                {r.fromBedLabel ? ` bed ${r.fromBedLabel}` : ''} → {r.toWardName} bed {r.toBedLabel} · asked by{' '}
                {r.requestedByName ?? '—'} {formatDateTime(r.requestedAt)}
                {r.acceptanceDueAt && r.status === 'MovedAwaitingAcceptance'
                  ? ` · accept by ${formatTime(r.acceptanceDueAt)}`
                  : ''}
                {r.holdUntil ? ` · bed held until ${formatTime(r.holdUntil)}` : ''}
              </Caption1>
              <Caption1>Reason: {r.reason}</Caption1>
              <Caption1 className={styles.summary}>Summary: {r.clinicalSummary}</Caption1>
              <div className={styles.actions}>
                {mayDecide && r.canDecide && (
                  <Button
                    size="small"
                    appearance="primary"
                    disabled={accept.isPending}
                    onClick={() => accept.mutate(r)}
                  >
                    {r.status === 'MovedAwaitingAcceptance' ? 'Record acceptance' : 'Accept and move'}
                  </Button>
                )}
                {mayDecide && r.canDecide && r.status === 'Pending' && (
                  <Button size="small" onClick={() => setClosing({ request: r, action: 'decline' })}>
                    Decline
                  </Button>
                )}
                {canSend && r.status === 'Pending' && (
                  <Button size="small" appearance="subtle" onClick={() => setClosing({ request: r, action: 'cancel' })}>
                    Cancel request
                  </Button>
                )}
              </div>
            </div>
          ))}
        </div>
      )}
      <ReasonDialog
        open={closing !== null}
        title={closing?.action === 'decline' ? 'Decline this transfer?' : 'Cancel this transfer request?'}
        description="The held bed is released. The sending ward is told why."
        confirmLabel={closing?.action === 'decline' ? 'Decline' : 'Cancel request'}
        busy={close.isPending}
        error={close.error}
        onConfirm={(reason) => closing && close.mutate({ ...closing, reason })}
        onCancel={() => {
          setClosing(null);
          close.reset();
        }}
      />
    </Section>
  );
}
