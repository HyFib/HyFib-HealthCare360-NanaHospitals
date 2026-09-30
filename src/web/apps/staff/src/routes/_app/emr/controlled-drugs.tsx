import { Field, Input, Text } from '@fluentui/react-components';
import { formatDateTime, todayIso } from '@hc360/core';
import { DataTable, FilterBar, Page, PageHeader, Section } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { addDaysIso } from '@/modules/emr/encounterForm';
import { prescriptionQueries } from '@/modules/emr/prescriptionsApi';

const DATE = /^\d{4}-\d{2}-\d{2}$/;

export const Route = createFileRoute('/_app/emr/controlled-drugs')({
  validateSearch: (s: Record<string, unknown>): { from?: string; to?: string; page?: number } => ({
    from: typeof s.from === 'string' && DATE.test(s.from) ? s.from : undefined,
    to: typeof s.to === 'string' && DATE.test(s.to) ? s.to : undefined,
    page: typeof s.page === 'number' && s.page > 1 ? s.page : undefined,
  }),
  component: ControlledDrugRegisterPage,
});

/** Prescribing register of Schedule H1/X medicines (ERX-06): one row per controlled line of every signed prescription. */
function ControlledDrugRegisterPage() {
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const today = todayIso();
  const from = search.from ?? addDaysIso(today, -30);
  const to = search.to ?? today;
  const register = useQuery(prescriptionQueries.register(from, to, search.page ?? 1));
  const set = (patch: { from?: string; to?: string; page?: number }) =>
    void navigate({ search: (prev) => ({ ...prev, ...patch }), replace: true });

  return (
    <Page>
      <PageHeader
        title="Controlled drug register"
        subtitle="Schedule H1 and X medicines prescribed, with the confirming doctor (Drugs and Cosmetics Rules)."
      />
      <Section flush>
        <div style={{ padding: '0 16px 12px' }}>
          <FilterBar>
            <Field label="From">
              <Input
                type="date"
                value={from}
                max={to}
                onChange={(_, d) => set({ from: d.value || undefined, page: undefined })}
              />
            </Field>
            <Field label="To">
              <Input
                type="date"
                value={to}
                min={from}
                max={today}
                onChange={(_, d) => set({ to: d.value || undefined, page: undefined })}
              />
            </Field>
          </FilterBar>
        </div>
        <DataTable
          ariaLabel="Controlled drug register"
          rows={register.data?.items}
          loading={register.isLoading}
          error={register.error}
          onRetry={() => void register.refetch()}
          getRowId={(r) => r.id}
          onRowClick={(r) =>
            void navigate({ to: '/emr/prescriptions/$prescriptionId', params: { prescriptionId: r.prescriptionId } })
          }
          size="small"
          columns={[
            { key: 'date', header: 'Signed', width: 170, render: (r) => formatDateTime(r.prescribedAt) },
            {
              key: 'rx',
              header: 'Prescription',
              width: 150,
              render: (r) => <Text font="monospace">{r.prescriptionNumber}</Text>,
            },
            {
              key: 'patient',
              header: 'Patient',
              render: (r) => `${r.patientName ?? '—'}${r.uhid ? ` · ${r.uhid}` : ''}`,
            },
            { key: 'drug', header: 'Medicine', render: (r) => `${r.drugName}${r.strength ? ` ${r.strength}` : ''}` },
            { key: 'schedule', header: 'Schedule', width: 90, render: (r) => r.schedule ?? '—' },
            { key: 'qty', header: 'Quantity', width: 90, align: 'end', render: (r) => r.quantity },
            { key: 'doctor', header: 'Doctor', priority: 'low', render: (r) => r.practitionerName ?? '—' },
          ]}
          emptyTitle="No controlled medicines prescribed in this period"
          page={search.page ?? 1}
          pageSize={25}
          totalCount={register.data?.totalCount}
          onPageChange={(page) => set({ page: page > 1 ? page : undefined })}
        />
      </Section>
    </Page>
  );
}
