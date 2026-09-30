import { Button, Caption1, makeStyles, mergeClasses, Text, tokens } from '@fluentui/react-components';
import {
  DocumentBulletListRegular,
  DropRegular,
  HourglassHalfRegular,
  ShieldRegular,
  WarningRegular,
} from '@fluentui/react-icons';
import { formatTime, type BloodGroup } from '@hc360/core';
import { DataTable, ErrorState, KpiCard, LoadingState, Page, PageHeader, Section } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { usePermission } from '@/lib/permissions';
import {
  bloodKeys,
  bloodQueries,
  COMPONENT_TYPES,
  KNOWN_GROUPS,
  type BloodComponentType,
  type InventoryCell,
} from '@/modules/blood-bank/api';
import { ExpiryText, UnitStatusBadge } from '@/modules/blood-bank/BloodBadges';
import { componentLabel, componentShort, groupLabel } from '@/modules/blood-bank/labels';
import { LOW_STOCK_THRESHOLD, stockLevel } from '@/modules/blood-bank/rules';
import { UnitLookup } from '@/modules/blood-bank/UnitActions';
import { NoAccess } from '@/modules/ot/shared/feedback';
import { useFacilityId, useLiveInvalidation, useNow } from '@/modules/ot/shared/hooks';

export const Route = createFileRoute('/_app/blood-bank/')({
  component: BloodStockPage,
});

const useStyles = makeStyles({
  kpis: { display: 'grid', gap: '12px', gridTemplateColumns: 'repeat(auto-fill, minmax(190px, 1fr))' },
  urgent: { color: tokens.colorPaletteRedForeground1 },
  gridWrap: { overflowX: 'auto' },
  grid: { width: '100%', borderCollapse: 'separate', borderSpacing: '4px', minWidth: '640px' },
  th: {
    textAlign: 'center',
    padding: '6px',
    color: tokens.colorNeutralForeground3,
    fontWeight: tokens.fontWeightSemibold,
    fontSize: tokens.fontSizeBase200,
  },
  rowHead: {
    textAlign: 'start',
    padding: '6px 8px',
    fontWeight: tokens.fontWeightBold,
    fontSize: tokens.fontSizeBase400,
    whiteSpace: 'nowrap',
  },
  cell: {
    textAlign: 'center',
    padding: '6px 4px',
    borderRadius: tokens.borderRadiusMedium,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    backgroundColor: tokens.colorNeutralBackground1,
    cursor: 'pointer',
    width: '100%',
    color: tokens.colorNeutralForeground1,
    ':hover': { backgroundColor: tokens.colorSubtleBackgroundHover },
    ':focus-visible': { outline: `2px solid ${tokens.colorStrokeFocus2}` },
  },
  low: {
    border: `1px solid ${tokens.colorPaletteDarkOrangeBorder2}`,
    backgroundColor: tokens.colorPaletteDarkOrangeBackground1,
  },
  out: { border: `1px solid ${tokens.colorPaletteRedBorder2}`, backgroundColor: tokens.colorPaletteRedBackground1 },
  available: {
    display: 'block',
    fontSize: tokens.fontSizeBase500,
    fontWeight: tokens.fontWeightSemibold,
    lineHeight: tokens.lineHeightBase500,
  },
  detail: { display: 'block', fontSize: tokens.fontSizeBase100, color: tokens.colorNeutralForeground3 },
  expiring: {
    display: 'block',
    fontSize: tokens.fontSizeBase100,
    color: tokens.colorPaletteDarkOrangeForeground1,
    fontWeight: tokens.fontWeightSemibold,
  },
  legend: { display: 'flex', gap: '12px', flexWrap: 'wrap', marginTop: '8px' },
  mono: { fontFamily: tokens.fontFamilyMonospace },
});

