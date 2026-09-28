import { Badge, Button, Caption1, makeStyles, mergeClasses, ProgressBar, Text, tokens } from '@fluentui/react-components';
import { AlertUrgentRegular, CheckmarkCircleRegular } from '@fluentui/react-icons';
import { formatRelative } from '@hc360/core';
import { ErrorState, Section } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { Link, useNavigate } from '@tanstack/react-router';
import { labQueries } from './api';
import { PriorityBadge } from './badges';
import { sectionLabel } from './labels';
import { bySection, complianceTone, formatMinutes, rejectionRate, withinTargetPercent } from './tat';
import { useLabLive } from './useLabLive';
import { criticalTubes } from './worklist';

const useStyles = makeStyles({
  list: { display: 'flex', flexDirection: 'column', gap: '2px', margin: 0, padding: 0, listStyleType: 'none' },
  item: {
    display: 'grid',
    gridTemplateColumns: 'minmax(0, 1fr) auto',
    gap: '2px 12px',
    padding: '8px 10px',
    borderRadius: tokens.borderRadiusMedium,
    textDecoration: 'none',
    color: tokens.colorNeutralForeground1,
    boxShadow: `inset 3px 0 0 ${tokens.colorPaletteRedBorder2}`,
    ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover },
    ':focus-visible': { outline: `2px solid ${tokens.colorStrokeFocus2}` },
  },
  muted: { color: tokens.colorNeutralForeground3 },
  clear: { display: 'flex', gap: '8px', alignItems: 'center', color: tokens.colorPaletteGreenForeground1 },
  stats: { display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(120px, 1fr))', gap: '12px', marginBottom: '12px' },
  stat: { display: 'flex', flexDirection: 'column', gap: '2px' },
  value: { fontSize: tokens.fontSizeBase500, fontWeight: tokens.fontWeightSemibold },
  bars: { display: 'flex', flexDirection: 'column', gap: '8px' },
  bar: { display: 'grid', gridTemplateColumns: 'minmax(0, 1fr) auto', gap: '2px 12px' },
  good: { color: tokens.colorPaletteGreenForeground1 },
  warning: { color: tokens.colorPaletteMarigoldForeground1 },
  bad: { color: tokens.colorPaletteRedForeground1 },
  none: { color: tokens.colorNeutralForeground3 },
});

/**
 * Home-screen widget: critical results waiting for the call to the doctor and verification. Opening one goes straight
 * to its result sheet.
 */
export function CriticalResultsWidget() {
  const styles = useStyles();
  const navigate = useNavigate();
  useLabLive();
  const worklist = useQuery(labQueries.worklist({}, 100));
  const tubes = criticalTubes(worklist.data?.items ?? []);

  return (
    <Section
      title="Critical results to call"
      description="Critical values waiting for the call to the doctor and verification"
      actions={
        <Button size="small" appearance="subtle" onClick={() => void navigate({ to: '/lab/worklist' })}>
          Result entry
        </Button>
      }
    >
      {worklist.error ? (
        <ErrorState error={worklist.error} onRetry={() => void worklist.refetch()} />
      ) : !worklist.data ? (
        <Caption1 className={styles.muted}>Checking…</Caption1>
      ) : tubes.length === 0 ? (
        <div className={styles.clear} role="status">
          <CheckmarkCircleRegular aria-hidden />
          <Text>No critical results are waiting.</Text>
        </div>
      ) : (
        <ul className={styles.list}>
          {tubes.slice(0, 6).map((tube) => (
            <li key={tube.sampleId}>
              <Link to="/lab/worklist" search={{ sampleId: tube.sampleId }} className={styles.item}>
                <span>
                  <Text weight="semibold">{tube.patientName}</Text>{' '}
                  <Caption1 className={styles.muted}>
                    {tube.age ?? '—'} / {tube.gender.charAt(0)} · {tube.uhid}
                  </Caption1>
                </span>
                <Badge color="danger" icon={<AlertUrgentRegular />} shape="rounded">
                  {tube.critical} critical
                </Badge>
                <Caption1 className={styles.muted}>
                  {tube.tests.join(', ')} · {sectionLabel[tube.section]}
                </Caption1>
                <span>
                  <PriorityBadge priority={tube.priority} /> <Caption1 className={styles.muted}>{tube.receivedAt ? formatRelative(tube.receivedAt) : ''}</Caption1>
                </span>
              </Link>
            </li>
          ))}
          {tubes.length > 6 && <Caption1 className={styles.muted}>and {tubes.length - 6} more on the worklist</Caption1>}
        </ul>
      )}
    </Section>
  );
}

/** Home-screen widget: today's lab turnaround and target compliance by bench. */
export function LabTatWidget() {
  const styles = useStyles();
  const navigate = useNavigate();
  const tat = useQuery(labQueries.tat());
  const report = tat.data;
  const within = report ? withinTargetPercent(report.tests) : null;
  const benches = report ? bySection(report.tests).filter((bench) => bench.released > 0) : [];
  const rate = report ? rejectionRate(report.samples, report.rejections) : null;

  return (
    <Section
      title="Lab turnaround today"
      description="Order to report release"
      actions={
        <Button size="small" appearance="subtle" onClick={() => void navigate({ to: '/lab/tat' })}>
          Details
        </Button>
      }
    >
      {tat.error ? (
        <ErrorState error={tat.error} onRetry={() => void tat.refetch()} />
      ) : !report ? (
        <Caption1 className={styles.muted}>Loading…</Caption1>
      ) : (
        <>
          <div className={styles.stats}>
            <div className={styles.stat}>
              <Caption1 className={styles.muted}>Median</Caption1>
              <span className={styles.value}>{formatMinutes(report.overall.medianMinutes)}</span>
            </div>
            <div className={styles.stat}>
              <Caption1 className={styles.muted}>90% within</Caption1>
              <span className={styles.value}>{formatMinutes(report.overall.p90Minutes)}</span>
            </div>
            <div className={styles.stat}>
              <Caption1 className={styles.muted}>On time</Caption1>
              <span className={mergeClasses(styles.value, styles[complianceTone(within)])}>{within === null ? '—' : `${within}%`}</span>
            </div>
            <div className={styles.stat}>
              <Caption1 className={styles.muted}>Released tests</Caption1>
              <span className={styles.value}>{report.overall.count.toLocaleString('en-IN')}</span>
            </div>
            <div className={styles.stat}>
              <Caption1 className={styles.muted}>Rejected samples</Caption1>
              <span className={styles.value}>
                {report.rejections}
                {rate !== null && <Caption1 className={styles.muted}> ({rate}%)</Caption1>}
              </span>
            </div>
          </div>
          {benches.length === 0 ? (
            <Caption1 className={styles.muted}>No reports released yet today.</Caption1>
          ) : (
            <div className={styles.bars}>
              {benches.map((bench) => (
                <div key={bench.section} className={styles.bar}>
                  <Text>{sectionLabel[bench.section]}</Text>
                  <Caption1>
                    {bench.withinTarget} of {bench.released} on time
                  </Caption1>
                  <div style={{ gridColumn: '1 / -1' }}>
                    <ProgressBar
                      value={bench.percent === null ? 0 : bench.percent / 100}
                      color={complianceTone(bench.percent) === 'good' ? 'success' : complianceTone(bench.percent) === 'warning' ? 'warning' : 'error'}
                      thickness="large"
                      aria-label={`${sectionLabel[bench.section]}: ${bench.percent ?? 0}% released within target`}
                    />
                  </div>
                </div>
              ))}
            </div>
          )}
        </>
      )}
    </Section>
  );
}
