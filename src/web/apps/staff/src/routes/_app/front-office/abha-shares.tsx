import { Button, Caption1, Text } from '@fluentui/react-components';
import { CheckmarkRegular, OpenRegular, PersonAddRegular } from '@fluentui/react-icons';
import { formatRelative } from '@hc360/core';
import { ConfirmDialog, DataTable, Page, PageHeader, Section, StatusBadge, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { FeatureOffState } from '@/lib/FeatureOffState';
import { useFeatureOn } from '@/lib/features';
import { usePermission } from '@/lib/permissions';
import {
  abhaShareApi,
  abhaShareQueries,
  matchedByLabel,
  tokenLabel,
  type AbhaShareRow,
} from '@/modules/front-office/abhaShares';

export const Route = createFileRoute('/_app/front-office/abha-shares')({
  component: AbhaSharesPage,
});

const age = (yearOfBirth: number | null) => (yearOfBirth ? `${new Date().getFullYear() - yearOfBirth}Y` : '—');

/**
 * Scan and share (ABDM M1): today's patients who scanned the hospital's QR code with their ABHA app, by token. One the
 * hospital already knows is matched to their record; anyone else is registered with the form filled in from their ABHA.
 */
function AbhaSharesPage() {
  const navigate = useNavigate();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const switchedOn = useFeatureOn('abdm');
  const canSeeContact = usePermission('patients.contact.read');
  const shares = useQuery({ ...abhaShareQueries.today(), enabled: switchedOn });
  const [dismissing, setDismissing] = useState<AbhaShareRow | null>(null);

  const done = () => void queryClient.invalidateQueries({ queryKey: ['abdm', 'shares'] });
  const registered = useMutation({
    mutationFn: (share: AbhaShareRow) => abhaShareApi.registered(share.id, share.patient!.id),
    onSuccess: (share) => {
      notify.success(
        `Token ${tokenLabel(share.tokenNumber)} done`,
        `${share.patient?.fullName} (${share.patient?.uhid})`,
      );
      done();
    },
    onError: (error) => notify.error(error, 'Could not update the share'),
  });
  const dismiss = useMutation({
    mutationFn: (share: AbhaShareRow) => abhaShareApi.dismiss(share.id),
    onSuccess: () => {
      setDismissing(null);
      done();
    },
    onError: (error) => notify.error(error, 'Could not dismiss the share'),
  });

  return (
    <Page>
      <PageHeader
        title="ABHA scan and share"
        subtitle="Patients who scanned the hospital's QR code with their ABHA app today, by token number. Updates on its own."
      />
      {!switchedOn ? (
        <FeatureOffState feature="abdm" />
      ) : (
        <Section flush>
          <DataTable
            ariaLabel="ABHA profiles shared today"
            rows={shares.data}
            loading={shares.isLoading}
            error={shares.error}
            onRetry={() => void shares.refetch()}
            getRowId={(s) => s.id}
            columns={[
              {
                key: 'token',
                header: 'Token',
                width: 90,
                render: (s) => <Text font="monospace">{tokenLabel(s.tokenNumber)}</Text>,
              },
              {
                key: 'name',
                header: 'Name',
                render: (s) => (
                  <>
                    <Text weight="semibold">{s.name}</Text>
                    <Caption1 block>
                      {age(s.yearOfBirth)} / {s.gender ?? '—'}
                      {s.phoneMasked ? ` · ${s.phoneMasked}` : ''}
                    </Caption1>
                  </>
                ),
              },
              {
                key: 'abha',
                header: 'ABHA',
                priority: 'low',
                render: (s) => (
                  <>
                    <Text>{s.abhaAddress}</Text>
                    {s.abhaNumberMasked && <Caption1 block>{s.abhaNumberMasked}</Caption1>}
                  </>
                ),
              },
              { key: 'counter', header: 'Counter', priority: 'low', width: 100, render: (s) => s.counter },
              { key: 'shared', header: 'Shared', width: 120, render: (s) => formatRelative(s.sharedAt) },
              {
                key: 'record',
                header: 'Record',
                render: (s) =>
                  s.patient ? (
                    <>
                      <Text>
                        {s.patient.fullName} ({s.patient.uhid})
                      </Text>
                      {s.status === 'Waiting' && s.matchedBy && (
                        <Caption1 block>Matched by {matchedByLabel(s.matchedBy)}</Caption1>
                      )}
                    </>
                  ) : (
                    '—'
                  ),
              },
              { key: 'status', header: 'Status', width: 110, render: (s) => <StatusBadge status={s.status} /> },
            ]}
            rowActions={(s) =>
              s.status === 'Waiting' && (
                <span style={{ display: 'inline-flex', gap: 4 }}>
                  {s.patient ? (
                    <>
                      <Button
                        size="small"
                        icon={<OpenRegular />}
                        onClick={() =>
                          void navigate({ to: '/patients/$patientId', params: { patientId: s.patient!.id } })
                        }
                      >
                        Open record
                      </Button>
                      <Button
                        size="small"
                        icon={<CheckmarkRegular />}
                        disabled={registered.isPending}
                        onClick={() => registered.mutate(s)}
                      >
                        Done
                      </Button>
                    </>
                  ) : (
                    canSeeContact && (
                      <Button
                        size="small"
                        appearance="primary"
                        icon={<PersonAddRegular />}
                        onClick={() => void navigate({ to: '/patients/register', search: { share: s.id } })}
                      >
                        Register
                      </Button>
                    )
                  )}
                  <Button size="small" appearance="subtle" onClick={() => setDismissing(s)}>
                    Take off queue
                  </Button>
                </span>
              )
            }
            emptyTitle="No one has shared their ABHA today"
            size="small"
          />
        </Section>
      )}
      <ConfirmDialog
        open={dismissing !== null}
        title="Take this share off the queue?"
        confirmLabel="Take off"
        busy={dismiss.isPending}
        onConfirm={() => dismissing && dismiss.mutate(dismissing)}
        onCancel={() => setDismissing(null)}
      >
        Token {dismissing ? tokenLabel(dismissing.tokenNumber) : ''} ({dismissing?.name}) will leave the queue without
        being registered. If they scan again they get a new token.
      </ConfirmDialog>
    </Page>
  );
}
