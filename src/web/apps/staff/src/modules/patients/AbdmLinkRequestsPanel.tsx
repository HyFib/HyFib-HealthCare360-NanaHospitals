import { Button, Caption1, makeStyles, Text, tokens } from '@fluentui/react-components';
import { LinkRegular } from '@fluentui/react-icons';
import { formatDateTime, isApiError } from '@hc360/core';
import { DataTable, StatusBadge, useNotify } from '@hc360/ui';
import { queryOptions, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useRef } from 'react';
import { api } from '@/lib/api';
import { useFeatureOn } from '@/lib/features';
import { usePermission } from '@/lib/permissions';
import { abdmLinksPath, abdmLinkWaiting, type AbdmLinkStatus, type AbdmLinkView } from '@/modules/emr/abdmLinking';
import type { PatientDetail } from './api';

const STATUS_TEXT: Record<AbdmLinkStatus, string> = {
  AwaitingToken: 'Waiting for ABDM',
  Sent: 'Waiting for ABDM',
  Linked: 'Linked',
  Failed: 'Not linked',
};

/** While a request awaits ABDM's answer, the list is asked again this often. */
const LINKS_REFRESH_MS = 15_000;

const linkQueries = {
  list: (patientId: string) =>
    queryOptions({
      queryKey: ['abdm', 'links', patientId],
      queryFn: ({ signal }) => api.get<AbdmLinkView[]>(`/api/v1/abdm/patients/${patientId}/links`, { signal }),
    }),
};

const useStyles = makeStyles({
  intro: { display: 'block', marginBottom: '12px', maxWidth: '72ch', color: tokens.colorNeutralForeground3 },
  cell: { display: 'flex', flexDirection: 'column', gap: '2px', paddingBlock: '6px', whiteSpace: 'normal' },
  muted: { color: tokens.colorNeutralForeground3 },
  heading: { display: 'block', marginBottom: '8px' },
  section: { marginBottom: '24px' },
});

/**
 * Whether the server's answer to "link again" is lost (the WiFi dropped, the server failed or is still at work on it):
 * the request may have reached ABDM, so the same Idempotency-Key is sent again, and the server answers as it answered.
 */
const answerLost = (error: unknown) =>
  !isApiError(error) || error.status === 0 || error.status === 409 || error.status >= 500;

/**
 * The patient's records sent to their ABHA (ABDM M2, HIP-initiated linking): each request, whether the hospital made it
 * itself as the record was completed (a note signed, a report released, a prescription issued) or staff did, where it
 * has got to, and why ABDM did not link it. A request ABDM refused, or never answered (the server marks it failed after
 * a while), is linked again with "Link again", as a new request. Shown while ABDM is switched on, to staff with
 * `abdm.export`; asked again every few seconds while a request awaits ABDM's answer.
 */
export function AbdmLinkRequestsPanel({ patient }: { patient: PatientDetail }) {
  const styles = useStyles();
  const switchedOn = useFeatureOn('abdm');
  const canLink = usePermission('abdm.export');
  const notify = useNotify();
  const queryClient = useQueryClient();
  const links = useQuery({
    ...linkQueries.list(patient.id),
    enabled: switchedOn && canLink,
    refetchInterval: (query) => (query.state.data?.some((l) => abdmLinkWaiting(l.status)) ? LINKS_REFRESH_MS : false),
  });

  // One Idempotency-Key per failed request while the server's answer is lost, as for linking a record.
  const attempts = useRef(new Map<string, string>());
  const again = useMutation({
    mutationFn: (link: AbdmLinkView) => {
      const idempotencyKey = attempts.current.get(link.id) ?? crypto.randomUUID();
      attempts.current.set(link.id, idempotencyKey);
      return api.post<AbdmLinkView>(`${abdmLinksPath}/${link.id}/retry`, undefined, { idempotencyKey });
    },
    onSuccess: (view, link) => {
      attempts.current.delete(link.id);
      void queryClient.invalidateQueries({ queryKey: ['abdm', 'links', patient.id] });
      if (view.status === 'Failed') {
        notify.warning('ABDM did not link the records', view.error ?? view.errorCode ?? 'Link them again later.');
      } else {
        notify.info(
          'Sent to ABDM again',
          `It links the records to ${view.abhaAddress}; this list shows when it answers.`,
        );
      }
    },
    onError: (error, link) => {
      if (!answerLost(error)) {
        attempts.current.delete(link.id);
      }

      notify.error(error, 'Could not link the records again');
    },
  });

  if (!switchedOn || !canLink) {
    return null;
  }

  return (
    <div className={styles.section}>
      <Text as="h3" weight="semibold" className={styles.heading}>
        Linked to the patient&apos;s ABHA
      </Text>
      <Caption1 className={styles.intro}>
        Records sent to the patient&apos;s ABHA, where they see them in their PHR app: by the hospital as each record is
        completed, or by staff. A request ABDM did not link can be linked again.
      </Caption1>
      <DataTable
        ariaLabel="Records sent to the patient's ABHA"
        rows={links.data}
        loading={links.isLoading}
        error={links.error}
        onRetry={() => void links.refetch()}
        getRowId={(l) => l.id}
        columns={[
          {
            key: 'when',
            header: 'Sent',
            width: 170,
            render: (l) => (
              <div className={styles.cell}>
                <Text>{formatDateTime(l.createdAt)}</Text>
                <Caption1 className={styles.muted}>{l.automatic ? 'Automatically' : 'By staff'}</Caption1>
              </div>
            ),
          },
          {
            key: 'records',
            header: 'Records',
            render: (l) => (
              <div className={styles.cell}>
                {l.records.map((r) => (
                  <Text key={r.referenceNumber}>{r.display}</Text>
                ))}
                <Caption1 className={styles.muted}>{l.abhaAddress}</Caption1>
              </div>
            ),
          },
          {
            key: 'status',
            header: 'Status',
            render: (l) => (
              <div className={styles.cell}>
                <StatusBadge status={STATUS_TEXT[l.status]} />
                {l.status === 'Failed' && (l.error ?? l.errorCode) && (
                  <Caption1 className={styles.muted}>{l.error ?? l.errorCode}</Caption1>
                )}
                {l.retryable && (
                  <div>
                    <Button
                      size="small"
                      icon={<LinkRegular />}
                      disabled={again.isPending}
                      onClick={() => again.mutate(l)}
                    >
                      Link again
                    </Button>
                  </div>
                )}
              </div>
            ),
          },
        ]}
        emptyTitle="No records have been sent to the patient's ABHA"
        size="small"
      />
    </div>
  );
}
