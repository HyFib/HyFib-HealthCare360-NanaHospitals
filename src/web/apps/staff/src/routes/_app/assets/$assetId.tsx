import {
  Badge,
  Button,
  Caption1,
  Field,
  Input,
  makeStyles,
  Menu,
  MenuItem,
  MenuList,
  MenuPopover,
  MenuTrigger,
  MessageBar,
  MessageBarBody,
  Tab,
  TabList,
  Text,
  tokens,
} from '@fluentui/react-components';
import {
  AddRegular,
  BinRecycleRegular,
  EditRegular,
  MoreHorizontalRegular,
  PauseRegular,
  PlayRegular,
  PrintRegular,
  ProhibitedRegular,
  WrenchRegular,
} from '@fluentui/react-icons';
import { formatDateOnly, formatDateTime, formatInr, formatRelative, todayIso } from '@hc360/core';
import {
  ConfirmDialog,
  DataTable,
  ErrorState,
  LoadingState,
  Page,
  PageHeader,
  Section,
  StatusBadge,
  useNotify,
} from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useMemo, useState } from 'react';
import { EChart, type ChartOption } from '@/lib/EChart';
import { useLetterhead } from '@/lib/letterhead';
import { usePermission } from '@/lib/permissions';
import {
  assetApi,
  assetKeys,
  assetQueries,
  type AssetContractDto,
  type AssetDetail,
  type PmPlanDto,
} from '@/modules/assets/api';
import {
  CalibrationDialog,
  ContractDialog,
  DisposeDialog,
  PmPlanDialog,
  PmVisitDialog,
} from '@/modules/assets/AssetDialogs';
import { AssetDrawer } from '@/modules/assets/AssetDrawer';
import { BreakdownDialog } from '@/modules/assets/BreakdownDialog';
import { printAssetTag } from '@/modules/assets/print';
import {
  assetCategoryLabel,
  assetStatusLabel,
  assetStatusTone,
  calibrationLabel,
  contractTypeLabel,
  criticalityTone,
  depreciationLabel,
  pmOutcomeLabel,
} from '@/modules/assets/labels';
import { dueLabel, formatDowntime, isRetired } from '@/modules/assets/ticketFlow';
import { formatPercent } from '@/modules/inventory/format';
import { ASSET_READERS, SUPPLY_PERMISSIONS as S } from '@/modules/inventory/permissions';
import { ReasonDialog } from '@/modules/inventory/ReasonDialog';
import { Detail, DetailGrid, DocStatus } from '@/modules/inventory/StockBits';

type AssetTab = 'overview' | 'contracts' | 'depreciation' | 'maintenance' | 'calibration' | 'tickets';
const TABS: AssetTab[] = ['overview', 'contracts', 'depreciation', 'maintenance', 'calibration', 'tickets'];

export const Route = createFileRoute('/_app/assets/$assetId')({
  validateSearch: (s: Record<string, unknown>): { tab?: AssetTab } => ({
    tab: TABS.includes(s.tab as AssetTab) ? (s.tab as AssetTab) : undefined,
  }),
  component: AssetPage,
});

const useStyles = makeStyles({
  facts: { display: 'flex', gap: '6px 16px', flexWrap: 'wrap', alignItems: 'center' },
  muted: { color: tokens.colorNeutralForeground3 },
  late: { color: tokens.colorPaletteRedForeground1, fontWeight: tokens.fontWeightSemibold },
  toolbar: { display: 'flex', gap: '8px', alignItems: 'end', flexWrap: 'wrap', marginBottom: '12px' },
});

type ActionDialog = 'edit' | 'breakdown' | 'condemn' | 'dispose' | 'idle' | 'inuse';

