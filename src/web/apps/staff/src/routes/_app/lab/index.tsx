import { Badge, Button, Caption1, makeStyles, Text, tokens } from '@fluentui/react-components';
import {
  AddRegular,
  AlertUrgentRegular,
  BeakerEditRegular,
  CheckmarkCircleRegular,
  ClipboardTaskListLtrRegular,
  DropRegular,
  LockClosedRegular,
  ShieldCheckmarkRegular,
  TimerRegular,
} from '@fluentui/react-icons';
import { formatTime } from '@hc360/core';
import { ErrorState, KpiCard, Page, PageHeader, Section } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { Can, usePermission } from '@/lib/permissions';
import { LAB_PERMISSIONS, labQueries } from '@/modules/lab/api';
import { QcOutcomeBadge } from '@/modules/lab/badges';
import { summarizeQc } from '@/modules/lab/qc';
import { formatMinutes, withinTargetPercent } from '@/modules/lab/tat';
import { TatChart } from '@/modules/lab/TatChart';
import { useLabLive } from '@/modules/lab/useLabLive';
import { CriticalResultsWidget } from '@/modules/lab/widgets';
import { worklistCounts } from '@/modules/lab/worklist';

export const Route = createFileRoute('/_app/lab/')({
  component: LabDashboard,
});

const useStyles = makeStyles({
  kpis: { display: 'grid', gap: '12px', gridTemplateColumns: 'repeat(auto-fill, minmax(180px, 1fr))' },
  grid: {
    display: 'grid',
    gap: '16px',
    gridTemplateColumns: 'minmax(0, 3fr) minmax(0, 2fr)',
    alignItems: 'start',
    '@media (max-width: 1180px)': { gridTemplateColumns: 'minmax(0, 1fr)' },
  },
  column: { display: 'flex', flexDirection: 'column', gap: '16px', minWidth: 0 },
  qcRow: {
    display: 'grid',
    gridTemplateColumns: 'minmax(0, 1fr) auto',
    gap: '2px 12px',
    alignItems: 'center',
    padding: '6px 0',
    borderBottom: `1px solid ${tokens.colorNeutralStroke3}`,
  },
  muted: { color: tokens.colorNeutralForeground3 },
  red: { color: tokens.colorPaletteRedForeground1 },
  green: { color: tokens.colorPaletteGreenForeground1 },
});

