import { Button, Caption1, Field, Select, Text, tokens } from '@fluentui/react-components';
import {
  ChatMultipleRegular,
  DataTrendingRegular,
  EmojiSadRegular,
  PersonFeedbackRegular,
  QrCodeRegular,
  StarRegular,
  TextBulletListLtrRegular,
  TimerRegular,
} from '@fluentui/react-icons';
import { todayIso } from '@hc360/core';
import {
  DataTable,
  EmptyState,
  ErrorState,
  FilterBar,
  KpiCard,
  LoadingState,
  Page,
  PageHeader,
  Section,
} from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useMemo, useState } from 'react';
import { EChart, type ChartOption } from '@/lib/EChart';
import { toOptions, useDepartments } from '@/lib/lookups';
import { usePermission } from '@/lib/permissions';
import { feedbackQueries, type FeedbackAnalytics } from '@/modules/feedback/api';
import { StarsValue } from '@/modules/feedback/badges';
import {
  aspectLabel,
  channelLabel,
  FEEDBACK_CAPTURERS,
  FEEDBACK_PERMISSIONS as F,
  FORM_VIEWERS,
  type FeedbackChannel,
} from '@/modules/feedback/labels';
import { NpsGauge } from '@/modules/feedback/NpsGauge';
import { PeriodFields } from '@/modules/feedback/PeriodFields';
import {
  npsBand,
  npsText,
  PERIOD_PRESETS,
  resolvePeriod,
  turnaroundText,
  type PeriodPreset,
} from '@/modules/feedback/rules';
import { StaffFeedbackDialog } from '@/modules/feedback/StaffFeedbackDialog';
import { isIsoDate, monthKeyLabel } from '@/modules/hr/shared/dates';
import { Columns, FilterPad, KpiGrid } from '@/modules/hr/shared/layout';
import { RequirePermission } from '@/modules/hr/shared/NoAccess';

interface AnalyticsSearch {
  preset?: PeriodPreset;
  from?: string;
  to?: string;
  formId?: string;
  departmentId?: string;
}

export const Route = createFileRoute('/_app/feedback/')({
  validateSearch: (s: Record<string, unknown>): AnalyticsSearch => ({
    preset: PERIOD_PRESETS.includes(s.preset as PeriodPreset) ? (s.preset as PeriodPreset) : undefined,
    from: isIsoDate(s.from) ? s.from : undefined,
    to: isIsoDate(s.to) ? s.to : undefined,
    formId: typeof s.formId === 'string' && s.formId ? s.formId : undefined,
    departmentId: typeof s.departmentId === 'string' && s.departmentId ? s.departmentId : undefined,
  }),
  component: () => (
    <RequirePermission permission={F.read} what="feedback analytics">
      <AnalyticsPage />
    </RequirePermission>
  ),
});

// Canvas charts cannot read CSS variables, so chart colours are fixed (readable on light and dark themes).
const GREEN = '#2E9E5B';
const AMBER = '#C19C00';
const RED = '#D13438';
const GREY = '#8A8886';
const BLUE = '#0A7CA7';
const SENTIMENT_COLORS: Record<string, string> = { Positive: GREEN, Neutral: GREY, Negative: RED };
const CHANNEL_COLORS = [BLUE, '#5C2E91', '#CA5010', '#038387', '#8764B8'];

function ratingColor(value: number | null): string {
  if (value === null) return GREY;
  return value >= 4 ? GREEN : value >= 3 ? AMBER : RED;
}

