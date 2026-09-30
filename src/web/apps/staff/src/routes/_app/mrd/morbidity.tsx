import { Button, Field, Input, makeStyles, MessageBar, MessageBarBody, Select, Text } from '@fluentui/react-components';
import { ArrowDownloadRegular, DataBarHorizontalRegular, PeopleRegular, PersonRegular } from '@fluentui/react-icons';
import { formatDateOnly, formatNumber, todayIso } from '@hc360/core';
import { DataTable, EmptyState, FilterBar, KpiCard, LoadingState, Page, PageHeader, Section } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useMemo } from 'react';
import { EChart, type ChartOption } from '@/lib/EChart';
import { addDaysIso } from '@/modules/emr/encounterForm';
import { mrdQueries, type MorbidityGroupBy, type MorbiditySource } from '@/modules/mrd/api';
import {
  chartLabel,
  morbidityCsv,
  periodProblem,
  topRows,
  totals,
  type MorbidityRowLike,
} from '@/modules/mrd/morbidity';

const DATE = /^\d{4}-\d{2}-\d{2}$/;

interface MorbiditySearch {
  from?: string;
  to?: string;
  groupBy?: MorbidityGroupBy;
  source?: MorbiditySource;
}

export const Route = createFileRoute('/_app/mrd/morbidity')({
  validateSearch: (s: Record<string, unknown>): MorbiditySearch => ({
    from: typeof s.from === 'string' && DATE.test(s.from) ? s.from : undefined,
    to: typeof s.to === 'string' && DATE.test(s.to) ? s.to : undefined,
    groupBy: s.groupBy === 'block' || s.groupBy === 'chapter' ? s.groupBy : undefined,
    source: s.source === 'coded' ? 'coded' : undefined,
  }),
  component: MorbidityPage,
});

const useStyles = makeStyles({
  kpis: { display: 'grid', gap: '12px', gridTemplateColumns: 'repeat(auto-fill, minmax(170px, 1fr))' },
});

const groupLabel: Record<MorbidityGroupBy, string> = {
  code: 'ICD-10 code',
  block: 'ICD-10 block',
  chapter: 'ICD-10 chapter',
};

function downloadCsv(name: string, content: string) {
  // UTF-8 byte-order mark so Excel opens Indian-language names correctly.
  const url = URL.createObjectURL(new Blob(['﻿', content], { type: 'text/csv;charset=utf-8' }));
  const link = document.createElement('a');
  link.href = url;
  link.download = name;
  document.body.appendChild(link);
  link.click();
  link.remove();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}

