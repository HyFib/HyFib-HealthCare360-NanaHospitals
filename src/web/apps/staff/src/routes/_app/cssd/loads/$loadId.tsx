import {
  Badge,
  Button,
  Caption1,
  makeStyles,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  Tab,
  TabList,
  Text,
  tokens,
} from '@fluentui/react-components';
import { CheckmarkCircleRegular, DismissCircleRegular, FireRegular, PlayRegular, PrintRegular, TimerRegular } from '@fluentui/react-icons';
import { formatDateOnly, formatDateTime, isApiError, todayIso } from '@hc360/core';
import { ConfirmDialog, DataTable, ErrorState, KpiCard, LoadingState, Page, PageHeader, Section, StatusBadge, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState, type ReactNode } from 'react';
import { usePermission } from '@/lib/permissions';
import { cssdApi, cssdKeys, cssdQueries, ISSUE_STATUS_LABEL, LOAD_STATUS_LABEL, loadStatusTone, PACK_STATUS_LABEL, type LoadDto } from '@/modules/cssd/api';
import { IndicatorBadge } from '@/modules/cssd/IndicatorBadge';
import { BiologicalIndicatorDialog, CompleteLoadDialog } from '@/modules/cssd/LoadDialogs';
import { printPackLabels } from '@/modules/cssd/print';
import { daysToExpiry, METHOD_LABEL } from '@/modules/cssd/rules';
import { ReasonDialog } from '@/modules/inpatient/ReasonDialog';

type TabId = 'packs' | 'recall';

export const Route = createFileRoute('/_app/cssd/loads/$loadId')({
  validateSearch: (s: Record<string, unknown>): { tab?: TabId } => ({ tab: s.tab === 'recall' ? 'recall' : undefined }),
  component: LoadPage,
});

const useStyles = makeStyles({
  rows: { display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(220px, 1fr))', gap: '12px 24px' },
  item: { display: 'flex', flexDirection: 'column', gap: '2px' },
  label: { color: tokens.colorNeutralForeground3 },
  actions: { display: 'flex', gap: '8px', flexWrap: 'wrap' },
  kpis: { display: 'grid', gap: '12px', gridTemplateColumns: 'repeat(auto-fill, minmax(170px, 1fr))', marginBottom: '12px' },
});

function Item({ label, children }: { label: string; children: ReactNode }) {
  const styles = useStyles();
  return (
    <div className={styles.item}>
      <Caption1 className={styles.label}>{label}</Caption1>
      <Text>{children || '—'}</Text>
    </div>
  );
}

type Action = 'complete' | 'bi' | 'release' | 'abort' | null;