/** The laboratory at a glance: what to collect, what is on the benches, critical values, turnaround and QC. */
function LabDashboard() {
  const styles = useStyles();
  const navigate = useNavigate();
  const canCollectList = usePermission([LAB_PERMISSIONS.collect, LAB_PERMISSIONS.read]);
  const canBench = usePermission([LAB_PERMISSIONS.enter, LAB_PERMISSIONS.verify]);
  const canQc = usePermission([LAB_PERMISSIONS.qc, LAB_PERMISSIONS.verify, LAB_PERMISSIONS.enter]);
  const canRead = usePermission(LAB_PERMISSIONS.read);
  useLabLive();

  const pending = useQuery({ ...labQueries.pendingCollection({}, 1), enabled: canCollectList });
  const worklist = useQuery({ ...labQueries.worklist({}, 100), enabled: canBench });
  const qc = useQuery({ ...labQueries.qcStatus(), enabled: canQc });
  const tat = useQuery({ ...labQueries.tat(), enabled: canRead });

  const counts = worklist.data ? worklistCounts(worklist.data.items, worklist.data.totalCount) : null;
  const qcSummary = qc.data ? summarizeQc(qc.data) : null;
  const onTime = tat.data ? withinTargetPercent(tat.data.tests) : null;
  const value = (n: number | undefined | null) => (n === undefined || n === null ? '…' : n.toLocaleString('en-IN'));
  const qcAttention = qc.data?.filter((row) => row.locked || row.latestOutcome !== 'Accepted') ?? [];

  return (
    <Page>
      <PageHeader
        title="Laboratory"
        subtitle="Today at a glance. Updates as samples move through the lab."
        actions={
          <>
            <Can permission={LAB_PERMISSIONS.collect}>
              <Button
                appearance="primary"
                icon={<DropRegular />}
                onClick={() => void navigate({ to: '/lab/collection' })}
              >
                Collect samples
              </Button>
            </Can>
            <Can permission={[LAB_PERMISSIONS.enter, LAB_PERMISSIONS.verify]}>
              <Button icon={<BeakerEditRegular />} onClick={() => void navigate({ to: '/lab/worklist' })}>
                Enter results
              </Button>
            </Can>
            <Can permission={LAB_PERMISSIONS.create}>
              <Button icon={<AddRegular />} onClick={() => void navigate({ to: '/lab/orders/new' })}>
                New lab order
              </Button>
            </Can>
          </>
        }
      />

      <div className={styles.kpis}>
        {canCollectList && (
          <KpiCard
            label="To collect"
            value={value(pending.data?.totalCount)}
            hint="Tubes waiting for phlebotomy"
            icon={<DropRegular />}
            onClick={() => void navigate({ to: '/lab/collection' })}
          />
        )}
        {canBench && (
          <>
            <KpiCard
              label="In the lab"
              value={value(counts?.inLab)}
              hint={counts ? `${counts.stat} STAT` : undefined}
              icon={<ClipboardTaskListLtrRegular />}
              onClick={() => void navigate({ to: '/lab/worklist' })}
            />
            <KpiCard
              label="Awaiting verification"
              value={value(counts?.awaitingVerification)}
              hint="Samples with entered results"
              icon={<CheckmarkCircleRegular />}
              onClick={() => void navigate({ to: '/lab/worklist' })}
            />
            <KpiCard
              label="Critical to call"
              value={
                counts ? <span className={counts.critical > 0 ? styles.red : undefined}>{counts.critical}</span> : '…'
              }
              hint="Critical values not verified yet"
              icon={<AlertUrgentRegular />}
              onClick={() => void navigate({ to: '/lab/worklist' })}
            />
          </>
        )}
        {canRead && (
          <KpiCard
            label="Median turnaround today"
            value={tat.data ? formatMinutes(tat.data.overall.medianMinutes) : '…'}
            hint={onTime !== null ? `${onTime}% on time` : 'Nothing released yet'}
            icon={<TimerRegular />}
            onClick={() => void navigate({ to: '/lab/tat' })}
          />
        )}
        {canQc && (
          <KpiCard
            label="QC today"
            value={
              qcSummary ? (
                qcSummary.lockedTests.length > 0 ? (
                  <span className={styles.red}>{qcSummary.lockedTests.length} locked</span>
                ) : qcSummary.notRun > 0 ? (
                  `${qcSummary.notRun} not run`
                ) : (
                  <span className={styles.green}>All passed</span>
                )
              ) : (
                '…'
              )
            }
            hint={
              qcSummary
                ? `${qcSummary.accepted} accepted · ${qcSummary.warnings} warnings · ${qcSummary.rejected} rejected`
                : undefined
            }
            icon={qcSummary && qcSummary.lockedTests.length > 0 ? <LockClosedRegular /> : <ShieldCheckmarkRegular />}
            onClick={() => void navigate({ to: '/lab/qc' })}
          />
        )}
      </div>

      <div className={styles.grid}>
        <div className={styles.column}>
          {canRead && (
            <Section
              title="Turnaround today"
              description="Order to release for the busiest tests, against their targets"
              actions={
                <Button size="small" appearance="subtle" onClick={() => void navigate({ to: '/lab/tat' })}>
                  Details
                </Button>
              }
            >
              {tat.error ? (
                <ErrorState error={tat.error} onRetry={() => void tat.refetch()} />
              ) : !tat.data ? (
                <Caption1 className={styles.muted}>Loading…</Caption1>
              ) : tat.data.tests.some((row) => row.released > 0) ? (
                <TatChart rows={tat.data.tests} count={8} />
              ) : (
                <Caption1 className={styles.muted}>No reports released yet today.</Caption1>
              )}
            </Section>
          )}
        </div>
        <div className={styles.column}>
          {canBench && <CriticalResultsWidget />}
          {canQc && (
            <Section
              title="Quality control"
              description="Lots that need attention today"
              actions={
                <Button size="small" appearance="subtle" onClick={() => void navigate({ to: '/lab/qc' })}>
                  Open QC
                </Button>
              }
            >
              {qc.error ? (
                <ErrorState error={qc.error} onRetry={() => void qc.refetch()} />
              ) : !qc.data ? (
                <Caption1 className={styles.muted}>Loading…</Caption1>
              ) : qc.data.length === 0 ? (
                <Caption1 className={styles.muted}>No control lots are registered.</Caption1>
              ) : qcAttention.length === 0 ? (
                <Text className={styles.green}>
                  <CheckmarkCircleRegular aria-hidden /> Every control lot passed today.
                </Text>
              ) : (
                qcAttention.slice(0, 8).map((row) => (
                  <div key={row.lotId} className={styles.qcRow}>
                    <span>
                      <Text weight="semibold">{row.testName}</Text>{' '}
                      <Caption1 className={styles.muted}>
                        L{row.level} · {row.lotNumber}
                        {row.latestRunAt ? ` · ${formatTime(row.latestRunAt)}` : ''}
                      </Caption1>
                    </span>
                    <span>
                      {row.locked && (
                        <Badge color="danger" icon={<LockClosedRegular />} shape="rounded" style={{ marginRight: 6 }}>
                          Locked
                        </Badge>
                      )}
                      <QcOutcomeBadge outcome={row.latestOutcome} />
                    </span>
                  </div>
                ))
              )}
            </Section>
          )}
        </div>
      </div>
    </Page>
  );
}
