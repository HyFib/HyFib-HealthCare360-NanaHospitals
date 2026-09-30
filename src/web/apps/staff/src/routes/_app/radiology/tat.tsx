import { Caption1, Field, makeStyles, Select, Text, tokens } from '@fluentui/react-components';
import { formatDate, todayIso } from '@hc360/core';
import { DataTable, EmptyState, ErrorState, FilterBar, Page, PageHeader, Section } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useMemo } from 'react';
import { EChart, type ChartOption } from '@/lib/EChart';
import { DateRangeFields } from '@/modules/lab/common/DateRangeFields';
import { searchDate, searchOneOf } from '@/modules/lab/common/search';
import { formatMinutes, toHours } from '@/modules/lab/tat';
import {
  MODALITIES,
  radiologyQueries,
  type Modality,
  type RadiologyTatReport,
  type RadiologyTatRow,
} from '@/modules/radiology/api';
import { modalityLabel, repeatReasonLabel } from '@/modules/radiology/labels';

interface TatSearch {
  from?: string;
  to?: string;
  modality?: Modality;
}

export const Route = createFileRoute('/_app/radiology/tat')({
  validateSearch: (s: Record<string, unknown>): TatSearch => ({
    from: searchDate(s.from),
    to: searchDate(s.to),
    modality: searchOneOf(s.modality, MODALITIES),
  }),
  component: RadiologyTatPage,
});

const useStyles = makeStyles({
  charts: {
    display: 'grid',
    gap: '16px',
    gridTemplateColumns: 'minmax(0, 2fr) minmax(0, 1fr)',
    '@media (max-width: 1100px)': { gridTemplateColumns: 'minmax(0, 1fr)' },
  },
  muted: { color: tokens.colorNeutralForeground3 },
  cell: { display: 'flex', flexDirection: 'column', gap: '2px' },
});

function TatBars({ report }: { report: RadiologyTatReport }) {
  const option = useMemo<ChartOption>(() => {
    const rows = report.modalities.filter((row) => row.studies > 0);
    return {
      grid: { left: 110, right: 24, top: 32, bottom: 32 },
      tooltip: { trigger: 'axis', axisPointer: { type: 'shadow' } },
      legend: { top: 0, data: ['Order to scan', 'Scan to report'] },
      xAxis: { type: 'value', name: 'hours' },
      yAxis: { type: 'category', inverse: true, data: rows.map((row) => modalityLabel[row.modality]) },
      series: [
        {
          type: 'bar',
          name: 'Order to scan',
          stack: 'tat',
          data: rows.map((row) => toHours(row.orderToAcquisition.medianMinutes)),
          itemStyle: { color: '#92C7E1' },
        },
        {
          type: 'bar',
          name: 'Scan to report',
          stack: 'tat',
          data: rows.map((row) => toHours(row.acquisitionToReport.medianMinutes)),
          itemStyle: { color: '#0A7CA7' },
        },
      ],
    };
  }, [report]);
  const count = report.modalities.filter((row) => row.studies > 0).length;
  return (
    <EChart
      option={option}
      height={Math.max(160, 60 + count * 40)}
      ariaLabel="Median turnaround per modality: order to scan and scan to report"
    />
  );
}