/** One asset: details, AMC/CMC contracts, depreciation, preventive maintenance, calibration and breakdowns. */
function AssetPage() {
  const styles = useStyles();
  const { assetId } = Route.useParams();
  const { tab = 'overview' } = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const notify = useNotify();
  const queryClient = useQueryClient();
  const letterhead = useLetterhead();
  const canManage = usePermission(S.assetsManage);
  const canMaintain = usePermission(S.assetsMaintenance);
  const canRead = usePermission(ASSET_READERS);
  const asset = useQuery(assetQueries.detail(assetId));
  const [dialog, setDialog] = useState<ActionDialog | null>(null);

  const operational = useMutation({
    mutationFn: (inUse: boolean) => assetApi.setOperational(assetId, inUse),
    onSuccess: (_, inUse) => {
      setDialog(null);
      notify.success(inUse ? 'Back in use' : 'Marked idle');
      void queryClient.invalidateQueries({ queryKey: assetKeys.all });
    },
    onError: (error) => {
      setDialog(null);
      notify.error(error, 'Could not change the status');
    },
  });
  const condemn = useMutation({
    mutationFn: (reason: string) => assetApi.requestCondemnation(assetId, reason),
    onSuccess: () => {
      setDialog(null);
      notify.success('Condemnation requested', 'An asset manager (not you) decides it under Condemnation.');
      void queryClient.invalidateQueries({ queryKey: assetKeys.all });
    },
  });

  if (asset.isLoading) {
    return <LoadingState />;
  }
  if (!asset.data || !canRead) {
    return (
      <Page maxWidth={900}>
        <ErrorState error={asset.error} onRetry={() => void asset.refetch()} />
      </Page>
    );
  }
  const a = asset.data;
  const retired = isRetired(a.status);

  const printTag = async () => {
    try {
      await printAssetTag(a, letterhead.name);
    } catch (error) {
      notify.error(error, 'The tag did not print');
    }
  };

  return (
    <Page>
      <PageHeader
        title={a.name}
        subtitle={[a.assetTag, a.make, a.model, a.serialNumber ? `S/N ${a.serialNumber}` : null]
          .filter(Boolean)
          .join(' · ')}
        breadcrumbs={[
          { label: 'Asset register', onClick: () => void navigate({ to: '/assets/register' }) },
          { label: a.assetTag },
        ]}
        actions={
          <>
            {!retired && (
              <Button appearance="primary" icon={<WrenchRegular />} onClick={() => setDialog('breakdown')}>
                Report breakdown
              </Button>
            )}
            {canManage && !retired && (
              <Button icon={<EditRegular />} onClick={() => setDialog('edit')}>
                Edit
              </Button>
            )}
            <Menu>
              <MenuTrigger disableButtonEnhancement>
                <Button icon={<MoreHorizontalRegular />} aria-label="More actions" />
              </MenuTrigger>
              <MenuPopover>
                <MenuList>
                  <MenuItem icon={<PrintRegular />} onClick={() => void printTag()}>
                    Print asset tag
                  </MenuItem>
                  {(canManage || canMaintain) && a.status === 'InUse' && (
                    <MenuItem icon={<PauseRegular />} onClick={() => setDialog('idle')}>
                      Mark idle (not in use)
                    </MenuItem>
                  )}
                  {(canManage || canMaintain) && (a.status === 'Idle' || a.status === 'UnderMaintenance') && (
                    <MenuItem icon={<PlayRegular />} onClick={() => setDialog('inuse')}>
                      Back in use
                    </MenuItem>
                  )}
                  {(canManage || canMaintain) && !retired && (
                    <MenuItem icon={<ProhibitedRegular />} onClick={() => setDialog('condemn')}>
                      Request condemnation
                    </MenuItem>
                  )}
                  {canManage && a.status === 'Condemned' && (
                    <MenuItem icon={<BinRecycleRegular />} onClick={() => setDialog('dispose')}>
                      Record disposal
                    </MenuItem>
                  )}
                </MenuList>
              </MenuPopover>
            </Menu>
          </>
        }
      />
      <div className={styles.facts}>
        <StatusBadge status={a.status} tone={assetStatusTone[a.status]} label={assetStatusLabel[a.status]} />
        <Badge appearance="tint" color={criticalityTone[a.criticality]}>
          {a.criticality} criticality
        </Badge>
        <Caption1>{assetCategoryLabel[a.category]}</Caption1>
        <Caption1>{[a.departmentName, a.location].filter(Boolean).join(' · ') || 'Location not recorded'}</Caption1>
        {a.underWarranty && (
          <Badge appearance="tint" color="success">
            Under warranty to {formatDateOnly(a.warrantyExpiry)}
          </Badge>
        )}
      </div>
      {a.status === 'Condemned' && (
        <MessageBar intent="warning">
          <MessageBarBody>
            Condemned on {formatDateOnly(a.condemnedOn)}. Record the disposal once it leaves the hospital.
          </MessageBarBody>
        </MessageBar>
      )}
      {a.status === 'Disposed' && (
        <MessageBar intent="info">
          <MessageBarBody>
            Disposed on {formatDateOnly(a.disposedOn)} by {a.disposalMethod} for {formatInr(a.disposalValue)}.
          </MessageBarBody>
        </MessageBar>
      )}

      <Section flush>
        <div style={{ padding: '0 8px' }}>
          <TabList
            selectedValue={tab}
            onTabSelect={(_, d) =>
              void navigate({
                search: { tab: d.value === 'overview' ? undefined : (d.value as AssetTab) },
                replace: true,
              })
            }
          >
            <Tab value="overview">Overview</Tab>
            <Tab value="maintenance">Maintenance</Tab>
            <Tab value="calibration">Calibration</Tab>
            <Tab value="tickets">Breakdowns</Tab>
            <Tab value="contracts">AMC / CMC</Tab>
            <Tab value="depreciation">Depreciation</Tab>
          </TabList>
        </div>
        <div style={{ padding: 16 }}>
          {tab === 'overview' && <Overview asset={a} />}
          {tab === 'contracts' && <Contracts asset={a} canManage={canManage && !retired} />}
          {tab === 'depreciation' && <Depreciation asset={a} />}
          {tab === 'maintenance' && <Maintenance asset={a} canMaintain={canMaintain && !retired} />}
          {tab === 'calibration' && <Calibration asset={a} canMaintain={canMaintain && !retired} />}
          {tab === 'tickets' && <Tickets asset={a} onReport={() => setDialog('breakdown')} />}
        </div>
      </Section>

      {dialog === 'edit' && <AssetDrawer asset={a} onClose={() => setDialog(null)} />}
      {dialog === 'breakdown' && (
        <BreakdownDialog
          asset={{
            id: a.id,
            assetTag: a.assetTag,
            name: a.name,
            location: a.location ?? a.departmentName,
            criticality: a.criticality,
          }}
          onClose={() => setDialog(null)}
          onReported={() => void navigate({ search: { tab: 'tickets' } })}
        />
      )}
      {dialog === 'dispose' && (
        <DisposeDialog
          asset={{ id: a.id, name: a.name, assetTag: a.assetTag, bookValue: a.bookValue }}
          onClose={() => setDialog(null)}
        />
      )}
      {dialog === 'condemn' && (
        <ReasonDialog
          title={`Request condemnation of ${a.name}?`}
          confirmLabel="Request condemnation"
          destructive
          minLength={10}
          maxLength={1000}
          busy={condemn.isPending}
          error={condemn.error}
          placeholder="e.g. Beyond economical repair: service report dated 12 Sep, spares discontinued"
          onSubmit={(reason) => condemn.mutate(reason)}
          onClose={() => {
            condemn.reset();
            setDialog(null);
          }}
        >
          The book value today ({formatInr(a.bookValue)}) is recorded with the request. Another asset manager decides
          it.
        </ReasonDialog>
      )}
      <ConfirmDialog
        open={dialog === 'idle' || dialog === 'inuse'}
        title={dialog === 'idle' ? 'Mark as idle?' : 'Put back in use?'}
        confirmLabel={dialog === 'idle' ? 'Mark idle' : 'Back in use'}
        busy={operational.isPending}
        onConfirm={() => operational.mutate(dialog === 'inuse')}
        onCancel={() => setDialog(null)}
      >
        {dialog === 'idle'
          ? 'Idle equipment is working but not being used (spare or stored).'
          : 'The equipment is working and available for use again.'}
      </ConfirmDialog>
    </Page>
  );
}

