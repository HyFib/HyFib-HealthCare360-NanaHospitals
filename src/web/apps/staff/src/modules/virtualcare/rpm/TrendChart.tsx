import { MarkAreaComponent } from 'echarts/components';
import { use as registerChartParts } from 'echarts/core';
import { useMemo } from 'react';
import { EChart, type ChartOption } from '@/lib/EChart';
import { addDays, dateRange, dayParts, type IsoDate } from '@/modules/scheduling/dates';
import type { ReadingSeverity, RpmAdherenceDto, RpmTrend } from './api';
import { ecgRhythmLabel, readingTypeInfo } from './labels';
import { axisRange, thresholdBands, type Limits } from './thresholds';

// The shared chart wrapper registers line/bar/pie and mark lines; limit bands also need mark areas (ECharts' own
// registration function, not a React hook).
registerChartParts([MarkAreaComponent]);

/** Canvas colours (ECharts cannot read CSS variables); translucent so they work on light and dark themes. */
const BAND_COLOUR: Record<ReadingSeverity, string> = {
  Normal: 'rgba(16, 124, 16, 0.10)',
  Warning: 'rgba(234, 163, 0, 0.16)',
  Critical: 'rgba(209, 52, 56, 0.16)',
};
const POINT_COLOUR: Record<ReadingSeverity, string> = { Normal: '#0A7CA7', Warning: '#C77700', Critical: '#C50F1F' };
const LINE_COLOUR = '#0A7CA7';

/** One measurement over time, with the patient's limits drawn as coloured bands (RPM-04). */
export function TrendChart({ trend, height = 220 }: { trend: RpmTrend; height?: number }) {
  const option = useMemo<ChartOption>(() => {
    const info = readingTypeInfo[trend.type];
    const ecg = trend.type === 'EcgRhythm';
    const limits: Limits | null = trend.threshold
      ? { min: trend.threshold.min, max: trend.threshold.max, criticalMin: trend.threshold.criticalMin, criticalMax: trend.threshold.criticalMax }
      : null;
    const axis = ecg ? { min: -0.5, max: 2.5 } : axisRange(trend.points.map((p) => p.value), limits, info.plausible);
    const bands = limits ? thresholdBands(limits, axis) : [];

    return {
      grid: { left: 52, right: 16, top: 20, bottom: 32 },
      tooltip: {
        trigger: 'axis',
        valueFormatter: (value) => (ecg ? (ecgRhythmLabel[Math.round(Number(value))] ?? String(value)) : `${String(value)} ${trend.unit}`),
      },
      xAxis: { type: 'time' },
      yAxis: {
        type: 'value',
        min: axis.min,
        max: axis.max,
        name: ecg ? undefined : trend.unit,
        nameTextStyle: { align: 'right' },
        interval: ecg ? 1 : undefined,
        axisLabel: ecg ? { formatter: (value: number) => (value === 0 ? 'Normal' : value === 1 ? 'Review' : value === 2 ? 'AF' : '') } : undefined,
        splitLine: { show: !ecg },
      },
      series: [
        {
          type: 'line',
          name: trend.label,
          step: ecg ? 'end' : undefined,
          showSymbol: true,
          symbolSize: 7,
          lineStyle: { width: 2, color: LINE_COLOUR },
          itemStyle: { color: LINE_COLOUR },
          data: trend.points.map((point) => ({
            value: [point.measuredAt, point.value],
            itemStyle: { color: POINT_COLOUR[point.severity] },
            symbolSize: point.severity === 'Normal' ? 6 : 9,
          })),
          markArea: {
            silent: true,
            data: bands.map((band) => [{ yAxis: band.from, itemStyle: { color: BAND_COLOUR[band.severity] } }, { yAxis: band.to }]),
          },
        },
      ],
    };
  }, [trend]);

  const outOfRange = trend.points.filter((p) => p.severity !== 'Normal').length;
  return (
    <EChart
      option={option}
      height={height}
      ariaLabel={`${trend.label}: ${trend.points.length} readings, ${outOfRange} outside the limits. Green band is the normal range, amber out of range, red critical.`}
    />
  );
}

/** Readings per day for the adherence window; days short of the expected count are amber, missing days show a gap. */
export function AdherenceChart({ adherence, today, height = 160 }: { adherence: RpmAdherenceDto; today: IsoDate; height?: number }) {
  const option = useMemo<ChartOption>(() => {
    const days = dateRange(addDays(today, -(adherence.windowDays - 1)), adherence.windowDays);
    const counts = new Map(adherence.readingsPerDay.map((d) => [d.date, d.readings]));
    return {
      grid: { left: 36, right: 12, top: 16, bottom: 28 },
      tooltip: { trigger: 'axis' },
      xAxis: {
        type: 'category',
        data: days.map((d) => {
          const parts = dayParts(d);
          return `${parts.day} ${parts.month}`;
        }),
        axisLabel: { interval: Math.max(0, Math.floor(days.length / 8) - 1) },
      },
      yAxis: { type: 'value', minInterval: 1 },
      series: [
        {
          type: 'bar',
          name: 'Readings',
          data: days.map((d) => {
            const count = counts.get(d) ?? 0;
            return { value: count, itemStyle: { color: count >= adherence.expectedReadingsPerDay ? '#107C10' : '#C77700' } };
          }),
          markLine: {
            silent: true,
            symbol: 'none',
            lineStyle: { type: 'dashed', color: '#616161' },
            data: [{ yAxis: adherence.expectedReadingsPerDay, name: 'Expected' }],
          },
        },
      ],
    };
  }, [adherence, today]);

  return (
    <EChart
      option={option}
      height={height}
      ariaLabel={`Readings on ${adherence.daysWithReadings} of the last ${adherence.windowDays} days (${adherence.percent}%). Expected ${adherence.expectedReadingsPerDay} a day.`}
    />
  );
}