/** One sterilizer load: its cycle record, indicators, packs with expiry and, after a failure, the recall report. */
function LoadPage() {
  const styles = useStyles();
  const { loadId } = Route.useParams();
  const { tab = 'packs' } = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const notify = useNotify();
  const queryClient = useQueryClient();
  const canProcess = usePermission('cssd.process');
  const canRelease = usePermission('cssd.release');
  const load = useQuery(cssdQueries.load(loadId));
  const [action, setAction] = useState<Action>(null);

  const refresh = () => void queryClient.invalidateQueries({ queryKey: cssdKeys.all });
  const start = useMutation({
    mutationFn: () => cssdApi.startLoad(loadId),
    onSuccess: (l) => {
      notify.success(`Cycle ${l.cycleNumber} started`);
      refresh();
    },
    onError: (error) => notify.error(error, 'Could not start the cycle'),
  });
  const release = useMutation({
    mutationFn: () => cssdApi.releaseLoad(loadId),
    onSuccess: (l) => {
      notify.success(`Load ${l.cycleNumber} released`, `${l.packs.length} packs are on the shelf. Reprint labels to show the use-by date.`);
      setAction(null);
      refresh();
    },
  });
  const abort = useMutation({
    mutationFn: (reason: string) => cssdApi.abortLoad(loadId, reason),
    onSuccess: () => {
      notify.warning('Cycle aborted', 'Send the packs back for reprocessing.');
      setAction(null);
      refresh();
    },
  });

  if (load.isLoading) {
    return <LoadingState label="Loading the load…" />;
  }
  if (load.error || !load.data) {
    return (
      <Page maxWidth={900}>
        <ErrorState error={load.error} onRetry={() => void load.refetch()} title="Could not load this sterilizer load" />
      </Page>
    );
  }

  const l = load.data;
  const recallRequired = l.status === 'Failed' && l.releasedAt !== null;
  const canBi = canRelease && (l.status === 'Completed' || l.status === 'Released') && l.biologicalIndicator === 'Pending';
  const releaseBlocked = l.status === 'Completed' && l.containsImplants && l.biologicalIndicator !== 'Pass';

  const print = () => printPackLabels(l).catch((error: unknown) => notify.error(error, 'Printing failed'));

  return (
    <Page>
      <PageHeader
        title={`Load ${l.cycleNumber}`}
        icon={<FireRegular />}
        subtitle={`${l.sterilizerName} · ${METHOD_LABEL[l.method]}${l.program ? ` · ${l.program}` : ''} · ${l.packs.length} packs`}
        breadcrumbs={[{ label: 'Sterilizer loads', onClick: () => void navigate({ to: '/cssd/loads' }) }, { label: l.cycleNumber }]}
        actions={
          <div className={styles.actions}>
            <Button icon={<PrintRegular />} onClick={() => void print()}>
              Print pack labels
            </Button>
            {canProcess && l.status === 'Loaded' && (
              <Button appearance="primary" icon={<PlayRegular />} disabled={start.isPending} onClick={() => start.mutate()}>
                Start cycle
              </Button>
            )}
            {canProcess && l.status === 'Running' && (
              <Button appearance="primary" icon={<CheckmarkCircleRegular />} onClick={() => setAction('complete')}>
                Record cycle result
              </Button>
            )}
            {canRelease && l.status === 'Completed' && (
              <Button appearance="primary" icon={<CheckmarkCircleRegular />} disabled={releaseBlocked} onClick={() => setAction('release')}>
                Release packs
              </Button>
            )}
            {canBi && (
              <Button icon={<TimerRegular />} onClick={() => setAction('bi')}>
                Biological indicator
              </Button>
            )}
            {canProcess && (l.status === 'Loaded' || l.status === 'Running' || l.status === 'Completed') && (
              <Button icon={<DismissCircleRegular />} onClick={() => setAction('abort')}>
                Abort
              </Button>
            )}
          </div>
        }
      />

      {recallRequired && (
        <MessageBar intent="error" layout="multiline">
          <MessageBarBody>
            <MessageBarTitle>Recall: this load failed after release</MessageBarTitle>
            {l.failureReason} Every issue of this load has been flagged. See the recall report for departments and patients.
          </MessageBarBody>
        </MessageBar>
      )}
      {l.status === 'Failed' && !recallRequired && (
        <MessageBar intent="warning" layout="multiline">
          <MessageBarBody>
            <MessageBarTitle>Load failed</MessageBarTitle>
            {l.failureReason} The packs were never released: send them back for reprocessing.
          </MessageBarBody>
        </MessageBar>
      )}
      {releaseBlocked && (
        <MessageBar intent="warning">
          <MessageBarBody>This load contains implants: release it after the biological indicator passes.</MessageBarBody>
        </MessageBar>
      )}

      <Section title="Cycle record">
        <div className={styles.rows}>
          <Item label="Status">
            <StatusBadge status={l.status} label={LOAD_STATUS_LABEL[l.status]} tone={loadStatusTone(l.status)} />
          </Item>
          <Item label="Loaded">{`${formatDateTime(l.loadedAt)}${l.operatorName ? ` by ${l.operatorName}` : ''}`}</Item>
          <Item label="Started">{l.startedAt ? formatDateTime(l.startedAt) : null}</Item>
          <Item label="Completed">{l.completedAt ? formatDateTime(l.completedAt) : null}</Item>
          <Item label="Temperature">{l.temperatureC !== null ? `${l.temperatureC} °C` : null}</Item>
          <Item label="Exposure">{l.exposureMinutes !== null ? `${l.exposureMinutes} min` : null}</Item>
          <Item label="Pressure">{l.pressureKpa !== null ? `${l.pressureKpa} kPa` : null}</Item>
          <Item label="Indicators">
            <span style={{ display: 'inline-flex', gap: 4, flexWrap: 'wrap' }}>
              <IndicatorBadge result={l.chemicalIndicator} label="Chemical" />
              <IndicatorBadge result={l.biologicalIndicator} label="Biological" />
            </span>
          </Item>
          {l.biologicalReadAt && <Item label="Biological read">{`${formatDateTime(l.biologicalReadAt)}${l.biologicalIndicatorLot ? ` · lot ${l.biologicalIndicatorLot}` : ''}`}</Item>}
          <Item label="Released">{l.releasedAt ? `${formatDateTime(l.releasedAt)}${l.releasedByName ? ` by ${l.releasedByName}` : ''}` : null}</Item>
          <Item label="Implants">{l.containsImplants ? 'Yes (quarantine until biological pass)' : 'No'}</Item>
          {l.notes && <Item label="Notes">{l.notes}</Item>}
        </div>
      </Section>

      <Section flush>
        <div style={{ padding: '0 8px' }}>
          <TabList selectedValue={tab} onTabSelect={(_, d) => void navigate({ search: { tab: d.value === 'recall' ? 'recall' : undefined }, replace: true })}>
            <Tab value="packs">Packs ({l.packs.length})</Tab>
            <Tab value="recall">Where the packs went</Tab>
          </TabList>
        </div>
        <div style={{ padding: 16 }}>{tab === 'packs' ? <Packs load={l} /> : <RecallTab loadId={l.id} />}</div>
      </Section>

      {action === 'complete' && <CompleteLoadDialog load={l} onClose={() => setAction(null)} />}
      {action === 'bi' && <BiologicalIndicatorDialog load={l} onClose={() => setAction(null)} />}
      <ConfirmDialog
        open={action === 'release'}
        title={`Release load ${l.cycleNumber}?`}
        confirmLabel="Release packs"
        busy={release.isPending}
        onConfirm={() => release.mutate()}
        onCancel={() => {
          setAction(null);
          release.reset();
        }}
      >
        The {l.packs.length} packs go on the shelf, dated today plus each set's shelf life. The chemical indicator passed and the parameters are within range.
        {release.error && (
          <MessageBar intent="error" style={{ marginTop: 12 }}>
            <MessageBarBody>{isApiError(release.error) ? release.error.userMessage : 'The load could not be released.'}</MessageBarBody>
          </MessageBar>
        )}
      </ConfirmDialog>
      <ReasonDialog
        open={action === 'abort'}
        title={`Abort cycle ${l.cycleNumber}?`}
        description="The load fails and its packs go back for reprocessing."
        placeholder="e.g. Power failure at 12 minutes, door seal fault"
        confirmLabel="Abort cycle"
        destructive
        busy={abort.isPending}
        error={abort.error}
        onConfirm={(reason) => abort.mutate(reason)}
        onCancel={() => {
          setAction(null);
          abort.reset();
        }}
      />
    </Page>
  );
}