function Overview({ asset: a }: { asset: AssetDetail }) {
  return (
    <DetailGrid>
      <Detail label="Asset tag">{a.assetTag}</Detail>
      <Detail label="Barcode">{a.barcode}</Detail>
      <Detail label="Category">{assetCategoryLabel[a.category]}</Detail>
      <Detail label="Make and model">{[a.make, a.model].filter(Boolean).join(' ')}</Detail>
      <Detail label="Serial number">{a.serialNumber}</Detail>
      <Detail label="Department">{a.departmentName}</Detail>
      <Detail label="Location">{a.location}</Detail>
      <Detail label="Custodian">{a.custodianName}</Detail>
      <Detail label="Purchased">
        {a.purchaseDate
          ? `${formatDateOnly(a.purchaseDate)} for ${formatInr(a.purchaseCost)}`
          : formatInr(a.purchaseCost)}
      </Detail>
      <Detail label="Supplier">{a.vendorName}</Detail>
      <Detail label="PO / invoice">{[a.purchaseOrderNumber, a.invoiceNumber].filter(Boolean).join(' / ')}</Detail>
      <Detail label="Installed">{a.installationDate ? formatDateOnly(a.installationDate) : null}</Detail>
      <Detail label="Warranty">
        {a.warrantyExpiry ? `${a.underWarranty ? 'Until' : 'Ended'} ${formatDateOnly(a.warrantyExpiry)}` : null}
      </Detail>
      <Detail label="Calibration">
        {a.requiresCalibration
          ? a.nextCalibrationDue
            ? `Next due ${formatDateOnly(a.nextCalibrationDue)}`
            : 'Required, not recorded yet'
          : 'Not required'}
      </Detail>
      <Detail label="Depreciation">{`${depreciationLabel[a.depreciationMethod]} · ${a.usefulLifeYears} years · ${formatPercent(a.depreciationRatePercent)} a year`}</Detail>
      <Detail label="Book value today">{formatInr(a.bookValue)}</Detail>
      {a.notes && <Detail label="Notes">{a.notes}</Detail>}
    </DetailGrid>
  );
}