function AnalyticsPage() {
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const departments = useDepartments();
  const forms = useQuery(feedbackQueries.forms());
  const canComplaints = usePermission(F.complaintsManage);
  const canForms = usePermission(FORM_VIEWERS);
  const canCapture = usePermission(FEEDBACK_CAPTURERS);
  const [capturing, setCapturing] = useState(false);

  const preset = search.preset ?? 'last12Months';
  const period = resolvePeriod(preset, search.from, search.to, todayIso());
  const analytics = useQuery(
    feedbackQueries.analytics({ ...period, formId: search.formId, departmentId: search.departmentId }),
  );

  const set = (patch: Partial<AnalyticsSearch>) => void navigate({ search: (prev) => ({ ...prev, ...patch }) });
  const toResponses = (
    extra: {
      lowOnly?: boolean;
      sentiment?: 'Positive' | 'Neutral' | 'Negative';
      departmentId?: string;
      channel?: FeedbackChannel;
    } = {},
  ) =>
    void navigate({
      to: '/feedback/responses',
      search: {
        preset: 'custom',
        from: period.from,
        to: period.to,
        formId: search.formId,
        departmentId: search.departmentId,
        ...extra,
      },
    });

  return (
    <Page>
      <PageHeader
        title="Feedback analytics"
        subtitle="Would patients recommend us, what they rate high and low, how they feel, and how quickly complaints are resolved."
        icon={<DataTrendingRegular />}
        actions={
          <>
            {canCapture && (
              <Button icon={<PersonFeedbackRegular />} onClick={() => setCapturing(true)}>
                Record feedback
              </Button>
            )}
            <Button icon={<TextBulletListLtrRegular />} onClick={() => toResponses()}>
              Responses
            </Button>
            {canComplaints && (
              <Button icon={<ChatMultipleRegular />} onClick={() => void navigate({ to: '/feedback/complaints' })}>
                Complaints
              </Button>
            )}
            {canForms && (
              <Button icon={<QrCodeRegular />} onClick={() => void navigate({ to: '/feedback/forms' })}>
                Forms and QR codes
              </Button>
            )}
          </>
        }
      />
      <Section flush>
        <FilterPad>
          <FilterBar>
            <PeriodFields preset={preset} from={period.from} to={period.to} onChange={(patch) => set(patch)} />
            <Field label="Form">
              <Select value={search.formId ?? ''} onChange={(_, d) => set({ formId: d.value || undefined })}>
                <option value="">All forms</option>
                {(forms.data ?? []).map((f) => (
                  <option key={f.id} value={f.id}>
                    {f.title}
                  </option>
                ))}
              </Select>
            </Field>
            <Field label="Department">
              <Select
                value={search.departmentId ?? ''}
                onChange={(_, d) => set({ departmentId: d.value || undefined })}
              >
                <option value="">Whole hospital</option>
                {toOptions(departments.data).map((o) => (
                  <option key={o.value} value={o.value}>
                    {o.label}
                  </option>
                ))}
              </Select>
            </Field>
          </FilterBar>
        </FilterPad>
      </Section>

      {analytics.error ? (
        <ErrorState error={analytics.error} onRetry={() => void analytics.refetch()} />
      ) : !analytics.data ? (
        <LoadingState label="Working out the figures…" />
      ) : (
        <Dashboard
          a={analytics.data}
          canComplaints={canComplaints}
          onResponses={toResponses}
          onComplaints={(overdueOnly) =>
            void navigate({ to: '/feedback/complaints', search: overdueOnly ? { view: 'overdue' } : {} })
          }
        />
      )}

      <StaffFeedbackDialog open={capturing} onClose={() => setCapturing(false)} />
    </Page>
  );
}

