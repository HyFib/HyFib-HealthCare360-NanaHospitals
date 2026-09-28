import { Button, Caption1, Field, Select, Text, tokens } from '@fluentui/react-components';
import { CheckmarkRegular, SearchRegular } from '@fluentui/react-icons';
import { formatDateTime } from '@hc360/core';
import { DataTable, FilterBar, Page, PageHeader, Section, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { bloodApi, bloodKeys, bloodQueries, REACTION_STATUSES, type ReactionStatus, type TransfusionReactionDto } from '@/modules/blood-bank/api';
import { ReactionStatusBadge, SeverityBadge } from '@/modules/blood-bank/BloodBadges';
import { reactionStatusLabel, reactionTypeLabel, symptomLabel } from '@/modules/blood-bank/labels';
import { CloseReactionDialog } from '@/modules/blood-bank/ReactionDialogs';
import { NoAccess } from '@/modules/ot/shared/feedback';
import { useFacilityId, useLiveInvalidation } from '@/modules/ot/shared/hooks';

interface ReactionSearch {
  status?: ReactionStatus;
  page?: number;
}

export const Route = createFileRoute('/_app/blood-bank/reactions')({
  validateSearch: (s: Record<string, unknown>): ReactionSearch => ({
    status: REACTION_STATUSES.includes(s.status as ReactionStatus) ? (s.status as ReactionStatus) : undefined,
    page: typeof s.page === 'number' && s.page > 1 ? s.page : undefined,
  }),
  component: ReactionsPage,
});

/** Haemovigilance: reported reactions, their investigation and HvPI classification. */
function ReactionsPage() {
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const notify = useNotify();
  const queryClient = useQueryClient();
  const facilityId = useFacilityId();
  const canRead = usePermission('bloodbank.read');
  const canInvestigate = usePermission('bloodbank.reactions.investigate');
  const [closing, setClosing] = useState<TransfusionReactionDto | null>(null);
  const page = search.page ?? 1;
  const reactions = useQuery({ ...bloodQueries.reactions(search.status, page), enabled: canRead });

  useLiveInvalidation(facilityId ? `facility:${facilityId}` : null, 'bloodbank.changed', [['blood-bank', 'reactions']]);

  const investigate = useMutation({
    mutationFn: (id: string) => bloodApi.investigateReaction(id),
    onSuccess: () => {
      notify.success('Investigation started');
      void queryClient.invalidateQueries({ queryKey: bloodKeys.all });
    },
    onError: (error) => notify.error(error, 'Could not start the investigation'),
  });

  if (!canRead) {
    return (
      <Page>
        <NoAccess what="transfusion reactions" />
      </Page>
    );
  }

  return (
    <Page>
      <PageHeader
        title="Transfusion reactions"
        subtitle="Reported from the wards; investigated and classified by the blood bank (Haemovigilance Programme of India)."
        breadcrumbs={[{ label: 'Blood stock', onClick: () => void navigate({ to: '/blood-bank' }) }, { label: 'Reactions' }]}
      />
      <Section flush>
        <div style={{ padding: '0 16px 12px' }}>
          <FilterBar>
            <Field label="Status">
              <Select value={search.status ?? ''} onChange={(_, d) => void navigate({ search: { status: (d.value || undefined) as ReactionStatus | undefined } })}>
                <option value="">All</option>
                {REACTION_STATUSES.map((s) => (
                  <option key={s} value={s}>
                    {reactionStatusLabel[s]}
                  </option>
                ))}
              </Select>
            </Field>
          </FilterBar>
        </div>
        <DataTable
          ariaLabel="Transfusion reactions"
          rows={reactions.data?.items}
          loading={reactions.isLoading}
          error={reactions.error}
          onRetry={() => void reactions.refetch()}
          getRowId={(r) => r.id}
          onRowClick={(r) => void navigate({ to: '/blood-bank/requests/$id', params: { id: r.requestId } })}
          columns={[
            { key: 'when', header: 'Reported', render: (r) => `${formatDateTime(r.reportedAt)}${r.reportedByName ? ` · ${r.reportedByName}` : ''}` },
            {
              key: 'patient',
              header: 'Patient · unit',
              render: (r) => (
                <span>
                  <Text weight="semibold">{r.patientName}</Text>
                  <Caption1 block style={{ color: tokens.colorNeutralForeground3 }}>
                    {r.uhid} · unit {r.unitNumber}
                  </Caption1>
                </span>
              ),
            },
            { key: 'severity', header: 'Severity', render: (r) => <SeverityBadge severity={r.severity} /> },
            { key: 'symptoms', header: 'Signs', priority: 'low', render: (r) => r.symptoms.map((s) => symptomLabel[s]).join(', ') },
            { key: 'type', header: 'Classification', priority: 'low', render: (r) => reactionTypeLabel[r.type] },
            { key: 'status', header: 'Status', render: (r) => <ReactionStatusBadge status={r.status} /> },
          ]}
          rowActions={
            canInvestigate
              ? (r) =>
                  r.status === 'Reported' ? (
                    <Button size="small" icon={<SearchRegular />} disabled={investigate.isPending} onClick={() => investigate.mutate(r.id)}>
                      Start investigation
                    </Button>
                  ) : r.status === 'UnderInvestigation' ? (
                    <Button size="small" appearance="primary" icon={<CheckmarkRegular />} onClick={() => setClosing(r)}>
                      Close
                    </Button>
                  ) : null
              : undefined
          }
          emptyTitle="No transfusion reactions reported"
          emptyDescription="Reactions are reported from the issued units of a blood request."
          page={page}
          pageSize={25}
          totalCount={reactions.data?.totalCount}
          onPageChange={(next) => void navigate({ search: (prev) => ({ ...prev, page: next > 1 ? next : undefined }) })}
          size="small"
        />
      </Section>
      {closing && <CloseReactionDialog reaction={closing} onClose={() => setClosing(null)} />}
    </Page>
  );
}
