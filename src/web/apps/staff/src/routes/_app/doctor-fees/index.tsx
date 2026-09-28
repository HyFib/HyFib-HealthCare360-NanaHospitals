import { Button, makeStyles } from '@fluentui/react-components';
import {
  AddRegular,
  DocumentTextRegular,
  PersonMoneyRegular,
  TextBulletListSquareRegular,
} from '@fluentui/react-icons';
import { formatDateOnly } from '@hc360/core';
import { DataTable, KpiCard, Page, PageHeader, Section, StatusBadge } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { Amount, NoAccess } from '@/modules/billing/ui';
import { feeQueries, PAYOUT_STATUS_LABEL, type PayoutStatus } from '@/modules/doctor-fees/api';
import { CreatePayoutDialog, PayoutDialog } from '@/modules/doctor-fees/PayoutDialogs';

export const Route = createFileRoute('/_app/doctor-fees/')({
  component: DoctorFeesPage,
});

const useStyles = makeStyles({
  kpis: { display: 'grid', gap: '12px', gridTemplateColumns: 'repeat(auto-fill, minmax(200px, 1fr))' },
});

function PayoutKpi({ status, label, hint }: { status: PayoutStatus; label: string; hint: string }) {
  const navigate = useNavigate();
  const count = useQuery(feeQueries.payoutCount(status));
  return (
    <KpiCard
      label={label}
      value={count.data === undefined ? '…' : count.data.toLocaleString('en-IN')}
      hint={hint}
      onClick={() => void navigate({ to: '/doctor-fees/payouts', search: { status } })}
    />
  );
}

/** Doctor fee accounting (DFA): payouts waiting for approval or payment, and the way to rules and statements. */
function DoctorFeesPage() {
  const styles = useStyles();
  const navigate = useNavigate();
  const canRules = usePermission('doctorfees.rules.manage');
  const canPayouts = usePermission('doctorfees.payouts.manage');
  const [creating, setCreating] = useState(false);
  const [opened, setOpened] = useState<string | null>(null);
  const recent = useQuery({ ...feeQueries.payouts({ page: 1 }), enabled: canPayouts });

  if (!canRules && !canPayouts) {
    return (
      <Page>
        <PageHeader title="Doctor fees" />
        <NoAccess what="doctor fees" />
      </Page>
    );
  }

  return (
    <Page>
      <PageHeader
        title="Doctor fees"
        subtitle="Revenue shares earned on bills, paid out monthly after TDS"
        icon={<PersonMoneyRegular />}
        actions={
          <>
            <Button icon={<TextBulletListSquareRegular />} onClick={() => void navigate({ to: '/doctor-fees/rules' })}>
              Sharing rules
            </Button>
            {canPayouts && (
              <>
                <Button icon={<DocumentTextRegular />} onClick={() => void navigate({ to: '/doctor-fees/statements' })}>
                  Statements
                </Button>
                <Button appearance="primary" icon={<AddRegular />} onClick={() => setCreating(true)}>
                  Prepare payout
                </Button>
              </>
            )}
          </>
        }
      />
      {canPayouts && (
        <>
          <div className={styles.kpis}>
            <PayoutKpi status="Draft" label="Awaiting approval" hint="Prepared payouts" />
            <PayoutKpi status="Approved" label="Approved, to pay" hint="Record the transfer or cheque" />
            <PayoutKpi status="Paid" label="Paid" hint="All time" />
          </div>
          <Section title="Recent payouts" flush>
            <DataTable
              ariaLabel="Recent payouts"
              rows={recent.data?.items}
              loading={recent.isLoading}
              error={recent.error}
              onRetry={() => void recent.refetch()}
              getRowId={(p) => p.id}
              onRowClick={(p) => setOpened(p.id)}
              size="small"
              columns={[
                { key: 'doctor', header: 'Doctor', render: (p) => p.practitionerName ?? '—' },
                {
                  key: 'period',
                  header: 'Period',
                  render: (p) => `${formatDateOnly(p.periodFrom)} – ${formatDateOnly(p.periodTo)}`,
                },
                { key: 'net', header: 'Net', align: 'end', render: (p) => <Amount value={p.netAmount} strong /> },
                {
                  key: 'status',
                  header: 'Status',
                  render: (p) => <StatusBadge status={p.status} label={PAYOUT_STATUS_LABEL[p.status]} />,
                },
              ]}
              emptyTitle="No payouts yet"
              emptyDescription="Prepare a payout for a doctor and a period to pay their earned fees."
            />
          </Section>
        </>
      )}
      {creating && (
        <CreatePayoutDialog
          onClose={() => setCreating(false)}
          onCreated={(id) => {
            setCreating(false);
            setOpened(id);
          }}
        />
      )}
      {opened && <PayoutDialog payoutId={opened} onClose={() => setOpened(null)} />}
    </Page>
  );
}
