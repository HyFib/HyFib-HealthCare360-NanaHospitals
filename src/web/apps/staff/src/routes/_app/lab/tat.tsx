import { Caption1, Field, makeStyles, Select, Text, tokens } from '@fluentui/react-components';
import { ClockRegular, DataTrendingRegular, TimerRegular } from '@fluentui/react-icons';
import { formatDate, todayIso } from '@hc360/core';
import { DataTable, EmptyState, ErrorState, FilterBar, KpiCard, Page, PageHeader, Section } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { LAB_SECTIONS, labQueries, type LabSection, type LabTatRow } from '@/modules/lab/api';
import { DateRangeFields } from '@/modules/lab/common/DateRangeFields';
import { searchDate, searchOneOf } from '@/modules/lab/common/search';
import { rejectionReasonLabel, sectionLabel } from '@/modules/lab/labels';
import { complianceTone, formatMinutes, rejectionRate, withinTargetPercent } from '@/modules/lab/tat';
import { RejectionChart, TatChart } from '@/modules/lab/TatChart';

interface TatSearch {
  from?: string;
  to?: string;
  section?: LabSection;
}

export const Route = createFileRoute('/_app/lab/tat')({
  validateSearch: (s: Record<string, unknown>): TatSearch => ({
    from: searchDate(s.from),
    to: searchDate(s.to),
    section: searchOneOf(s.section, LAB_SECTIONS),
  }),
  component: LabTatPage,
});

const useStyles = makeStyles({
  kpis: { display: 'grid', gap: '12px', gridTemplateColumns: 'repeat(auto-fill, minmax(170px, 1fr))' },
  charts: {
    display: 'grid',
    gap: '16px',
    gridTemplateColumns: 'minmax(0, 2fr) minmax(0, 1fr)',
    '@media (max-width: 1100px)': { gridTemplateColumns: 'minmax(0, 1fr)' },
  },
  muted: { color: tokens.colorNeutralForeground3 },
  cell: { display: 'flex', flexDirection: 'column', gap: '2px' },
  good: { color: tokens.colorPaletteGreenForeground1, fontWeight: tokens.fontWeightSemibold },
  warning: { color: tokens.colorPaletteMarigoldForeground1, fontWeight: tokens.fontWeightSemibold },
  bad: { color: tokens.colorPaletteRedForeground1, fontWeight: tokens.fontWeightSemibold },
  none: { color: tokens.colorNeutralForeground3 },
});

/**
 * Laboratory turnaround time (SRS LIS-07, NABH quality indicators): order → release and receipt → verification per
 * test (median and 90th percentile, nearest rank), share released within the target, and sample rejections by reason.
 */