function Contracts({ asset, canManage }: { asset: AssetDetail; canManage: boolean }) {
  const contracts = useQuery(assetQueries.contracts(asset.id));
  const [editing, setEditing] = useState<AssetContractDto | 'new' | null>(null);
  return (
    <>
      {canManage && (
        <div style={{ marginBottom: 12 }}>
          <Button icon={<AddRegular />} onClick={() => setEditing('new')}>
            Add AMC / CMC
          </Button>
        </div>
      )}
      <DataTable<AssetContractDto>
        ariaLabel="Maintenance contracts"
        rows={contracts.data}
        loading={contracts.isLoading}
        error={contracts.error}
        onRetry={() => void contracts.refetch()}
        getRowId={(c) => c.id}
        onRowClick={canManage ? (c) => setEditing(c) : undefined}
        size="small"
        columns={[
          { key: 'type', header: 'Type', render: (c) => contractTypeLabel[c.type] },
          { key: 'vendor', header: 'Vendor', render: (c) => c.vendorName ?? '—' },
          { key: 'no', header: 'Contract', priority: 'low', render: (c) => c.contractNumber ?? '—' },
          {
            key: 'period',
            header: 'Period',
            render: (c) => `${formatDateOnly(c.startDate)} to ${formatDateOnly(c.endDate)}`,
          },
          { key: 'cost', header: 'Cost', align: 'end', render: (c) => formatInr(c.cost) },
          { key: 'cover', header: 'Covers', priority: 'low', render: (c) => c.coverageNotes ?? '—' },
          {
            key: 'status',
            header: 'Status',
            render: (c) => (
              <StatusBadge
                status={c.isCurrent ? 'Active' : 'Expired'}
                label={c.isCurrent ? 'Current' : 'Not current'}
              />
            ),
          },
        ]}
        emptyTitle="No maintenance contracts"
        emptyDescription="Warranty is recorded on the asset; add an AMC or CMC when it ends."
      />
      {editing && (
        <ContractDialog
          assetId={asset.id}
          contract={editing === 'new' ? null : editing}
          onClose={() => setEditing(null)}
        />
      )}
    </>
  );
}