function Packs({ load }: { load: LoadDto }) {
  const today = todayIso();
  return (
    <DataTable
      ariaLabel="Packs in this load"
      rows={load.packs}
      getRowId={(p) => p.id}
      size="small"
      columns={[
        { key: 'number', header: 'Pack', width: 200, render: (p) => <Text font="monospace">{p.packNumber}</Text> },
        { key: 'set', header: 'Set', render: (p) => p.setName ?? '—' },
        {
          key: 'expiry',
          header: 'Use by',
          width: 150,
          render: (p) => {
            if (!p.sterileUntil) {
              return '—';
            }
            const days = daysToExpiry(p.sterileUntil, today);
            return (
              <span>
                {formatDateOnly(p.sterileUntil)}
                {p.isExpired ? (
                  <Badge size="small" color="danger" appearance="tint" style={{ marginLeft: 6 }}>
                    Expired
                  </Badge>
                ) : days !== null && days <= 2 ? (
                  <Badge size="small" color="warning" appearance="tint" style={{ marginLeft: 6 }}>
                    {days === 0 ? 'Today' : `${days} d`}
                  </Badge>
                ) : null}
              </span>
            );
          },
        },
        { key: 'status', header: 'Status', width: 140, render: (p) => <StatusBadge status={p.status} label={PACK_STATUS_LABEL[p.status]} tone={p.status === 'Available' ? 'success' : p.status === 'Recalled' || p.status === 'Reprocess' ? 'danger' : undefined} /> },
      ]}
      emptyTitle="No packs in this load"
    />
  );
}

