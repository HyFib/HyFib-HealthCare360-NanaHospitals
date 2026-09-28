import { Badge, Button, Caption1, Field, makeStyles, Select, Switch, Tab, TabList, Text, tokens } from '@fluentui/react-components';
import { CertificateRegular, ClipboardTaskRegular, HandshakeRegular } from '@fluentui/react-icons';
import { formatDateOnly } from '@hc360/core';
import { DataTable, Page, PageHeader, Section } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState, type ReactElement, type ReactNode } from 'react';
import { usePermission } from '@/lib/permissions';
import { assetQueries, type MaintenanceDueItem, type PmPlanDto } from '@/modules/assets/api';
import { CalibrationDialog, ContractDialog, PmVisitDialog } from '@/modules/assets/AssetDialogs';
import { dueKindLabel } from '@/modules/assets/labels';
import { dueLabel } from '@/modules/assets/ticketFlow';
import { SUPPLY_PERMISSIONS as S } from '@/modules/inventory/permissions';

type MaintenanceTab = 'pm' | 'calibration' | 'expiring';
const TABS: readonly MaintenanceTab[] = ['pm', 'calibration', 'expiring'];
const WINDOWS = [7, 14, 30, 60, 90] as const;
const DEFAULT_WINDOW = 30;
/** The server always looks at least this far ahead for warranties and contracts (MaintenanceService.ExpiryWindowDays). */
const EXPIRY_WINDOW = 30;

interface MaintenanceSearch {
  tab?: MaintenanceTab;
  days?: number;
}

export const Route = createFileRoute('/_app/assets/maintenance')({
  validateSearch: (s: Record<string, unknown>): MaintenanceSearch => {
    const days = Number(s.days);
    return {
      tab: TABS.includes(s.tab as MaintenanceTab) && s.tab !== 'pm' ? (s.tab as MaintenanceTab) : undefined,
      days: (WINDOWS as readonly number[]).includes(days) && days !== DEFAULT_WINDOW ? days : undefined,
    };
  },
  component: MaintenancePage,
});

const useStyles = makeStyles({
  bar: { display: 'flex', gap: '16px', alignItems: 'end', flexWrap: 'wrap', justifyContent: 'space-between' },
  tabs: { display: 'flex', gap: '12px', alignItems: 'center', flexWrap: 'wrap', padding: '0 16px 12px' },
  muted: { color: tokens.colorNeutralForeground3 },
  late: { color: tokens.colorPaletteRedForeground1, fontWeight: tokens.fontWeightSemibold },
  soon: { color: tokens.colorPaletteMarigoldForeground1, fontWeight: tokens.fontWeightSemibold },
});

type AssetRef = { id: string; name: string; assetTag: string };

/** Button inside a clickable row: acts on its own without opening the row. */
function RowButton({ icon, children, onClick }: { icon: ReactElement; children: ReactNode; onClick: () => void }) {
  return (
    <Button
      size="small"
      icon={icon}
      onClick={(event) => {
        event.stopPropagation();
        onClick();
      }}
      onKeyDown={(event) => event.stopPropagation()}
    >
      {children}
    </Button>
  );
}

function TabCount({ total, overdue }: { total: number | undefined; overdue: number }) {
  if (total === undefined) {
    return null;
  }
  return (
    <Badge size="small" appearance={overdue > 0 ? 'filled' : 'tint'} color={overdue > 0 ? 'danger' : total > 0 ? 'warning' : 'informative'} style={{ marginLeft: 6 }}>
      {total}
    </Badge>
  );
}

/**
 * Preventive maintenance, calibration and contract renewals that fall due (AST-02, AST-03; NABH FMS).
 * Overdue items stay on the list until they are done.
 */
