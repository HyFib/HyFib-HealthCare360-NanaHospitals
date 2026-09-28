import {
  Button,
  Caption1,
  DrawerBody,
  DrawerHeader,
  DrawerHeaderTitle,
  Field,
  makeStyles,
  OverlayDrawer,
  SearchBox,
  Select,
  Switch,
  Text,
  tokens,
} from '@fluentui/react-components';
import { ArrowDownloadRegular, DismissRegular, MoneyRegular, PeopleRegular, WalletRegular } from '@fluentui/react-icons';
import { formatDateOnly, formatInr } from '@hc360/core';
import { DataTable, EmptyState, FilterBar, KpiCard, Page, PageHeader, Section, useNotify } from '@hc360/ui';
import { useMutation, useQuery } from '@tanstack/react-query';
import { createFileRoute } from '@tanstack/react-router';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { canteenApi, canteenQueries, type StaffBalanceDto } from '@/modules/canteen/api';
import { PaymentDialog, SettleDialog, StatementPanel } from '@/modules/canteen/CreditDialogs';
import { monthLabel, recentMonths } from '@/modules/canteen/rules';
import { istMonth, previousMonth } from '@/modules/inpatient/time';

export const Route = createFileRoute('/_app/canteen/credit')({
  component: StaffCreditPage,
});

const useStyles = makeStyles({
  kpis: { display: 'grid', gap: '12px', gridTemplateColumns: 'repeat(auto-fill, minmax(190px, 1fr))' },
  monthEnd: { display: 'flex', gap: '8px', flexWrap: 'wrap', alignItems: 'flex-end' },
  owes: { fontWeight: tokens.fontWeightSemibold },
  credit: { color: tokens.colorPaletteGreenForeground1 },
});