function Depreciation({ asset }: { asset: AssetDetail }) {
  const styles = useStyles();
  const [asOf, setAsOf] = useState(todayIso());
  const schedule = useQuery(assetQueries.depreciation(asset.id, asOf || undefined));
  const option = useMemo<ChartOption | null>(() => {
    const rows = schedule.data?.schedule ?? [];
    return rows.length === 0
      ? null
      : {
          grid: { left: 80, right: 16, top: 16, bottom: 30 },
          tooltip: { trigger: 'axis', valueFormatter: (v) => formatInr(Number(v)) },
          xAxis: { type: 'category', data: rows.map((r) => `Year ${r.year}`) },
          yAxis: { type: 'value', axisLabel: { formatter: (v: number) => `₹${v.toLocaleString('en-IN')}` } },
          series: [
            { type: 'bar', name: 'Depreciation', data: rows.map((r) => r.depreciation), barMaxWidth: 30 },
            { type: 'line', name: 'Book value at year end', data: rows.map((r) => r.closing), smooth: true },
          ],
          legend: { bottom: 0 },
        };
  }, [schedule.data]);
  if (schedule.error) {
    return <ErrorState error={schedule.error} onRetry={() => void schedule.refetch()} />;
  }
  const d = schedule.data;
  return (
    <>
      <div className={styles.toolbar}>
        <Field label="Book value as of">
          <Input type="date" value={asOf} onChange={(_, dd) => setAsOf(dd.value)} />
        </Field>
        {d && (
          <Caption1 className={styles.muted} style={{ alignSelf: 'center' }}>
            {depreciationLabel[d.method]} at {formatPercent(d.ratePercent)} a year · cost {formatInr(d.cost)} · salvage{' '}
            {formatInr(d.salvageValue)} · depreciated {formatInr(d.accumulatedDepreciation)} · book value{' '}
            {formatInr(d.bookValue)}
          </Caption1>
        )}
      </div>
      {!d && <LoadingState />}
      {d && !d.startDate && <Caption1>Record the purchase or installation date to work out depreciation.</Caption1>}
      {option && <EChart option={option} height={240} ariaLabel="Depreciation by year" />}
      {d && (
        <DataTable
          ariaLabel="Depreciation schedule"
          rows={d.schedule}
          getRowId={(r) => String(r.year)}
          size="small"
          columns={[
            { key: 'year', header: 'Year', width: 70, render: (r) => r.year },
            { key: 'period', header: 'Period', render: (r) => `${formatDateOnly(r.from)} to ${formatDateOnly(r.to)}` },
            { key: 'opening', header: 'Opening', align: 'end', render: (r) => formatInr(r.opening) },
            { key: 'dep', header: 'Depreciation', align: 'end', render: (r) => formatInr(r.depreciation) },
            {
              key: 'closing',
              header: 'Closing',
              align: 'end',
              render: (r) => <Text weight="semibold">{formatInr(r.closing)}</Text>,
            },
          ]}
          emptyTitle="No schedule yet"
        />
      )}
    </>
  );
}