function Dashboard({
  a,
  canComplaints,
  onResponses,
  onComplaints,
}: {
  a: FeedbackAnalytics;
  canComplaints: boolean;
  onResponses: (extra?: {
    lowOnly?: boolean;
    sentiment?: 'Positive' | 'Neutral' | 'Negative';
    departmentId?: string;
    channel?: FeedbackChannel;
  }) => void;
  onComplaints: (overdueOnly: boolean) => void;
}) {
  const band = npsBand(a.nps.score);
  const lowShare = a.responses === 0 ? 0 : Math.round((a.lowScores * 100) / a.responses);

  const trend = useMemo<ChartOption>(
    () => ({
      grid: { left: 48, right: 48, top: 36, bottom: 32 },
      legend: { top: 0 },
      tooltip: { trigger: 'axis' },
      xAxis: { type: 'category', data: a.trend.map((m) => monthKeyLabel(m.month)), axisLabel: { hideOverlap: true } },
      yAxis: [
        { type: 'value', name: 'Responses', minInterval: 1 },
        { type: 'value', name: 'NPS', min: -100, max: 100, splitLine: { show: false } },
      ],
      series: [
        {
          type: 'bar',
          name: 'Responses',
          data: a.trend.map((m) => m.responses),
          itemStyle: { color: BLUE },
          barMaxWidth: 28,
        },
        {
          type: 'bar',
          name: 'Low scores',
          data: a.trend.map((m) => m.lowScores),
          itemStyle: { color: RED },
          barMaxWidth: 28,
        },
        {
          type: 'line',
          name: 'NPS',
          yAxisIndex: 1,
          data: a.trend.map((m) => m.nps),
          connectNulls: true,
          symbolSize: 8,
          lineStyle: { color: GREEN, width: 2 },
          itemStyle: { color: GREEN },
          markLine: {
            silent: true,
            symbol: 'none',
            lineStyle: { type: 'dashed', color: GREY },
            label: { formatter: 'NPS 0' },
            data: [{ yAxis: 0 }],
          },
        },
      ],
    }),
    [a.trend],
  );

  const aspects = useMemo<ChartOption>(() => {
    const rows = [...a.aspects].reverse();
    return {
      grid: { left: 150, right: 40, top: 8, bottom: 28 },
      tooltip: {
        trigger: 'axis',
        valueFormatter: (value) => `${typeof value === 'number' ? value.toFixed(2) : String(value)} / 5`,
      },
      xAxis: { type: 'value', min: 0, max: 5, interval: 1 },
      yAxis: { type: 'category', data: rows.map((r) => aspectLabel[r.aspect]) },
      series: [
        {
          type: 'bar',
          name: 'Average rating',
          barMaxWidth: 22,
          data: rows.map((r) => ({ value: r.average ?? 0, itemStyle: { color: ratingColor(r.average) } })),
          label: {
            show: true,
            position: 'right',
            formatter: (p) => (typeof p.value === 'number' ? p.value.toFixed(1) : ''),
          },
        },
      ],
    };
  }, [a.aspects]);

  const sentiment = useMemo<ChartOption>(
    () => ({
      tooltip: { trigger: 'item', formatter: '{b}: {c} ({d}%)' },
      legend: { bottom: 0 },
      series: [
        {
          type: 'pie',
          name: 'Sentiment',
          radius: ['45%', '72%'],
          top: -10,
          label: { formatter: '{d}%' },
          data: a.bySentiment.map((s) => ({
            name: s.label,
            value: s.count,
            itemStyle: { color: SENTIMENT_COLORS[s.label] ?? GREY },
          })),
        },
      ],
    }),
    [a.bySentiment],
  );

  const channels = useMemo<ChartOption>(
    () => ({
      tooltip: { trigger: 'item', formatter: '{b}: {c} ({d}%)' },
      legend: { bottom: 0 },
      series: [
        {
          type: 'pie',
          name: 'Channel',
          radius: ['45%', '72%'],
          top: -10,
          label: { formatter: '{d}%' },
          data: a.byChannel.map((c, index) => ({
            name: channelLabel[c.label as FeedbackChannel] ?? c.label,
            value: c.count,
            itemStyle: { color: CHANNEL_COLORS[index % CHANNEL_COLORS.length] },
          })),
        },
      ],
    }),
    [a.byChannel],
  );

  if (a.responses === 0 && a.complaints.raised === 0) {
    return (
      <Section>
        <EmptyState
          title="No feedback in this period"
          description="Put up the QR posters, set up a kiosk, or record feedback during follow-up calls. Answers appear here straight away."
        />
      </Section>
    );
  }

  return (
    <>
      <KpiGrid>
        <KpiCard
          label="Responses"
          value={a.responses.toLocaleString('en-IN')}
          hint={`${a.nps.responses.toLocaleString('en-IN')} answered the NPS question`}
          icon={<PersonFeedbackRegular />}
          onClick={() => onResponses()}
        />
        <KpiCard
          label="Net promoter score"
          value={npsText(a.nps.score)}
          hint={band.label}
          icon={<DataTrendingRegular />}
        />
        <KpiCard
          label="Average rating"
          value={a.averageRating === null ? '—' : `${a.averageRating.toFixed(2)} / 5`}
          hint="Each response counts once"
          icon={<StarRegular />}
        />
        <KpiCard
          label="Low scores"
          value={a.lowScores.toLocaleString('en-IN')}
          hint={`${lowShare}% of responses · each raised a complaint`}
          icon={<EmojiSadRegular />}
          onClick={() => onResponses({ lowOnly: true })}
        />
        {canComplaints && (
          <KpiCard
            label="Complaints still open"
            value={a.complaints.open.toLocaleString('en-IN')}
            hint={`Of ${a.complaints.raised} raised in the period${a.complaints.overdue > 0 ? ` · ${a.complaints.overdue} past the deadline` : ''}`}
            icon={<ChatMultipleRegular />}
            onClick={() => onComplaints(a.complaints.overdue > 0)}
          />
        )}
        {canComplaints && (
          <KpiCard
            label="Resolved on time"
            value={a.complaints.resolvedWithinSlaPercent === null ? '—' : `${a.complaints.resolvedWithinSlaPercent}%`}
            hint={`Average turnaround ${turnaroundText(a.complaints.averageTurnaroundHours)}`}
            icon={<TimerRegular />}
          />
        )}
      </KpiGrid>

      <Columns>
        <Section
          title="Net promoter score"
          description="Promoters (9–10) minus detractors (0–6), as a percentage of answers"
        >
          <NpsGauge nps={a.nps} />
        </Section>
        <Section title="Month by month" description="Responses, low scores and the NPS line">
          {a.trend.length === 0 ? (
            <Caption1>No months in this period.</Caption1>
          ) : (
            <EChart option={trend} height={280} ariaLabel="Monthly responses, low scores and net promoter score" />
          )}
        </Section>
      </Columns>

      <Columns>
        <Section title="Ratings by aspect" description="Average stars; below 3 needs attention">
          {a.aspects.length === 0 ? (
            <Caption1>No aspects were rated in this period.</Caption1>
          ) : (
            <EChart
              option={aspects}
              height={Math.max(160, a.aspects.length * 38 + 40)}
              ariaLabel="Average rating for each aspect"
            />
          )}
        </Section>
        <Section title="How patients feel" description="Worked out from comments, ratings and the NPS answer">
          <EChart option={sentiment} height={240} ariaLabel="Responses by sentiment" />
          <div style={{ display: 'flex', gap: 8, justifyContent: 'center', flexWrap: 'wrap' }}>
            {a.bySentiment.map((s) => (
              <Button
                key={s.label}
                size="small"
                appearance="subtle"
                onClick={() => onResponses({ sentiment: s.label as 'Positive' | 'Neutral' | 'Negative' })}
              >
                See {s.label.toLowerCase()} ({s.count})
              </Button>
            ))}
          </div>
        </Section>
      </Columns>

      <Columns>
        <Section title="Departments" description="Select a department to read its responses">
          <DataTable
            ariaLabel="Feedback by department"
            rows={a.departments}
            getRowId={(d) => d.departmentId ?? 'none'}
            onRowClick={(d) => (d.departmentId ? onResponses({ departmentId: d.departmentId }) : undefined)}
            columns={[
              { key: 'department', header: 'Department', render: (d) => <Text weight="semibold">{d.department}</Text> },
              {
                key: 'responses',
                header: 'Responses',
                align: 'end',
                render: (d) => d.responses.toLocaleString('en-IN'),
              },
              { key: 'rating', header: 'Average', render: (d) => <StarsValue value={d.averageRating} /> },
              {
                key: 'nps',
                header: 'NPS',
                align: 'end',
                render: (d) => (
                  <Text style={{ color: d.nps !== null && d.nps < 0 ? tokens.colorPaletteRedForeground1 : undefined }}>
                    {npsText(d.nps)}
                  </Text>
                ),
              },
            ]}
            emptyTitle="No responses"
            emptyDescription="Department figures appear once feedback arrives."
          />
        </Section>
        <Section title="How feedback arrives" description="QR posters, kiosks, SMS links, the portal and staff">
          {a.byChannel.length === 0 ? (
            <Caption1>No responses in this period.</Caption1>
          ) : (
            <EChart option={channels} height={240} ariaLabel="Responses by channel" />
          )}
        </Section>
      </Columns>
    </>
  );
}