/** Disease / morbidity index (MRD-06) from doctors' diagnoses or verified MRD coding, by code, block or chapter. */
function MorbidityPage() {
  const styles = useStyles();
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const today = todayIso();
  const to = search.to ?? today;
  const from = search.from ?? addDaysIso(to, -29);
  const groupBy = search.groupBy ?? 'code';
  const source = search.source ?? 'clinical';
  const problem = periodProblem(from, to);
  const report = useQuery({ ...mrdQueries.morbidity({ from, to, groupBy, source }), enabled: problem === null });
  const rows = useMemo(() => report.data?.rows ?? [], [report.data]);
  const sum = totals(rows);

  const option = useMemo<ChartOption>(() => {
    const top = topRows<MorbidityRowLike>(rows, 15).toReversed();
    return {
      tooltip: { trigger: 'axis', axisPointer: { type: 'shadow' } },
      grid: { left: 8, right: 24, top: 8, bottom: 8, containLabel: true },
      xAxis: { type: 'value', name: 'Episodes', minInterval: 1 },
      yAxis: { type: 'category', data: top.map((r) => chartLabel(r)), axisLabel: { width: 280, overflow: 'truncate' } },
      series: [{ type: 'bar', name: 'Episodes', data: top.map((r) => r.episodes), barMaxWidth: 22 }],
    };
  }, [rows]);

  const set = (patch: Partial<MorbiditySearch>) =>
    void navigate({ search: (prev) => ({ ...prev, ...patch }), replace: true });

  return (
    <Page>
      <PageHeader
        title="Morbidity report"
        subtitle={`${formatDateOnly(from)} to ${formatDateOnly(to)} · ${source === 'coded' ? 'verified MRD coding' : "doctors' diagnoses"}`}
        actions={
          <Button
            icon={<ArrowDownloadRegular />}
            disabled={rows.length === 0}
            onClick={() =>
              downloadCsv(`morbidity-${groupBy}-${from}-to-${to}.csv`, morbidityCsv(rows, groupLabel[groupBy]))
            }
          >
            Download CSV
          </Button>
        }
      />
      <Section>
        <FilterBar>
          <Field label="From">
            <Input type="date" value={from} max={to} onChange={(_, d) => set({ from: d.value || undefined })} />
          </Field>
          <Field label="To">
            <Input
              type="date"
              value={to}
              min={from}
              max={today}
              onChange={(_, d) => set({ to: d.value || undefined })}
            />
          </Field>
          <Field label="Group by">
            <Select
              value={groupBy}
              onChange={(_, d) => set({ groupBy: d.value === 'code' ? undefined : (d.value as MorbidityGroupBy) })}
            >
              <option value="code">ICD-10 code</option>
              <option value="block">Block (disease group)</option>
              <option value="chapter">Chapter</option>
            </Select>
          </Field>
          <Field label="Based on">
            <Select value={source} onChange={(_, d) => set({ source: d.value === 'coded' ? 'coded' : undefined })}>
              <option value="clinical">Doctors' diagnoses (all episodes)</option>
              <option value="coded">Verified MRD coding</option>
            </Select>
          </Field>
        </FilterBar>
      </Section>

      {problem ? (
        <MessageBar intent="warning">
          <MessageBarBody>{problem}</MessageBarBody>
        </MessageBar>
      ) : report.isLoading ? (
        <LoadingState label="Building the report…" />
      ) : rows.length === 0 ? (
        <EmptyState
          title="No episodes in this period"
          description="Choose a longer period, or switch the source if coding has not been verified yet."
        />
      ) : (
        <>
          <div className={styles.kpis}>
            <KpiCard
              label="Episodes"
              value={formatNumber(report.data?.totalEpisodes ?? sum.episodes)}
              icon={<DataBarHorizontalRegular />}
            />
            <KpiCard label={`${groupLabel[groupBy]}s`} value={formatNumber(rows.length)} />
            <KpiCard
              label="Male / female"
              value={`${formatNumber(sum.male)} / ${formatNumber(sum.female)}`}
              icon={<PeopleRegular />}
            />
            <KpiCard label="Children (0–14)" value={formatNumber(sum.children)} icon={<PersonRegular />} />
            <KpiCard label="Elderly (65+)" value={formatNumber(sum.elderly)} icon={<PersonRegular />} />
          </div>
          <Section title="Most frequent" description="Top 15 by number of episodes">
            <EChart
              option={option}
              height={Math.max(260, Math.min(15, rows.length) * 30)}
              ariaLabel="Bar chart of the most frequent diagnoses. The table below lists every group."
            />
          </Section>
          <Section title={`All ${groupLabel[groupBy].toLowerCase()}s`} flush>
            <DataTable
              ariaLabel="Morbidity report"
              rows={rows}
              getRowId={(r) => r.key}
              size="small"
              columns={[
                {
                  key: 'key',
                  header: groupLabel[groupBy],
                  width: 120,
                  render: (r) => <Text font="monospace">{r.key}</Text>,
                },
                { key: 'title', header: 'Title', render: (r) => r.title },
                {
                  key: 'episodes',
                  header: 'Episodes',
                  width: 90,
                  align: 'end',
                  render: (r) => formatNumber(r.episodes),
                },
                {
                  key: 'patients',
                  header: 'Patients',
                  width: 90,
                  align: 'end',
                  render: (r) => formatNumber(r.patients),
                },
                { key: 'mf', header: 'M / F', width: 90, priority: 'low', render: (r) => `${r.male} / ${r.female}` },
                { key: 'child', header: '0–14', width: 70, priority: 'low', align: 'end', render: (r) => r.age0To14 },
                { key: 'adult', header: '15–44', width: 70, priority: 'low', align: 'end', render: (r) => r.age15To44 },
                {
                  key: 'middle',
                  header: '45–64',
                  width: 70,
                  priority: 'low',
                  align: 'end',
                  render: (r) => r.age45To64,
                },
                { key: 'old', header: '65+', width: 70, priority: 'low', align: 'end', render: (r) => r.age65Plus },
              ]}
            />
          </Section>
        </>
      )}
    </Page>
  );
}
