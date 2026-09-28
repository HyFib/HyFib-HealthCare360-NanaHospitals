import { Field, Select } from '@fluentui/react-components';
import { DataTable, StatusBadge } from '@hc360/ui';
import { useQueries, useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { hrQueries, type PayslipListItem } from './api';
import type { PickedEmployee } from './EmployeePicker';
import type { PayrollRunStatus } from './labels';
import { PayslipDialog } from './PayslipView';
import { currentMonth, monthLabel, yearOptions } from './shared/dates';
import { Amount, RevealButton, Stack, Toolbar } from './shared/layout';

interface Row {
  runId: string;
  year: number;
  month: number;
  runStatus: PayrollRunStatus;
  payslip: PayslipListItem;
}

/** An employee's payslips for a year, across the month's payroll runs (payroll staff only). */
export function EmployeePayslips({ employee }: { employee: PickedEmployee }) {
  const [year, setYear] = useState(currentMonth().year);
  const [revealed, setRevealed] = useState(false);
  const [open, setOpen] = useState<string | null>(null);
  const runs = useQuery(hrQueries.payrollRuns(year, 1, 12));
  const processed = (runs.data?.items ?? []).filter((run) => run.status !== 'Draft');
  const slips = useQueries({
    queries: processed.map((run) => hrQueries.payslips(run.id, { search: employee.code, pageSize: 5 })),
  });

  const rows: Row[] = [];
  processed.forEach((run, index) => {
    const payslip = slips[index]?.data?.items.find((item) => item.employeeId === employee.id);
    if (payslip) {
      rows.push({ runId: run.id, year: run.year, month: run.month, runStatus: run.status, payslip });
    }
  });
  rows.sort((a, b) => b.year - a.year || b.month - a.month);
  const loading = runs.isLoading || slips.some((q) => q.isLoading);
  const error = runs.error ?? slips.find((q) => q.error)?.error;

  return (
    <Stack gap={12}>
      <Toolbar spaced={false}>
        <Field label="Year">
          <Select value={String(year)} onChange={(_, data) => setYear(Number(data.value))}>
            {yearOptions().map((y) => (
              <option key={y} value={y}>
                {y}
              </option>
            ))}
          </Select>
        </Field>
        <span style={{ alignSelf: 'flex-end' }}>
          <RevealButton revealed={revealed} onChange={setRevealed} />
        </span>
      </Toolbar>
      <DataTable
        ariaLabel="Payslips"
        rows={loading ? undefined : rows}
        loading={loading}
        error={error}
        onRetry={() => void runs.refetch()}
        getRowId={(r) => r.payslip.id}
        onRowClick={(r) => setOpen(r.payslip.id)}
        columns={[
          { key: 'month', header: 'Month', render: (r) => monthLabel(r.year, r.month) },
          { key: 'status', header: 'Payroll', render: (r) => <StatusBadge status={r.runStatus} /> },
          { key: 'days', header: 'Paid days', align: 'end', render: (r) => r.payslip.payableDays },
          { key: 'lop', header: 'Loss of pay', align: 'end', priority: 'low', render: (r) => r.payslip.lopDays },
          { key: 'gross', header: 'Gross', align: 'end', priority: 'low', render: (r) => <Amount value={r.payslip.grossEarnings} hidden={!revealed} /> },
          { key: 'net', header: 'Net pay', align: 'end', render: (r) => <Amount value={r.payslip.netPay} hidden={!revealed} /> },
          { key: 'errors', header: '', width: 90, render: (r) => (r.payslip.hasErrors ? <StatusBadge status="Failed" label="Has errors" /> : null) },
        ]}
        emptyTitle={`No payslips in ${year}`}
        emptyDescription="Payslips appear once the month's payroll is processed."
        size="small"
      />
      <PayslipDialog payslipId={open} onClose={() => setOpen(null)} />
    </Stack>
  );
}