function MaintenancePage() {
  const styles = useStyles();
  const search = Route.useSearch();
  const tab = search.tab ?? 'pm';
  const days = search.days ?? DEFAULT_WINDOW;
  const navigate = useNavigate({ from: Route.fullPath });
  const canMaintain = usePermission(S.assetsMaintenance);
  const canManage = usePermission(S.assetsManage);
  const [allPlans, setAllPlans] = useState(false);
  const [visitFor, setVisitFor] = useState<PmPlanDto | null>(null);
  const [calibrateFor, setCalibrateFor] = useState<AssetRef | null>(null);
  const [contractFor, setContractFor] = useState<string | null>(null);

  const plans = useQuery(assetQueries.plans());
  const due = useQuery(assetQueries.due(days));

  const planRowsDue = plans.data?.filter((p) => p.isActive && p.daysToDue <= days);
  const planRows = allPlans ? plans.data : planRowsDue;
  const calibrationRows = due.data?.filter((i) => i.kind === 'Calibration');
  const expiringRows = due.data?.filter((i) => i.kind === 'Warranty' || i.kind === 'Contract');
  const overdue = (rows: { daysToDue: number }[] | undefined) => rows?.filter((r) => r.daysToDue < 0).length ?? 0;

  const openAsset = (assetId: string, assetTab: 'maintenance' | 'calibration' | 'contracts') =>
    void navigate({ to: '/assets/$assetId', params: { assetId }, search: { tab: assetTab } });

  const whenCell = (daysToDue: number, date: string) => (
    <span className={daysToDue < 0 ? styles.late : daysToDue <= 7 ? styles.soon : undefined}>
      {dueLabel(daysToDue)}
      <Caption1 block className={styles.muted}>
        {formatDateOnly(date)}
      </Caption1>
    </span>
  );

  const assetCell = (name: string, tag: string) => (
    <span>
      <Text weight="semibold">{name}</Text> <Caption1 className={styles.muted}>{tag}</Caption1>
    </span>
  );

  return (
    <Page>
      <PageHeader
        title="Maintenance and calibration"
        subtitle="Preventive maintenance visits, calibration certificates and AMC/CMC renewals that fall due. Overdue work stays listed until it is recorded."
      />
      <Section flush>
        <div className={styles.tabs}>
          <div className={styles.bar} style={{ flex: 1 }}>
            <TabList selectedValue={tab} onTabSelect={(_, d) => void navigate({ search: (prev) => ({ ...prev, tab: d.value === 'pm' ? undefined : (d.value as MaintenanceTab) }), replace: true })}>
              <Tab value="pm">
                Preventive maintenance
                <TabCount total={planRowsDue?.length} overdue={overdue(planRowsDue)} />
              </Tab>
              <Tab value="calibration">
                Calibration
                <TabCount total={calibrationRows?.length} overdue={overdue(calibrationRows)} />
              </Tab>
              <Tab value="expiring">
                Warranties and contracts
                <TabCount total={expiringRows?.length} overdue={0} />
              </Tab>
            </TabList>
            <div style={{ display: 'flex', gap: 16, alignItems: 'end', flexWrap: 'wrap' }}>
              {tab === 'pm' && <Switch checked={allPlans} onChange={(_, d) => setAllPlans(d.checked)} label="All plans" />}
              <Field label="Look ahead" orientation="horizontal">
                <Select
                  value={String(days)}
                  onChange={(_, d) => void navigate({ search: (prev) => ({ ...prev, days: Number(d.value) === DEFAULT_WINDOW ? undefined : Number(d.value) }), replace: true })}
                  disabled={tab === 'pm' && allPlans}
                >
                  {WINDOWS.map((w) => (
                    <option key={w} value={w}>
                      {w} days
                    </option>
                  ))}
                </Select>
              </Field>
            </div>
          </div>
        </div>

        {tab === 'pm' && (
          <DataTable<PmPlanDto>
            ariaLabel="Preventive maintenance plans"
            rows={planRows}
            loading={plans.isLoading}
            error={plans.error}
            onRetry={() => void plans.refetch()}
            getRowId={(p) => p.id}
            onRowClick={(p) => openAsset(p.assetId, 'maintenance')}
            size="small"
            columns={[
              { key: 'asset', header: 'Equipment', render: (p) => assetCell(p.assetName, p.assetTag) },
              {
                key: 'plan',
                header: 'Plan',
                render: (p) => (
                  <span>
                    {p.title}
                    {!p.isActive && (
                      <Badge size="small" appearance="tint" color="informative" style={{ marginLeft: 6 }}>
                        Paused
                      </Badge>
                    )}
                    <Caption1 block className={styles.muted}>
                      Every {p.frequencyDays} days
                    </Caption1>
                  </span>
                ),
              },
              { key: 'last', header: 'Last done', priority: 'low', render: (p) => (p.lastDoneOn ? formatDateOnly(p.lastDoneOn) : 'Not yet') },
              { key: 'due', header: 'Next visit', render: (p) => whenCell(p.daysToDue, p.nextDueDate) },
              { key: 'who', header: 'Done by', priority: 'low', render: (p) => p.assignedTo ?? '—' },
              {
                key: 'act',
                header: '',
                width: 150,
                align: 'end',
                render: (p) =>
                  canMaintain && p.isActive ? (
                    <RowButton icon={<ClipboardTaskRegular />} onClick={() => setVisitFor(p)}>
                      Record visit
                    </RowButton>
                  ) : null,
              },
            ]}
            emptyTitle={allPlans ? 'No maintenance plans yet' : `No preventive maintenance due in the next ${days} days`}
            emptyDescription="Plans are set up on each asset's Maintenance tab, with how often the visit is due."
          />
        )}

        {tab === 'calibration' && (
          <DataTable<MaintenanceDueItem>
            ariaLabel="Calibration due"
            rows={calibrationRows}
            loading={due.isLoading}
            error={due.error}
            onRetry={() => void due.refetch()}
            getRowId={(i) => `${i.assetId}-${i.dueDate}`}
            onRowClick={(i) => openAsset(i.assetId, 'calibration')}
            size="small"
            columns={[
              { key: 'asset', header: 'Equipment', render: (i) => assetCell(i.assetName, i.assetTag) },
              { key: 'due', header: 'Calibration due', render: (i) => whenCell(i.daysToDue, i.dueDate) },
              {
                key: 'act',
                header: '',
                width: 190,
                align: 'end',
                render: (i) =>
                  canMaintain ? (
                    <RowButton icon={<CertificateRegular />} onClick={() => setCalibrateFor({ id: i.assetId, name: i.assetName, assetTag: i.assetTag })}>
                      Record calibration
                    </RowButton>
                  ) : null,
              },
            ]}
            emptyTitle={`No calibration due in the next ${days} days`}
            emptyDescription="Equipment marked as needing calibration shows here when its certificate is about to lapse."
          />
        )}

        {tab === 'expiring' && (
          <>
            <Caption1 block className={styles.muted} style={{ padding: '0 16px 8px' }}>
              Warranties and AMC/CMC contracts ending in the next {Math.max(days, EXPIRY_WINDOW)} days. Renew before the cover lapses.
            </Caption1>
            <DataTable<MaintenanceDueItem>
              ariaLabel="Warranties and contracts ending"
              rows={expiringRows}
              loading={due.isLoading}
              error={due.error}
              onRetry={() => void due.refetch()}
              getRowId={(i) => `${i.kind}-${i.assetId}-${i.dueDate}-${i.title}`}
              onRowClick={(i) => openAsset(i.assetId, 'contracts')}
              size="small"
              columns={[
                { key: 'asset', header: 'Equipment', render: (i) => assetCell(i.assetName, i.assetTag) },
                {
                  key: 'what',
                  header: 'Cover',
                  render: (i) => (
                    <span>
                      {i.title}
                      <Caption1 block className={styles.muted}>
                        {dueKindLabel[i.kind] ?? i.kind}
                      </Caption1>
                    </span>
                  ),
                },
                {
                  key: 'ends',
                  header: 'Ends',
                  render: (i) => (
                    <span className={i.daysToDue <= 7 ? styles.soon : undefined}>
                      {i.daysToDue === 0 ? 'Today' : `In ${i.daysToDue} ${i.daysToDue === 1 ? 'day' : 'days'}`}
                      <Caption1 block className={styles.muted}>
                        {formatDateOnly(i.dueDate)}
                      </Caption1>
                    </span>
                  ),
                },
                {
                  key: 'act',
                  header: '',
                  width: 170,
                  align: 'end',
                  render: (i) =>
                    canManage ? (
                      <RowButton icon={<HandshakeRegular />} onClick={() => setContractFor(i.assetId)}>
                        {i.kind === 'Contract' ? 'Renew contract' : 'Add AMC / CMC'}
                      </RowButton>
                    ) : null,
                },
              ]}
              emptyTitle="No warranty or contract ends soon"
            />
          </>
        )}
      </Section>

      {visitFor && <PmVisitDialog plan={visitFor} onClose={() => setVisitFor(null)} />}
      {calibrateFor && <CalibrationDialog asset={calibrateFor} onClose={() => setCalibrateFor(null)} />}
      {contractFor && <ContractDialog assetId={contractFor} contract={null} onClose={() => setContractFor(null)} />}
    </Page>
  );
}
