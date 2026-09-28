import {
  Badge,
  Button,
  Caption1,
  Field,
  makeStyles,
  Menu,
  MenuItem,
  MenuList,
  MenuPopover,
  MenuTrigger,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  Select,
  Switch,
  Text,
  tokens,
} from '@fluentui/react-components';
import { AddRegular, ClipboardTaskListLtrRegular, EditRegular, LockClosedRegular, MoreHorizontalRegular, ShieldErrorRegular } from '@fluentui/react-icons';
import { formatDate, formatDateOnly, formatDateTime, formatTime, todayIso } from '@hc360/core';
import { DataTable, EmptyState, ErrorState, KpiCard, Page, PageHeader, Section, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { LAB_PERMISSIONS, labApi, labKeys, labQueries, type QcLotDto, type QcRunDto, type QcStatusRow } from '@/modules/lab/api';
import { QcOutcomeBadge } from '@/modules/lab/badges';
import { searchFlag, searchId, searchInt, searchPage } from '@/modules/lab/common/search';
import { LeveyJenningsChart } from '@/modules/lab/LeveyJenningsChart';
import { QcLotDrawer, QcReviewDialog, QcRunDialog, type RunLot } from '@/modules/lab/QcDialogs';
import { QcOverrideDialog } from '@/modules/lab/QcOverrideDialog';
import { westgardRuleText } from '@/modules/lab/labels';
import { formatZ, summarizeQc } from '@/modules/lab/qc';
import { useLabLive } from '@/modules/lab/useLabLive';

interface QcSearch {
  lotId?: string;
  days?: number;
  inactive?: true;
  page?: number;
}

export const Route = createFileRoute('/_app/lab/qc/')({
  validateSearch: (s: Record<string, unknown>): QcSearch => ({
    lotId: searchId(s.lotId),
    days: searchInt(s.days, 7, 365),
    inactive: searchFlag(s.inactive),
    page: searchPage(s.page),
  }),
  component: QcPage,
});

const PERIODS = [7, 30, 60, 90, 180, 365] as const;

const useStyles = makeStyles({
  kpis: { display: 'grid', gap: '12px', gridTemplateColumns: 'repeat(auto-fill, minmax(160px, 1fr))' },
  layout: {
    display: 'grid',
    gridTemplateColumns: 'minmax(300px, 420px) minmax(0, 1fr)',
    gap: '16px',
    alignItems: 'start',
    '@media (max-width: 1180px)': { gridTemplateColumns: 'minmax(0, 1fr)' },
  },
  detail: { display: 'flex', flexDirection: 'column', gap: '16px', minWidth: 0 },
  facts: { display: 'flex', gap: '8px 20px', flexWrap: 'wrap', color: tokens.colorNeutralForeground2 },
  muted: { color: tokens.colorNeutralForeground3 },
  cell: { display: 'flex', flexDirection: 'column', gap: '2px', minWidth: 0 },
  mono: { fontFamily: tokens.fontFamilyMonospace },
  bad: { color: tokens.colorPaletteRedForeground1 },
});

/**
 * Quality control (SRS LIS-06): today's QC board, control lots, Levey-Jennings charts with ±1/2/3 SD lines and the
 * Westgard violations with their review. A rejected run locks verification of the assay until QC passes or a
 * pathologist overrides it with an audited reason.
 */
function QcPage() {
  const styles = useStyles();
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const notify = useNotify();
  const queryClient = useQueryClient();
  const canManage = usePermission(LAB_PERMISSIONS.qc);
  const canVerify = usePermission(LAB_PERMISSIONS.verify);
  const [editing, setEditing] = useState<QcLotDto | 'new' | null>(null);
  const [runFor, setRunFor] = useState<RunLot | null>(null);
  const [reviewing, setReviewing] = useState<QcRunDto | null>(null);
  const [overriding, setOverriding] = useState<QcStatusRow | null>(null);
  useLabLive();

  const status = useQuery(labQueries.qcStatus());
  const lots = useQuery(labQueries.qcLots(Boolean(search.inactive)));
  const days = search.days ?? 30;
  const from = todayIso(-days);
  const to = todayIso();
  const selectedLot = lots.data?.find((lot) => lot.id === search.lotId) ?? null;
  const chart = useQuery({ ...labQueries.leveyJennings(search.lotId ?? '', from, to), enabled: Boolean(search.lotId) });
  const runs = useQuery({ ...labQueries.qcRuns({ lotId: search.lotId, from, to, page: search.page }), enabled: Boolean(search.lotId) });
  const summary = status.data ? summarizeQc(status.data) : null;

  const select = (lotId: string) => void navigate({ search: (prev) => ({ ...prev, lotId, page: undefined }), replace: true });
  const lotById = (id: string) => lots.data?.find((lot) => lot.id === id);

  const override = useMutation({
    mutationFn: ({ row, reason }: { row: QcStatusRow; reason: string }) => labApi.overrideQc(row.labTestId, reason),
    onSuccess: (_, { row }) => {
      setOverriding(null);
      notify.success(`QC lock overridden for ${row.testName}`, 'The override is recorded in the audit trail.');
      void queryClient.invalidateQueries({ queryKey: labKeys.qc });
    },
    onError: (error) => notify.error(error, 'Could not override the QC lock'),
  });

  const lockedRows = status.data?.filter((row) => row.locked) ?? [];

  return (
    <Page>
      <PageHeader
        title="Quality control"
        subtitle="Today's QC, Levey-Jennings charts and Westgard rules"
        actions={
          canManage && (
            <Button appearance="primary" icon={<AddRegular />} onClick={() => setEditing('new')}>
              Register control lot
            </Button>
          )
        }
      />

      {lockedRows.length > 0 && (
        <MessageBar intent="error" icon={<ShieldErrorRegular />} layout="multiline">
          <MessageBarBody>
            <MessageBarTitle>Verification locked</MessageBarTitle>
            Today's QC was rejected for {[...new Set(lockedRows.map((row) => row.testName))].join(', ')}. Correct the problem and run QC again
            {canVerify ? ', or override the lock with a reason if the patient results are reliable.' : '.'}
          </MessageBarBody>
        </MessageBar>
      )}

      {summary && (
        <div className={styles.kpis}>
          <KpiCard label="Control lots in use" value={summary.lots} icon={<ClipboardTaskListLtrRegular />} />
          <KpiCard label="Accepted today" value={summary.accepted} />
          <KpiCard label="Warnings (1-2s)" value={summary.warnings} />
          <KpiCard label="Rejected today" value={summary.rejected} icon={summary.rejected > 0 ? <LockClosedRegular /> : undefined} />
          <KpiCard label="Not run yet today" value={summary.notRun} hint={summary.notRun > 0 ? 'Run QC before reporting patients' : undefined} />
        </div>
      )}

      <Section title="Today's QC" description="Latest run of each control lot today" flush>
        <DataTable
          ariaLabel="Today's QC by control lot"
          rows={status.data}
          loading={status.isLoading}
          error={status.error}
          onRetry={() => void status.refetch()}
          getRowId={(row) => row.lotId}
          onRowClick={(row) => select(row.lotId)}
          size="small"
          columns={[
            {
              key: 'test',
              header: 'Test',
              render: (row: QcStatusRow) => (
                <span className={styles.cell}>
                  <Text weight="semibold">{row.testName}</Text>
                  <Caption1 className={styles.muted}>
                    Level {row.level} · lot {row.lotNumber}
                    {row.instrumentCode ? ` · ${row.instrumentCode}` : ''}
                  </Caption1>
                </span>
              ),
            },
            { key: 'outcome', header: 'Latest run', width: 160, render: (row: QcStatusRow) => <QcOutcomeBadge outcome={row.latestOutcome} /> },
            { key: 'time', header: 'At', width: 90, priority: 'low', render: (row: QcStatusRow) => (row.latestRunAt ? formatTime(row.latestRunAt) : '—') },
            {
              key: 'lock',
              header: 'Verification',
              width: 140,
              render: (row: QcStatusRow) =>
                row.locked ? (
                  <Badge color="danger" icon={<LockClosedRegular />} shape="rounded">
                    Locked
                  </Badge>
                ) : row.overridden ? (
                  <Badge color="warning" shape="rounded" appearance="tint">
                    Overridden
                  </Badge>
                ) : (
                  <Caption1 className={styles.muted}>Open</Caption1>
                ),
            },
          ]}
          rowActions={
            canManage || canVerify
              ? (row: QcStatusRow) => {
                  const lot = lotById(row.lotId);
                  return (
                    <Menu>
                      <MenuTrigger disableButtonEnhancement>
                        <Button size="small" appearance="subtle" icon={<MoreHorizontalRegular />} aria-label={`Actions for ${row.testName} level ${row.level}`} />
                      </MenuTrigger>
                      <MenuPopover>
                        <MenuList>
                          {canManage && lot && (
                            <MenuItem icon={<AddRegular />} onClick={() => setRunFor(lot)}>
                              Record QC run
                            </MenuItem>
                          )}
                          {canVerify && row.locked && (
                            <MenuItem icon={<ShieldErrorRegular />} onClick={() => setOverriding(row)}>
                              Override lock
                            </MenuItem>
                          )}
                          <MenuItem onClick={() => select(row.lotId)}>Show chart</MenuItem>
                        </MenuList>
                      </MenuPopover>
                    </Menu>
                  );
                }
              : undefined
          }
          emptyTitle="No control lots in use"
          emptyDescription={canManage ? 'Register the control lots you run every day.' : 'Ask a QC manager to register the control lots.'}
        />
      </Section>

      <div className={styles.layout}>
        <Section
          title="Control lots"
          actions={<Switch checked={Boolean(search.inactive)} onChange={(_, data) => void navigate({ search: (prev) => ({ ...prev, inactive: data.checked ? true : undefined }), replace: true })} label="Show old lots" />}
          flush
        >
          <DataTable
            ariaLabel="Control lots"
            rows={lots.data}
            loading={lots.isLoading}
            error={lots.error}
            onRetry={() => void lots.refetch()}
            getRowId={(lot) => lot.id}
            onRowClick={(lot) => select(lot.id)}
            size="small"
            columns={[
              {
                key: 'lot',
                header: 'Lot',
                render: (lot: QcLotDto) => (
                  <span className={styles.cell}>
                    <Text weight={lot.id === search.lotId ? 'bold' : 'semibold'}>
                      {lot.testName} · L{lot.level}
                    </Text>
                    <Caption1 className={styles.muted}>
                      {lot.lotNumber} · {lot.targetMean} ± {lot.targetSd} {lot.unit ?? ''}
                    </Caption1>
                  </span>
                ),
              },
              {
                key: 'expiry',
                header: 'Expiry',
                width: 110,
                render: (lot: QcLotDto) =>
                  lot.isExpired ? (
                    <Badge color="danger" appearance="tint" shape="rounded">
                      Expired
                    </Badge>
                  ) : !lot.isActive ? (
                    <Badge appearance="outline" shape="rounded">
                      Not in use
                    </Badge>
                  ) : (
                    <Caption1>{formatDateOnly(lot.expiryDate)}</Caption1>
                  ),
              },
            ]}
            emptyTitle="No control lots"
          />
        </Section>

        <div className={styles.detail}>
          {!search.lotId ? (
            <Section>
              <EmptyState title="Choose a control lot" description="Its Levey-Jennings chart, runs and Westgard violations appear here." />
            </Section>
          ) : (
            <>
              <Section
                title={selectedLot ? `${selectedLot.testName} · level ${selectedLot.level}` : 'Levey-Jennings chart'}
                description={
                  selectedLot ? (
                    <span className={styles.facts}>
                      <span>Lot {selectedLot.lotNumber}</span>
                      <span>{selectedLot.material}</span>
                      <span>
                        Target {selectedLot.targetMean} ± {selectedLot.targetSd} {selectedLot.unit ?? ''}
                      </span>
                      <span>Expires {formatDateOnly(selectedLot.expiryDate)}</span>
                      {selectedLot.instrumentCode && <span>{selectedLot.instrumentCode}</span>}
                    </span>
                  ) : undefined
                }
                actions={
                  <>
                    <Field label="Period" orientation="horizontal">
                      <Select
                        value={String(days)}
                        onChange={(_, data) => void navigate({ search: (prev) => ({ ...prev, days: Number(data.value) === 30 ? undefined : Number(data.value), page: undefined }), replace: true })}
                      >
                        {PERIODS.map((period) => (
                          <option key={period} value={period}>
                            Last {period} days
                          </option>
                        ))}
                      </Select>
                    </Field>
                    {canManage && selectedLot && (
                      <>
                        <Button icon={<AddRegular />} appearance="primary" onClick={() => setRunFor(selectedLot)} disabled={!selectedLot.isActive || selectedLot.isExpired}>
                          Record run
                        </Button>
                        <Button icon={<EditRegular />} onClick={() => setEditing(selectedLot)}>
                          Edit lot
                        </Button>
                      </>
                    )}
                  </>
                }
              >
                {chart.error ? (
                  <ErrorState error={chart.error} onRetry={() => void chart.refetch()} />
                ) : !chart.data ? (
                  <Caption1 className={styles.muted}>Loading the chart…</Caption1>
                ) : chart.data.points.length === 0 ? (
                  <EmptyState title="No runs in this period" description="Record QC runs to build the chart, or choose a longer period." />
                ) : (
                  <LeveyJenningsChart chart={chart.data} />
                )}
              </Section>

              <Section title="Runs" description="Newest first. Rejected runs need a corrective action." flush>
                <DataTable
                  ariaLabel="QC runs of this lot"
                  rows={runs.data?.items}
                  loading={runs.isLoading}
                  error={runs.error}
                  onRetry={() => void runs.refetch()}
                  getRowId={(run) => run.id}
                  size="small"
                  columns={[
                    {
                      key: 'at',
                      header: 'Run',
                      width: 150,
                      render: (run: QcRunDto) => (
                        <span className={styles.cell}>
                          <Text>{formatTime(run.runAt)}</Text>
                          <Caption1 className={styles.muted}>{formatDate(run.runAt)}</Caption1>
                        </span>
                      ),
                    },
                    {
                      key: 'value',
                      header: 'Value',
                      width: 140,
                      render: (run: QcRunDto) => (
                        <span className={styles.cell}>
                          <Text weight="semibold">{run.value}</Text>
                          <Caption1 className={styles.muted}>{formatZ(run.zScore)}</Caption1>
                        </span>
                      ),
                    },
                    {
                      key: 'outcome',
                      header: 'Westgard',
                      render: (run: QcRunDto) => (
                        <span className={styles.cell}>
                          <QcOutcomeBadge outcome={run.outcome} />
                          {run.violations.length > 0 && <Caption1>{run.violations.map((rule) => westgardRuleText[rule] ?? rule).join('; ')}</Caption1>}
                          {run.overridden && <Caption1 className={styles.muted}>Lock overridden</Caption1>}
                        </span>
                      ),
                    },
                    {
                      key: 'review',
                      header: 'Review',
                      priority: 'low',
                      render: (run: QcRunDto) =>
                        run.reviewedAt ? (
                          <span className={styles.cell}>
                            <Caption1>{run.correctiveAction ?? run.reviewNote ?? 'Reviewed'}</Caption1>
                            <Caption1 className={styles.muted}>
                              {run.reviewedBy ?? ''} · {formatDateTime(run.reviewedAt)}
                            </Caption1>
                          </span>
                        ) : run.outcome === 'Rejected' ? (
                          <Caption1 className={styles.bad}>Corrective action due</Caption1>
                        ) : (
                          <Caption1 className={styles.muted}>{run.recordedBy ? `By ${run.recordedBy}` : '—'}</Caption1>
                        ),
                    },
                  ]}
                  rowActions={
                    canManage
                      ? (run: QcRunDto) => (
                          <Button size="small" appearance={run.outcome === 'Rejected' && !run.reviewedAt ? 'primary' : 'subtle'} onClick={() => setReviewing(run)}>
                            {run.reviewedAt ? 'Edit review' : 'Review'}
                          </Button>
                        )
                      : undefined
                  }
                  emptyTitle="No runs in this period"
                  page={search.page ?? 1}
                  pageSize={25}
                  totalCount={runs.data?.totalCount}
                  onPageChange={(page) => void navigate({ search: (prev) => ({ ...prev, page: page > 1 ? page : undefined }) })}
                />
              </Section>
            </>
          )}
        </div>
      </div>

      {editing && <QcLotDrawer lot={editing === 'new' ? null : editing} onClose={() => setEditing(null)} onSaved={(lotId) => lotId && select(lotId)} />}
      {runFor && <QcRunDialog key={runFor.id} lot={runFor} onClose={() => setRunFor(null)} />}
      {reviewing && <QcReviewDialog key={reviewing.id} run={reviewing} testName={selectedLot?.testName ?? 'Control'} onClose={() => setReviewing(null)} />}
      {overriding && (
        <QcOverrideDialog
          assays={[{ testName: overriding.testName, violations: [] }]}
          busy={override.isPending}
          confirmLabel="Override lock"
          onCancel={() => setOverriding(null)}
          onConfirm={(reason) => override.mutate({ row: overriding, reason })}
        />
      )}
    </Page>
  );
}