function Maintenance({ asset, canMaintain }: { asset: AssetDetail; canMaintain: boolean }) {
  const styles = useStyles();
  const plans = useQuery(assetQueries.plans(asset.id));
  const visits = useQuery(assetQueries.visits(asset.id));
  const [editing, setEditing] = useState<PmPlanDto | 'new' | null>(null);
  const [visiting, setVisiting] = useState<PmPlanDto | null>(null);
  const planTitle = new Map((plans.data ?? []).map((p) => [p.id, p.title]));
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 16 }}>
      {canMaintain && (
        <div>
          <Button icon={<AddRegular />} onClick={() => setEditing('new')}>
            Add maintenance plan
          </Button>
        </div>
      )}
      <DataTable<PmPlanDto>
        ariaLabel="Preventive maintenance plans"
        rows={plans.data}
        loading={plans.isLoading}
        error={plans.error}
        onRetry={() => void plans.refetch()}
        getRowId={(p) => p.id}
        onRowClick={canMaintain ? (p) => setEditing(p) : undefined}
        size="small"
        columns={[
          { key: 'title', header: 'Plan', render: (p) => <Text weight="semibold">{p.title}</Text> },
          { key: 'freq', header: 'Every', render: (p) => `${p.frequencyDays} days` },
          {
            key: 'due',
            header: 'Next due',
            render: (p) => (
              <span className={p.daysToDue < 0 ? styles.late : undefined}>
                {formatDateOnly(p.nextDueDate)}
                <Caption1 block>{dueLabel(p.daysToDue)}</Caption1>
              </span>
            ),
          },
          {
            key: 'last',
            header: 'Last done',
            priority: 'low',
            render: (p) => (p.lastDoneOn ? formatDateOnly(p.lastDoneOn) : 'Never'),
          },
          { key: 'who', header: 'Done by', priority: 'low', render: (p) => p.assignedTo ?? '—' },
          {
            key: 'active',
            header: 'Status',
            render: (p) => (
              <StatusBadge status={p.isActive ? 'Active' : 'Inactive'} tone={p.isActive ? 'success' : 'informative'} />
            ),
          },
        ]}
        rowActions={
          canMaintain
            ? (p) =>
                p.isActive ? (
                  <Button size="small" onClick={() => setVisiting(p)}>
                    Record visit
                  </Button>
                ) : null
            : undefined
        }
        emptyTitle="No maintenance plans"
        emptyDescription="Add the manufacturer's preventive maintenance schedule so visits are not missed."
      />
      <div>
        <Text weight="semibold" block style={{ marginBottom: 8 }}>
          Visit history
        </Text>
        <DataTable
          ariaLabel="Maintenance visits"
          rows={visits.data}
          loading={visits.isLoading}
          error={visits.error}
          onRetry={() => void visits.refetch()}
          getRowId={(v) => v.id}
          size="small"
          columns={[
            { key: 'date', header: 'Date', render: (v) => formatDateOnly(v.visitDate) },
            { key: 'plan', header: 'Plan', render: (v) => planTitle.get(v.planId) ?? '—' },
            {
              key: 'outcome',
              header: 'Outcome',
              render: (v) => (
                <DocStatus
                  status={v.outcome === 'NotDone' ? 'Skipped' : v.outcome === 'Completed' ? 'Completed' : 'Warning'}
                  label={pmOutcomeLabel[v.outcome]}
                />
              ),
            },
            { key: 'by', header: 'Done by', priority: 'low', render: (v) => v.performedBy ?? '—' },
            { key: 'findings', header: 'Findings', priority: 'low', render: (v) => v.findings ?? '—' },
            { key: 'cost', header: 'Cost', align: 'end', render: (v) => formatInr(v.cost) },
          ]}
          emptyTitle="No visits recorded yet"
        />
      </div>
      {editing && (
        <PmPlanDialog assetId={asset.id} plan={editing === 'new' ? null : editing} onClose={() => setEditing(null)} />
      )}
      {visiting && <PmVisitDialog plan={visiting} onClose={() => setVisiting(null)} />}
    </div>
  );
}