function LabTatPage() {
  const styles = useStyles();
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const tat = useQuery(labQueries.tat(search.from, search.to, search.section));
  const report = tat.data;
  const within = report ? withinTargetPercent(report.tests) : null;
  const rate = report ? rejectionRate(report.samples, report.rejections) : null;
  const today = todayIso();

  return (
    <Page>
      <PageHeader
        title="Lab turnaround"
        subtitle={
          report
            ? report.from === report.to
              ? report.to === today
                ? 'Today'
                : formatDate(report.from)
              : `${formatDate(report.from)} to ${formatDate(report.to)}`
            : 'Order to report release, per test'
        }
      />
      <Section>
        <FilterBar>
          <DateRangeFields
            value={{ from: search.from, to: search.to }}
            onChange={(range) => void navigate({ search: (prev) => ({ ...prev, ...range }) })}
          />
          <Field label="Bench">
            <Select
              value={search.section ?? ''}
              onChange={(_, data) =>
                void navigate({
                  search: (prev) => ({ ...prev, section: (data.value || undefined) as LabSection | undefined }),
                })
              }
            >
              <option value="">All benches</option>
              {LAB_SECTIONS.map((value) => (
                <option key={value} value={value}>
                  {sectionLabel[value]}
                </option>
              ))}
            </Select>
          </Field>
        </FilterBar>
      </Section>

      {tat.error ? (
        <ErrorState error={tat.error} onRetry={() => void tat.refetch()} />
      ) : !report ? (
        <Section>
          <Caption1 className={styles.muted}>Calculating…</Caption1>
        </Section>
      ) : (
        <>
          <div className={styles.kpis}>
            <KpiCard
              label="Median, order to release"
              value={formatMinutes(report.overall.medianMinutes)}
              icon={<TimerRegular />}
            />
            <KpiCard
              label="90% released within"
              value={formatMinutes(report.overall.p90Minutes)}
              icon={<ClockRegular />}
            />
            <KpiCard
              label="Released on time"
              value={<span className={styles[complianceTone(within)]}>{within === null ? '—' : `${within}%`}</span>}
              hint="Within each test's target"
              icon={<DataTrendingRegular />}
            />
            <KpiCard label="Tests released" value={report.overall.count.toLocaleString('en-IN')} />
            <KpiCard label="Samples collected" value={report.samples.toLocaleString('en-IN')} />
            <KpiCard
              label="Samples rejected"
              value={report.rejections.toLocaleString('en-IN')}
              hint={rate !== null ? `${rate}% of samples` : undefined}
            />
          </div>

          {report.tests.length === 0 ? (
            <Section>
              <EmptyState title="No tests in this period" description="Choose other dates or another bench." />
            </Section>
          ) : (
            <>
              <div className={styles.charts}>
                <Section
                  title="Busiest tests"
                  description="Median and 90th percentile against the target (diamond), hours"
                >
                  {report.tests.some((row) => row.released > 0) ? (
                    <TatChart rows={report.tests} count={12} />
                  ) : (
                    <Caption1 className={styles.muted}>Nothing released yet.</Caption1>
                  )}
                </Section>
                <Section title="Rejected samples" description="By reason">
                  {report.rejectionsByReason.length > 0 ? (
                    <RejectionChart stats={report.rejectionsByReason} />
                  ) : (
                    <Caption1 className={styles.muted}>No samples were rejected.</Caption1>
                  )}
                  {report.rejectionsByReason.length > 0 && (
                    <Caption1 block className={styles.muted} style={{ marginTop: 8 }}>
                      Most common: {rejectionReasonLabel[report.rejectionsByReason[0]!.reason]}
                    </Caption1>
                  )}
                </Section>
              </div>

              <Section title="By test" flush>
                <DataTable
                  ariaLabel="Turnaround by test"
                  rows={report.tests}
                  getRowId={(row) => row.labTestId}
                  size="small"
                  columns={[
                    {
                      key: 'test',
                      header: 'Test',
                      render: (row: LabTatRow) => (
                        <span className={styles.cell}>
                          <Text weight="semibold">{row.testName}</Text>
                          <Caption1 className={styles.muted}>
                            {row.testCode} · {sectionLabel[row.section]}
                          </Caption1>
                        </span>
                      ),
                    },
                    { key: 'target', header: 'Target', width: 80, render: (row: LabTatRow) => `${row.targetHours} h` },
                    {
                      key: 'released',
                      header: 'Released',
                      width: 90,
                      align: 'end',
                      render: (row: LabTatRow) => row.released.toLocaleString('en-IN'),
                    },
                    {
                      key: 'ontime',
                      header: 'On time',
                      width: 90,
                      render: (row: LabTatRow) => (
                        <span className={styles[complianceTone(row.withinTargetPercent)]}>
                          {row.withinTargetPercent === null ? '—' : `${row.withinTargetPercent}%`}
                        </span>
                      ),
                    },
                    {
                      key: 'or',
                      header: 'Order to release',
                      render: (row: LabTatRow) => (
                        <span className={styles.cell}>
                          <Text>{formatMinutes(row.orderToRelease.medianMinutes)}</Text>
                          <Caption1 className={styles.muted}>
                            90%: {formatMinutes(row.orderToRelease.p90Minutes)}
                          </Caption1>
                        </span>
                      ),
                    },
                    {
                      key: 'rv',
                      header: 'Receipt to verification',
                      priority: 'low',
                      render: (row: LabTatRow) => (
                        <span className={styles.cell}>
                          <Text>{formatMinutes(row.receiptToVerification.medianMinutes)}</Text>
                          <Caption1 className={styles.muted}>
                            90%: {formatMinutes(row.receiptToVerification.p90Minutes)}
                          </Caption1>
                        </span>
                      ),
                    },
                  ]}
                />
              </Section>
            </>
          )}
        </>
      )}
    </Page>
  );
}
