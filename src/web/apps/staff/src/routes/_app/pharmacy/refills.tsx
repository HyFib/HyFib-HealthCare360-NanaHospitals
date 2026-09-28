import { Badge, Button, Caption1, Field, Select, Text, tokens } from '@fluentui/react-components';
import { CheckmarkRegular, DismissRegular, GlobeRegular, PersonRegular } from '@fluentui/react-icons';
import { formatDateOnly, formatRelative, humanize } from '@hc360/core';
import { ConfirmDialog, DataTable, FilterBar, Page, PageHeader, Section, StatusBadge, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { ReasonDialog } from '@/modules/inventory/ReasonDialog';
import { pharmacyApi, pharmacyKeys, pharmacyQueries, REFILL_STATUSES, type RefillRequestDto, type RefillStatus } from '@/modules/pharmacy/api';
import { usePharmacyLive } from '@/modules/pharmacy/counter';

export const Route = createFileRoute('/_app/pharmacy/refills')({
  validateSearch: (s: Record<string, unknown>): { status?: RefillStatus; page?: number } => ({
    status: REFILL_STATUSES.includes(s.status as RefillStatus) ? (s.status as RefillStatus) : undefined,
    page: typeof s.page === 'number' && s.page > 1 ? s.page : undefined,
  }),
  component: RefillsPage,
});

/** Refill requests for chronic prescriptions (PH-05, PP-05), from the patient portal or the counter. */
function RefillsPage() {
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [approving, setApproving] = useState<RefillRequestDto | null>(null);
  const [rejecting, setRejecting] = useState<RefillRequestDto | null>(null);
  usePharmacyLive();
  const status = search.status ?? 'Requested';
  const refills = useQuery(pharmacyQueries.refills(status, search.page ?? 1));

  const refresh = () => {
    void queryClient.invalidateQueries({ queryKey: pharmacyKeys.refills });
    void queryClient.invalidateQueries({ queryKey: pharmacyKeys.queue });
    void queryClient.invalidateQueries({ queryKey: pharmacyKeys.dashboard });
  };

  const approve = useMutation({
    mutationFn: (request: RefillRequestDto) => pharmacyApi.approveRefill(request.id),
    onSuccess: (result, request) => {
      setApproving(null);
      refresh();
      notify.success(`Refill for ${request.patientName} is in the queue`, 'The patient gets an SMS/WhatsApp that it is being prepared.');
      void navigate({ to: '/pharmacy/queue/$entryId', params: { entryId: result.queueEntryId } });
    },
    onError: (error) => {
      setApproving(null);
      notify.error(error, 'Could not approve the refill');
    },
  });
  const reject = useMutation({
    mutationFn: ({ request, reason }: { request: RefillRequestDto; reason: string }) => pharmacyApi.rejectRefill(request.id, reason),
    onSuccess: () => {
      setRejecting(null);
      refresh();
      notify.success('Refill request rejected');
    },
  });

  return (
    <Page>
      <PageHeader
        title="Refill requests"
        subtitle="Chronic-medication refills asked for on the patient portal or at the counter. Approving puts a new prescription entry in the queue."
      />
      <Section flush>
        <div style={{ padding: '0 16px 12px' }}>
          <FilterBar>
            <Field label="Status">
              <Select value={status} onChange={(_, d) => void navigate({ search: { status: d.value === 'Requested' ? undefined : (d.value as RefillStatus) } })}>
                {REFILL_STATUSES.map((s) => (
                  <option key={s} value={s}>
                    {s === 'Requested' ? 'Waiting for a decision' : humanize(s)}
                  </option>
                ))}
              </Select>
            </Field>
          </FilterBar>
        </div>
        <DataTable<RefillRequestDto>
          ariaLabel="Refill requests"
          rows={refills.data?.items}
          loading={refills.isLoading}
          error={refills.error}
          onRetry={() => void refills.refetch()}
          getRowId={(r) => r.id}
          size="small"
          columns={[
            {
              key: 'patient',
              header: 'Patient',
              render: (r) => (
                <span>
                  <Text weight="semibold">{r.patientName}</Text> <Caption1>{r.uhid}</Caption1>
                </span>
              ),
            },
            {
              key: 'rx',
              header: 'Prescription',
              render: (r) => (
                <Button appearance="transparent" size="small" onClick={() => void navigate({ to: '/pharmacy/queue/$entryId', params: { entryId: r.queueEntryId } })}>
                  {r.prescriptionNumber}
                </Button>
              ),
            },
            {
              key: 'channel',
              header: 'From',
              priority: 'low',
              render: (r) => (
                <Badge appearance="tint" icon={r.channel === 'Portal' ? <GlobeRegular /> : <PersonRegular />}>
                  {r.channel === 'Portal' ? 'Patient portal' : 'Counter'}
                </Badge>
              ),
            },
            { key: 'when', header: 'Requested', render: (r) => formatRelative(r.requestedAt) },
            { key: 'pickup', header: 'Wants it on', render: (r) => (r.preferredPickupDate ? formatDateOnly(r.preferredPickupDate) : 'Any day') },
            {
              key: 'notes',
              header: 'Notes',
              priority: 'low',
              render: (r) => (r.rejectReason ? <Caption1 style={{ color: tokens.colorPaletteRedForeground1 }}>{r.rejectReason}</Caption1> : (r.notes ?? '—')),
            },
            { key: 'status', header: 'Status', render: (r) => <StatusBadge status={r.status} label={r.status === 'Requested' ? 'Waiting' : undefined} /> },
          ]}
          rowActions={(r) =>
            r.status === 'Requested' ? (
              <span style={{ display: 'inline-flex', gap: 4 }}>
                <Button size="small" appearance="primary" icon={<CheckmarkRegular />} onClick={() => setApproving(r)}>
                  Approve
                </Button>
                <Button size="small" icon={<DismissRegular />} onClick={() => setRejecting(r)}>
                  Reject
                </Button>
              </span>
            ) : r.refillEntryId ? (
              <Button size="small" onClick={() => void navigate({ to: '/pharmacy/queue/$entryId', params: { entryId: r.refillEntryId! } })}>
                Open refill
              </Button>
            ) : null
          }
          emptyTitle={status === 'Requested' ? 'No refill requests waiting' : 'Nothing here'}
          emptyDescription="Patients ask for refills on the portal; the counter can raise one from a dispensed prescription."
          page={search.page ?? 1}
          pageSize={25}
          totalCount={refills.data?.totalCount}
          onPageChange={(p) => void navigate({ search: (prev) => ({ ...prev, page: p > 1 ? p : undefined }) })}
        />
      </Section>

      <ConfirmDialog
        open={approving !== null}
        title={`Approve the refill for ${approving?.patientName ?? ''}?`}
        confirmLabel="Approve and queue"
        busy={approve.isPending}
        onConfirm={() => approving && approve.mutate(approving)}
        onCancel={() => setApproving(null)}
      >
        One of the authorised refills of prescription {approving?.prescriptionNumber} is used, and the full prescription goes into the dispensing queue. The patient
        is told it is being prepared.
      </ConfirmDialog>
      {rejecting && (
        <ReasonDialog
          title={`Reject the refill for ${rejecting.patientName}?`}
          confirmLabel="Reject"
          destructive
          busy={reject.isPending}
          error={reject.error}
          placeholder="e.g. Needs a review with the doctor first"
          onSubmit={(reason) => reject.mutate({ request: rejecting, reason })}
          onClose={() => setRejecting(null)}
        >
          The patient sees this reason on the portal.
        </ReasonDialog>
      )}
    </Page>
  );
}
