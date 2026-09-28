import {
  Button,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Field,
  Select,
  Spinner,
  Text,
  Textarea,
} from '@fluentui/react-components';
import { AddRegular, MoneyRegular } from '@fluentui/react-icons';
import { formatDateOnly, formatInr, isApiError } from '@hc360/core';
import { DataTable, FilterBar, Page, PageHeader, Section, StatusBadge, useNotify } from '@hc360/ui';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { hrApi, hrKeys, hrQueries } from '@/modules/hr/api';
import { HR_PERMISSIONS as P, PAYROLL_VIEWERS } from '@/modules/hr/labels';
import { PAYROLL_NAV } from '@/modules/hr/nav';
import { currentMonth, monthLabel, MONTH_NAMES, previousMonth, yearOptions } from '@/modules/hr/shared/dates';
import { RequirePermission } from '@/modules/hr/shared/NoAccess';
import { SubNav } from '@/modules/hr/shared/SubNav';
import { FilterPad } from '@/modules/hr/shared/layout';

export const Route = createFileRoute('/_app/hr/payroll/')({
  validateSearch: (s: Record<string, unknown>): { year?: number; page?: number } => ({
    year: typeof s.year === 'number' && s.year >= 2000 && s.year <= 2100 ? s.year : undefined,
    page: typeof s.page === 'number' && s.page > 1 ? s.page : undefined,
  }),
  component: () => (
    <RequirePermission permission={PAYROLL_VIEWERS} what="payroll">
      <PayrollRunsPage />
    </RequirePermission>
  ),
});

function PayrollRunsPage() {
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const canProcess = usePermission(P.payrollProcess);
  const year = search.year ?? currentMonth().year;
  const [creating, setCreating] = useState(false);
  const runs = useQuery(hrQueries.payrollRuns(year, search.page ?? 1));

  return (
    <Page>
      <PageHeader
        title="Payroll"
        subtitle="One payroll a month: collect inputs, process, check, approve (by a second person), pay, then lock."
        icon={<MoneyRegular />}
        actions={
          canProcess && (
            <Button appearance="primary" icon={<AddRegular />} onClick={() => setCreating(true)}>
              New payroll
            </Button>
          )
        }
      />
      <SubNav items={PAYROLL_NAV} ariaLabel="Payroll screens" />
      <Section flush>
        <FilterPad>
          <FilterBar>
            <Field label="Year">
              <Select value={String(year)} onChange={(_, d) => void navigate({ search: { year: Number(d.value) } })}>
                {yearOptions().map((y) => (
                  <option key={y} value={y}>
                    {y}
                  </option>
                ))}
              </Select>
            </Field>
          </FilterBar>
        </FilterPad>
        <DataTable
          ariaLabel="Payroll runs"
          rows={runs.data?.items}
          loading={runs.isLoading}
          error={runs.error}
          onRetry={() => void runs.refetch()}
          getRowId={(r) => r.id}
          onRowClick={(r) => void navigate({ to: '/hr/payroll/runs/$runId', params: { runId: r.id } })}
          columns={[
            { key: 'month', header: 'Month', render: (r) => <Text weight="semibold">{monthLabel(r.year, r.month)}</Text> },
            {
              key: 'status',
              header: 'Status',
              render: (r) => (
                <span style={{ display: 'inline-flex', gap: 4, flexWrap: 'wrap' }}>
                  <StatusBadge status={r.status} />
                  {r.payslipsWithErrors > 0 && <StatusBadge status="Failed" label={`${r.payslipsWithErrors} with errors`} />}
                </span>
              ),
            },
            { key: 'employees', header: 'Employees', align: 'end', width: 100, render: (r) => r.employeeCount },
            { key: 'gross', header: 'Gross', align: 'end', priority: 'low', render: (r) => formatInr(r.totalGross, { whole: true }) },
            { key: 'deductions', header: 'Deductions', align: 'end', priority: 'low', render: (r) => formatInr(r.totalDeductions, { whole: true }) },
            { key: 'net', header: 'Net pay', align: 'end', render: (r) => formatInr(r.totalNet, { whole: true }) },
            { key: 'paid', header: 'Paid on', priority: 'low', render: (r) => (r.paidOn ? formatDateOnly(r.paidOn) : '—') },
          ]}
          emptyTitle={`No payroll for ${year} yet`}
          emptyDescription={canProcess ? 'Create the month’s payroll to start collecting inputs.' : undefined}
          page={search.page ?? 1}
          pageSize={25}
          totalCount={runs.data?.totalCount}
          onPageChange={(page) => void navigate({ search: (prev) => ({ ...prev, page: page > 1 ? page : undefined }) })}
        />
      </Section>
      {creating && <NewRunDialog onClose={() => setCreating(false)} />}
    </Page>
  );
}

function NewRunDialog({ onClose }: { onClose: () => void }) {
  const navigate = useNavigate();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const initial = previousMonth();
  const [year, setYear] = useState(initial.year);
  const [month, setMonth] = useState(initial.month);
  const [notes, setNotes] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const now = currentMonth();
  const future = year > now.year || (year === now.year && month > now.month);

  const create = async () => {
    setBusy(true);
    setError(null);
    try {
      const id = await hrApi.createRun({ year, month, notes: notes.trim() || null });
      notify.success('Payroll created', `${monthLabel(year, month)}: add the month's inputs, then process.`);
      void queryClient.invalidateQueries({ queryKey: hrKeys.payroll });
      void navigate({ to: '/hr/payroll/runs/$runId', params: { runId: String(id) } });
    } catch (err) {
      if (isApiError(err) && (err.code === 'payroll.exists' || err.code === 'payroll.future_month')) {
        setError(err.userMessage);
      } else {
        notify.error(err, 'Could not create the payroll');
      }
    } finally {
      setBusy(false);
    }
  };

  return (
    <Dialog open onOpenChange={(_, d) => !d.open && !busy && onClose()}>
      <DialogSurface style={{ maxWidth: 520 }}>
        <form
          onSubmit={(event) => {
            event.preventDefault();
            void create();
          }}
        >
          <DialogBody>
            <DialogTitle>New payroll</DialogTitle>
            <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
              <div style={{ display: 'flex', gap: 12, flexWrap: 'wrap' }}>
                <Field label="Month" validationMessage={error ?? (future ? 'Payroll cannot be created for a future month.' : undefined)} validationState={error || future ? 'error' : 'none'}>
                  <Select value={String(month)} onChange={(_, d) => setMonth(Number(d.value))} autoFocus>
                    {MONTH_NAMES.map((name, index) => (
                      <option key={name} value={index + 1}>
                        {name}
                      </option>
                    ))}
                  </Select>
                </Field>
                <Field label="Year">
                  <Select value={String(year)} onChange={(_, d) => setYear(Number(d.value))}>
                    {yearOptions(2, 0).map((y) => (
                      <option key={y} value={y}>
                        {y}
                      </option>
                    ))}
                  </Select>
                </Field>
              </div>
              <Field label="Notes">
                <Textarea value={notes} onChange={(_, d) => setNotes(d.value)} rows={2} maxLength={1000} />
              </Field>
            </DialogContent>
            <DialogActions>
              <Button onClick={onClose} disabled={busy}>
                Cancel
              </Button>
              <Button type="submit" appearance="primary" disabled={busy || future} icon={busy ? <Spinner size="tiny" /> : undefined}>
                Create payroll
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
      </DialogSurface>
    </Dialog>
  );
}