/** Stock by group and component (BB-05), units expiring soonest first, open and emergency requests, unit look-up. */
function BloodStockPage() {
  const styles = useStyles();
  const navigate = useNavigate();
  const facilityId = useFacilityId();
  const now = useNow(60_000);
  const canRead = usePermission('bloodbank.read');
  const canRequest = usePermission('bloodbank.requests.create');
  const canManage = usePermission('bloodbank.inventory.manage');
  const inventory = useQuery({ ...bloodQueries.inventory(), enabled: canRead });

  useLiveInvalidation(facilityId ? `facility:${facilityId}` : null, 'bloodbank.changed', [bloodKeys.all]);

  if (!canRead) {
    return (
      <Page>
        <NoAccess what="blood stock" />
      </Page>
    );
  }
  if (inventory.isLoading) {
    return <LoadingState label="Loading blood stock…" />;
  }
  if (inventory.error || !inventory.data) {
    return (
      <Page maxWidth={900}>
        <ErrorState error={inventory.error} onRetry={() => void inventory.refetch()} />
      </Page>
    );
  }

  const d = inventory.data;
  const cell = (component: BloodComponentType, group: BloodGroup): InventoryCell | undefined =>
    d.stock.find((c) => c.component === component && c.bloodGroup === group);
  const groups = KNOWN_GROUPS.filter((g) => !g.startsWith('Bombay') || d.stock.some((c) => c.bloodGroup === g));
  const expiringSorted = [...d.expiringSoon].sort((a, b) => Date.parse(a.expiresAt) - Date.parse(b.expiresAt));
  const openUnits = (component: BloodComponentType, group: BloodGroup) =>
    void navigate({ to: '/blood-bank/units', search: { component, bloodGroup: group, status: 'Available' } });

  return (
    <Page>
      <PageHeader
        title="Blood stock"
        subtitle={`As of ${formatTime(d.asOf)}. Expiring units are listed soonest first.`}
        icon={<DropRegular />}
        actions={
          canRequest && (
            <Button
              appearance="primary"
              icon={<DropRegular />}
              onClick={() => void navigate({ to: '/blood-bank/requests/new' })}
            >
              Request blood
            </Button>
          )
        }
      />

      <div className={styles.kpis}>
        <KpiCard
          label="Emergency requests"
          value={<span className={d.emergencyRequests > 0 ? styles.urgent : undefined}>{d.emergencyRequests}</span>}
          icon={<WarningRegular />}
          hint={d.emergencyRequests > 0 ? 'Needs action now' : 'None open'}
          onClick={() => void navigate({ to: '/blood-bank/requests' })}
        />
        <KpiCard
          label="Open requests"
          value={d.openRequests}
          icon={<DocumentBulletListRegular />}
          onClick={() => void navigate({ to: '/blood-bank/requests' })}
        />
        <KpiCard
          label="In quarantine"
          value={d.unitsInQuarantine}
          icon={<ShieldRegular />}
          hint="Awaiting grouping or TTI results"
          onClick={() => void navigate({ to: '/blood-bank/units', search: { status: 'Quarantine' } })}
        />
        <KpiCard
          label="Expiring within 3 days"
          value={d.expiringSoon.length}
          icon={<HourglassHalfRegular />}
          hint="Use these first"
        />
      </div>

      <Section
        title="Stock by group and component"
        description="Available units (big number); reserved and in quarantine below. Select a cell to see its units."
      >
        <div className={styles.gridWrap}>
          <table className={styles.grid}>
            <thead>
              <tr>
                <th className={styles.th} scope="col">
                  Group
                </th>
                {COMPONENT_TYPES.map((component) => (
                  <th key={component} className={styles.th} scope="col" title={componentLabel[component]}>
                    {componentShort[component]}
                  </th>
                ))}
              </tr>
            </thead>
            <tbody>
              {groups.map((group) => (
                <tr key={group}>
                  <th className={styles.rowHead} scope="row">
                    {groupLabel(group)}
                  </th>
                  {COMPONENT_TYPES.map((component) => {
                    const c = cell(component, group);
                    const available = c?.available ?? 0;
                    const level = stockLevel(component, available);
                    return (
                      <td key={component}>
                        <button
                          type="button"
                          className={mergeClasses(
                            styles.cell,
                            level === 'low' && styles.low,
                            level === 'out' && styles.out,
                          )}
                          onClick={() => openUnits(component, group)}
                          aria-label={`${groupLabel(group)} ${componentLabel[component]}: ${available} available${level === 'out' ? ', none in stock' : level === 'low' ? ', low stock' : ''}, ${c?.reserved ?? 0} reserved, ${c?.quarantine ?? 0} in quarantine${c?.expiringSoon ? `, ${c.expiringSoon} expiring within 3 days` : ''}`}
                        >
                          <span className={styles.available}>{available}</span>
                          <span className={styles.detail}>
                            {level === 'out' ? 'none' : level === 'low' ? 'low' : 'available'}
                            {c?.reserved ? ` · ${c.reserved} res.` : ''}
                            {c?.quarantine ? ` · ${c.quarantine} quar.` : ''}
                          </span>
                          {c?.expiringSoon ? <span className={styles.expiring}>{c.expiringSoon} expiring</span> : null}
                        </button>
                      </td>
                    );
                  })}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
        <div className={styles.legend}>
          <Caption1>
            Low stock means fewer than:{' '}
            {COMPONENT_TYPES.map((c) => `${componentShort[c]} ${LOW_STOCK_THRESHOLD[c]}`).join(', ')} units.
          </Caption1>
        </div>
      </Section>

      <Section
        title={`Expiring within 3 days (${expiringSorted.length})`}
        description="First expiry, first out: issue or cross-match these before newer units."
        flush
      >
        <DataTable
          ariaLabel="Units expiring soon"
          rows={expiringSorted}
          getRowId={(u) => u.id}
          onRowClick={(u) => void navigate({ to: '/blood-bank/units', search: { search: u.unitNumber } })}
          size="small"
          columns={[
            { key: 'expiry', header: 'Expires', render: (u) => <ExpiryText expiresAt={u.expiresAt} now={now} /> },
            { key: 'number', header: 'Unit', render: (u) => <Text className={styles.mono}>{u.unitNumber}</Text> },
            { key: 'component', header: 'Component', render: (u) => componentLabel[u.component] },
            { key: 'group', header: 'Group', render: (u) => groupLabel(u.bloodGroup) },
            { key: 'status', header: 'Status', render: (u) => <UnitStatusBadge status={u.status} /> },
          ]}
          emptyTitle="Nothing expires in the next 3 days"
        />
      </Section>

      <Section title="Find a unit" description="Scan a unit label to see its status, print a new label or discard it.">
        <UnitLookup now={now} canManage={canManage} />
      </Section>
    </Page>
  );
}