function RecallTab({ loadId }: { loadId: string }) {
  const styles = useStyles();
  const navigate = useNavigate();
  const report = useQuery(cssdQueries.recall(loadId));
  const r = report.data;
  if (report.error) {
    return <ErrorState error={report.error} onRetry={() => void report.refetch()} />;
  }
  return (
    <div>
      {r && (
        <div className={styles.kpis}>
          <KpiCard label="Issues of this load" value={r.issues.length} />
          <KpiCard label="Flagged for recall" value={r.issuesFlagged} hint={r.recallRequired ? 'Recall in progress' : 'No recall needed'} />
          <KpiCard label="Acknowledged" value={`${r.issuesAcknowledged}/${r.issuesFlagged}`} />
          <KpiCard label="Patients exposed" value={r.patientsExposed} hint="For infection-control review" />
          <KpiCard label="Shelf packs withdrawn" value={r.packsOnShelfRecalled} />
        </div>
      )}
      <DataTable
        ariaLabel="Where the packs of this load went"
        rows={r?.issues}
        loading={report.isLoading}
        getRowId={(i) => i.issueId}
        size="small"
        columns={[
          { key: 'pack', header: 'Pack', width: 190, render: (i) => <Text font="monospace">{i.packNumber ?? '—'}</Text> },
          { key: 'dept', header: 'Department', render: (i) => `${i.departmentName ?? '—'} · ${formatDateTime(i.issuedAt)}` },
          {
            key: 'patient',
            header: 'Used on',
            render: (i) =>
              i.patientId ? (
                <Button size="small" appearance="transparent" onClick={() => void navigate({ to: '/patients/$patientId', params: { patientId: i.patientId! } })}>
                  {i.patientName ?? 'Patient'} ({i.patientUhid ?? ''})
                </Button>
              ) : (
                '—'
              ),
          },
          { key: 'status', header: 'Status', width: 130, render: (i) => <StatusBadge status={i.status} label={ISSUE_STATUS_LABEL[i.status]} /> },
          {
            key: 'recall',
            header: 'Recall',
            render: (i) =>
              i.recallFlaggedAt ? (
                i.recallAcknowledgedAt ? (
                  <span>
                    <Badge appearance="tint" color="success">
                      Acknowledged
                    </Badge>
                    {i.recallAction && <Caption1 block>{i.recallAction}</Caption1>}
                  </span>
                ) : (
                  <Badge appearance="tint" color="danger">
                    Open
                  </Badge>
                )
              ) : (
                '—'
              ),
          },
        ]}
        emptyTitle="No packs of this load were issued"
      />
    </div>
  );
}