function Calibration({ asset, canMaintain }: { asset: AssetDetail; canMaintain: boolean }) {
  const calibrations = useQuery(assetQueries.calibrations(asset.id));
  const [recording, setRecording] = useState(false);
  return (
    <>
      {canMaintain && (
        <div style={{ marginBottom: 12, display: 'flex', gap: 12, alignItems: 'center' }}>
          <Button icon={<AddRegular />} onClick={() => setRecording(true)}>
            Record calibration
          </Button>
          {!asset.requiresCalibration && <Caption1>This asset is not marked as needing calibration.</Caption1>}
        </div>
      )}
      <DataTable
        ariaLabel="Calibration records"
        rows={calibrations.data}
        loading={calibrations.isLoading}
        error={calibrations.error}
        onRetry={() => void calibrations.refetch()}
        getRowId={(c) => c.id}
        size="small"
        columns={[
          { key: 'date', header: 'Calibrated', render: (c) => formatDateOnly(c.calibratedOn) },
          { key: 'agency', header: 'Agency', render: (c) => c.agency },
          { key: 'cert', header: 'Certificate', priority: 'low', render: (c) => c.certificateNumber ?? '—' },
          {
            key: 'result',
            header: 'Result',
            render: (c) => (
              <StatusBadge
                status={c.outcome === 'Fail' ? 'Failed' : 'Pass'}
                tone={c.outcome === 'Fail' ? 'danger' : 'success'}
                label={calibrationLabel[c.outcome]}
              />
            ),
          },
          { key: 'next', header: 'Next due', render: (c) => formatDateOnly(c.nextDueDate) },
          { key: 'remarks', header: 'Remarks', priority: 'low', render: (c) => c.remarks ?? '—' },
        ]}
        emptyTitle="No calibrations recorded"
      />
      {recording && <CalibrationDialog asset={asset} onClose={() => setRecording(false)} />}
    </>
  );
}

function Tickets({ asset, onReport }: { asset: AssetDetail; onReport: () => void }) {
  const navigate = useNavigate();
  const tickets = useQuery(assetQueries.tickets({ assetId: asset.id }));
  return (
    <>
      {!isRetired(asset.status) && (
        <div style={{ marginBottom: 12 }}>
          <Button icon={<WrenchRegular />} onClick={onReport}>
            Report breakdown
          </Button>
        </div>
      )}
      <DataTable
        ariaLabel="Breakdowns"
        rows={tickets.data?.items}
        loading={tickets.isLoading}
        error={tickets.error}
        onRetry={() => void tickets.refetch()}
        getRowId={(t) => t.id}
        onRowClick={(t) => void navigate({ to: '/assets/tickets', search: { id: t.id } })}
        size="small"
        columns={[
          { key: 'no', header: 'Ticket', render: (t) => <Text font="monospace">{t.number}</Text> },
          { key: 'issue', header: 'Problem', render: (t) => t.issue },
          {
            key: 'reported',
            header: 'Reported',
            render: (t) => `${formatRelative(t.reportedAt)}${t.reportedBy ? ` by ${t.reportedBy}` : ''}`,
          },
          { key: 'down', header: 'Downtime', priority: 'low', render: (t) => formatDowntime(t.downtimeMinutes) },
          {
            key: 'resolved',
            header: 'Resolved',
            priority: 'low',
            render: (t) => (t.resolvedAt ? formatDateTime(t.resolvedAt) : '—'),
          },
          { key: 'status', header: 'Status', render: (t) => <DocStatus status={t.status} /> },
        ]}
        emptyTitle="No breakdowns reported"
      />
    </>
  );
}
