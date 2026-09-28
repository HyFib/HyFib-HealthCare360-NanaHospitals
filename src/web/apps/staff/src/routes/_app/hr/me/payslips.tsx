import { DocumentTextRegular } from '@fluentui/react-icons';
import { DataTable, Section, StatusBadge } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute } from '@tanstack/react-router';
import { useState } from 'react';
import { hrQueries } from '@/modules/hr/api';
import { PayslipDialog } from '@/modules/hr/PayslipView';
import { SelfServicePage } from '@/modules/hr/SelfServicePage';
import { monthLabel } from '@/modules/hr/shared/dates';
import { Amount, RevealButton, Stack, Toolbar } from '@/modules/hr/shared/layout';

export const Route = createFileRoute('/_app/hr/me/payslips')({
  component: () => (
    <SelfServicePage title="My payslips" subtitle="Salary slips of approved months. Open one to view or print it." icon={<DocumentTextRegular />}>
      {() => <MyPayslips />}
    </SelfServicePage>
  ),
});

/** Own payslips; amounts stay hidden on screen until shown (colleagues and patients may be near the screen). */
function MyPayslips() {
  const payslips = useQuery(hrQueries.myPayslips());
  const [revealed, setRevealed] = useState(false);
  const [open, setOpen] = useState<string | null>(null);

  return (
    <Stack gap={12}>
      <Toolbar spaced={false}>
        <RevealButton revealed={revealed} onChange={setRevealed} />
      </Toolbar>
      <Section flush>
        <DataTable
          ariaLabel="My payslips"
          rows={payslips.data}
          loading={payslips.isLoading}
          error={payslips.error}
          onRetry={() => void payslips.refetch()}
          getRowId={(p) => p.id}
          onRowClick={(p) => setOpen(p.id)}
          columns={[
            { key: 'month', header: 'Month', render: (p) => monthLabel(p.year, p.month) },
            { key: 'status', header: 'Salary', render: (p) => <StatusBadge status={p.status} label={p.status === 'Approved' ? 'Approved, not yet paid' : p.status === 'Locked' ? 'Paid' : undefined} tone={p.status === 'Locked' ? 'success' : undefined} /> },
            { key: 'gross', header: 'Gross', align: 'end', priority: 'low', render: (p) => <Amount value={p.grossEarnings} hidden={!revealed} /> },
            { key: 'deductions', header: 'Deductions', align: 'end', priority: 'low', render: (p) => <Amount value={p.totalDeductions} hidden={!revealed} /> },
            { key: 'net', header: 'Net pay', align: 'end', render: (p) => <Amount value={p.netPay} hidden={!revealed} /> },
          ]}
          emptyTitle="No payslips yet"
          emptyDescription="Payslips appear here once the month's payroll is approved."
        />
      </Section>
      <PayslipDialog payslipId={open} mine onClose={() => setOpen(null)} />
    </Stack>
  );
}
