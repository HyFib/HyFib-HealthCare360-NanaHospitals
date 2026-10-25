import { Button, Field, Input } from '@fluentui/react-components';
import { PrintRegular } from '@fluentui/react-icons';
import { formatDateTime, todayIso } from '@hc360/core';
import { DataTable, FilterBar, Page, PageHeader, Section, StatusBadge } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useServerDocuments } from '@/lib/documents';
import { CAUSE_LABELS, erApi, erQueries, MLC_READERS, type MlcRegisterRow } from '@/modules/emergency/api';
import { RequirePermission } from '@/modules/hr/shared/NoAccess';

const DAY = /^\d{4}-\d{2}-\d{2}$/;

export const Route = createFileRoute('/_app/emergency/mlc')({
  validateSearch: (s: Record<string, unknown>): { from?: string; to?: string } => ({
    from: typeof s.from === 'string' && DAY.test(s.from) ? s.from : undefined,
    to: typeof s.to === 'string' && DAY.test(s.to) ? s.to : undefined,
  }),
  component: () => (
    <RequirePermission permission={MLC_READERS} what="the MLC register">
      <MlcRegisterPage />
    </RequirePermission>
  ),
});

function MlcRegisterPage() {
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const register = useQuery(erQueries.register(search.from, search.to));
  const { print } = useServerDocuments();

  return (
    <Page>
      <PageHeader title="MLC register" subtitle="Medico-legal cases and their intimation to the police" />
      <Section flush>
        <div style={{ padding: '8px 16px 12px' }}>
          <FilterBar>
            <Field label="From">
              <Input
                type="date"
                value={search.from ?? ''}
                max={todayIso()}
                onChange={(_, d) =>
                  void navigate({ search: { ...search, from: DAY.test(d.value) ? d.value : undefined } })
                }
              />
            </Field>
            <Field label="To">
              <Input
                type="date"
                value={search.to ?? ''}
                max={todayIso()}
                onChange={(_, d) =>
                  void navigate({ search: { ...search, to: DAY.test(d.value) ? d.value : undefined } })
                }
              />
            </Field>
          </FilterBar>
        </div>
        <DataTable<MlcRegisterRow>
          ariaLabel="Medico-legal case register"
          rows={register.data}
          loading={register.isLoading}
          error={register.error}
          onRetry={() => void register.refetch()}
          getRowId={(r) => r.id}
          size="small"
          emptyTitle="No medico-legal cases in these days"
          columns={[
            { key: 'number', header: 'MLC no.', width: 140, render: (r) => r.number },
            { key: 'identified', header: 'Identified', width: 160, render: (r) => formatDateTime(r.identifiedAt) },
            { key: 'patient', header: 'Patient', render: (r) => `${r.patientName} · ${r.uhid}` },
            { key: 'cause', header: 'Alleged cause', render: (r) => CAUSE_LABELS[r.cause] },
            { key: 'station', header: 'Police station', render: (r) => r.policeStation },
            {
              key: 'intimated',
              header: 'Police informed',
              width: 190,
              render: (r) =>
                r.intimatedAt ? (
                  `${formatDateTime(r.intimatedAt)} · ${r.receivedBy ?? ''}`
                ) : (
                  <StatusBadge
                    status="Pending"
                    tone={r.intimationOverdue ? 'danger' : 'warning'}
                    label={r.intimationOverdue ? 'Overdue' : 'Not yet'}
                  />
                ),
            },
            { key: 'doctor', header: 'Doctor', render: (r) => r.doctorName },
            {
              key: 'print',
              header: '',
              width: 60,
              render: (r) => (
                <Button
                  appearance="subtle"
                  icon={<PrintRegular />}
                  aria-label={`Print MLC ${r.number}`}
                  onClick={() => print.mutate(erApi.mlcDocument(r.caseId))}
                />
              ),
            },
          ]}
        />
      </Section>
    </Page>
  );
}