/** Staff canteen credit (CAN-03): who owes what, monthly statements, counter repayments and the payroll deduction. */
function StaffCreditPage() {
  const styles = useStyles();
  const notify = useNotify();
  const canManage = usePermission('canteen.credit.manage');
  const [outstandingOnly, setOutstandingOnly] = useState(true);
  const [term, setTerm] = useState('');
  const [selected, setSelected] = useState<StaffBalanceDto | null>(null);
  const [paying, setPaying] = useState<StaffBalanceDto | 'new' | null>(null);
  const [settling, setSettling] = useState(false);
  const [currentMonth] = useState(() => istMonth());
  const [exportPeriod, setExportPeriod] = useState(() => previousMonth(istMonth()));
  const balances = useQuery({ ...canteenQueries.balances(outstandingOnly), enabled: canManage });

  const exportCsv = useMutation({
    mutationFn: async () => {
      const blob = await canteenApi.exportCsv(exportPeriod);
      const url = URL.createObjectURL(blob);
      const link = document.createElement('a');
      link.href = url;
      link.download = `canteen-staff-credit-${exportPeriod}.csv`;
      document.body.appendChild(link);
      link.click();
      link.remove();
      setTimeout(() => URL.revokeObjectURL(url), 1000);
    },
    onSuccess: () => notify.success(`Payroll file for ${monthLabel(exportPeriod)} downloaded`, 'Send it to HR for salary deduction.'),
    onError: (error) => notify.error(error, 'Could not download the payroll file'),
  });

  if (!canManage) {
    return (
      <Page maxWidth={720}>
        <EmptyState icon={<WalletRegular />} title="Staff credit is managed by the canteen manager and HR" />
      </Page>
    );
  }

  const needle = term.trim().toLowerCase();
  const rows = (balances.data ?? []).filter((b) => !needle || b.employeeCode.toLowerCase().includes(needle) || b.employeeName?.toLowerCase().includes(needle));
  const owed = (balances.data ?? []).filter((b) => b.balance > 0);
  const total = owed.reduce((sum, b) => sum + b.balance, 0);

  return (
    <Page>
      <PageHeader
        title="Staff credit"
        icon={<WalletRegular />}
        subtitle="Canteen purchases on credit are deducted from salary at month end"
        actions={
          <>
            <Button icon={<MoneyRegular />} onClick={() => setPaying('new')}>
              Record payment
            </Button>
            <Button appearance="primary" icon={<PeopleRegular />} onClick={() => setSettling(true)}>
              Settle a month with payroll
            </Button>
          </>
        }
      />
      <div className={styles.kpis}>
        <KpiCard label="Owed by staff" value={balances.data ? formatInr(total) : '…'} icon={<WalletRegular />} hint={balances.data ? `${owed.length} employees` : undefined} />
      </div>
      <Section title="Payroll file" description="CSV of each employee's balance for HR's salary deduction">
        <div className={styles.monthEnd}>
          <Field label="Month">
            <Select value={exportPeriod} onChange={(_, d) => setExportPeriod(d.value)}>
              {recentMonths(currentMonth, 7).map((m) => (
                <option key={m} value={m}>
                  {monthLabel(m)}
                </option>
              ))}
            </Select>
          </Field>
          <Button icon={<ArrowDownloadRegular />} disabled={exportCsv.isPending} onClick={() => exportCsv.mutate()}>
            Download CSV
          </Button>
        </div>
      </Section>
      <Section flush>
        <div style={{ padding: '0 16px 12px' }}>
          <FilterBar>
            <Field label="Find" style={{ flex: '1 1 240px' }}>
              <SearchBox value={term} onChange={(_, d) => setTerm(d.value)} placeholder="Employee code or name" />
            </Field>
            <Switch checked={outstandingOnly} onChange={(_, d) => setOutstandingOnly(d.checked)} label="Only employees with a balance" />
          </FilterBar>
        </div>
        <DataTable
          ariaLabel="Staff credit balances"
          rows={balances.data ? rows : undefined}
          loading={balances.isLoading}
          error={balances.error}
          onRetry={() => void balances.refetch()}
          getRowId={(b) => b.employeeCode}
          onRowClick={setSelected}
          columns={[
            {
              key: 'employee',
              header: 'Employee',
              render: (b) => (
                <span>
                  <Text weight="semibold">{b.employeeName ?? '—'}</Text>
                  <Caption1 block>{b.employeeCode}</Caption1>
                </span>
              ),
            },
            {
              key: 'balance',
              header: 'Balance',
              width: 140,
              align: 'end',
              render: (b) => <span className={b.balance > 0 ? styles.owes : styles.credit}>{formatInr(b.balance)}</span>,
            },
            { key: 'last', header: 'Last entry', width: 140, priority: 'low', render: (b) => formatDateOnly(b.lastEntryDate) },
          ]}
          rowActions={(b) =>
            b.balance > 0 ? (
              <Button size="small" icon={<MoneyRegular />} onClick={() => setPaying(b)}>
                Payment
              </Button>
            ) : null
          }
          emptyTitle={outstandingOnly ? 'No staff owe the canteen' : 'No staff credit yet'}
          emptyDescription="Staff credit sales are made at the canteen counter with the employee code."
        />
      </Section>

      {selected && (
        <OverlayDrawer open position="end" size="medium" onOpenChange={(_, d) => !d.open && setSelected(null)}>
          <DrawerHeader>
            <DrawerHeaderTitle action={<Button appearance="subtle" icon={<DismissRegular />} aria-label="Close" onClick={() => setSelected(null)} />}>
              {selected.employeeName ?? selected.employeeCode}
            </DrawerHeaderTitle>
            <Caption1>
              {selected.employeeCode} · balance {formatInr(selected.balance)}
            </Caption1>
          </DrawerHeader>
          <DrawerBody>
            <StatementPanel employeeCode={selected.employeeCode} currentMonth={currentMonth} />
          </DrawerBody>
        </OverlayDrawer>
      )}
      {paying && <PaymentDialog balance={paying === 'new' ? null : paying} onClose={() => setPaying(null)} />}
      {settling && <SettleDialog currentMonth={currentMonth} onClose={() => setSettling(false)} />}
    </Page>
  );
}