/** Imaging turnaround and reject analysis (SRS IMG-04): order → scan → report per modality, repeat rate and reasons. */
function RadiologyTatPage() {
  const styles = useStyles();
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const tat = useQuery(radiologyQueries.tat(search.from, search.to, search.modality));
  const report = tat.data;
  const today = todayIso();

  return (
    <Page>
      <PageHeader
        title="Imaging turnaround"
        subtitle={
          report
            ? report.from === report.to
              ? report.to === today
                ? 'Today'
                : formatDate(report.from)
              : `${formatDate(report.from)} to ${formatDate(report.to)}`
            : 'Order to report, per modality'
        }
      />
      <Section>
        <FilterBar>
          <DateRangeFields
            value={{ from: search.from, to: search.to }}
            onChange={(range) => void navigate({ search: (prev) => ({ ...prev, ...range }) })}
          />
          <Field label="Modality">
            <Select
              value={search.modality ?? ''}
              onChange={(_, data) =>
                void navigate({
                  search: (prev) => ({ ...prev, modality: (data.value || undefined) as Modality | undefined }),
                })
              }
            >
              <option value="">All</option>
              {MODALITIES.map((value) => (
                <option key={value} value={value}>
                  {modalityLabel[value]}
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
      ) : report.modalities.every((row) => row.studies === 0) ? (
        <Section>
          <EmptyState title="No studies in this period" description="Choose other dates or another modality." />
        </Section>
      ) : (
        <>
          <div className={styles.charts}>
            <Section title="Median turnaround" description="Order to scan, then scan to report (hours)">
              <TatBars report={report} />
            </Section>
            <Section title="Repeated images" description="Reasons, for radiographer training and equipment checks">
              {report.repeatReasons.length === 0 ? (
                <Caption1 className={styles.muted}>No repeats recorded.</Caption1>
              ) : (
                report.repeatReasons.map((stat) => (
                  <div key={stat.reason} style={{ display: 'flex', justifyContent: 'space-between', padding: '4px 0' }}>
                    <Text>{repeatReasonLabel[stat.reason]}</Text>
                    <Text weight="semibold">{stat.count}</Text>
                  </div>
                ))
              )}
            </Section>
          </div>
          <Section title="By modality" flush>
            <DataTable
              ariaLabel="Imaging turnaround by modality"
              rows={report.modalities}
              getRowId={(row) => row.modality}
              size="small"
              columns={[
                {
                  key: 'modality',
                  header: 'Modality',
                  render: (row: RadiologyTatRow) => <Text weight="semibold">{modalityLabel[row.modality]}</Text>,
                },
                {
                  key: 'studies',
                  header: 'Studies',
                  width: 90,
                  align: 'end',
                  render: (row: RadiologyTatRow) => row.studies.toLocaleString('en-IN'),
                },
                {
                  key: 'reported',
                  header: 'Reported',
                  width: 90,
                  align: 'end',
                  render: (row: RadiologyTatRow) => row.reported.toLocaleString('en-IN'),
                },
                {
                  key: 'oa',
                  header: 'Order to scan',
                  render: (row: RadiologyTatRow) => (
                    <span className={styles.cell}>
                      <Text>{formatMinutes(row.orderToAcquisition.medianMinutes)}</Text>
                      <Caption1 className={styles.muted}>
                        90%: {formatMinutes(row.orderToAcquisition.p90Minutes)}
                      </Caption1>
                    </span>
                  ),
                },
                {
                  key: 'ar',
                  header: 'Scan to report',
                  render: (row: RadiologyTatRow) => (
                    <span className={styles.cell}>
                      <Text>{formatMinutes(row.acquisitionToReport.medianMinutes)}</Text>
                      <Caption1 className={styles.muted}>
                        90%: {formatMinutes(row.acquisitionToReport.p90Minutes)}
                      </Caption1>
                    </span>
                  ),
                },
                {
                  key: 'or',
                  header: 'Order to report',
                  priority: 'low',
                  render: (row: RadiologyTatRow) => (
                    <span className={styles.cell}>
                      <Text>{formatMinutes(row.orderToReport.medianMinutes)}</Text>
                      <Caption1 className={styles.muted}>90%: {formatMinutes(row.orderToReport.p90Minutes)}</Caption1>
                    </span>
                  ),
                },
                {
                  key: 'repeats',
                  header: 'Repeat rate',
                  width: 110,
                  render: (row: RadiologyTatRow) =>
                    row.repeatRatePercent === null ? '—' : `${row.repeatRatePercent}% (${row.repeats})`,
                },
              ]}
            />
          </Section>
        </>
      )}
    </Page>
  );
}
