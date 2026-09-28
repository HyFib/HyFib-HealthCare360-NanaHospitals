import { HOSPITAL_TIME_ZONE } from '@hc360/core';
import { useMemo } from 'react';
import { EChart, type ChartOption } from '@/lib/EChart';
import { TREND_METRICS, trendSeries, type TrendMetric, type VitalsReading } from './vitals';

const LABEL = new Intl.DateTimeFormat('en-IN', { timeZone: HOSPITAL_TIME_ZONE, day: '2-digit', month: 'short', hour: 'numeric', minute: '2-digit', hour12: true });

/** Line chart of one vital sign over time, with the usual adult range as dashed guide lines. Loaded lazily (ECharts). */
export default function VitalsTrendChart({ readings, metric, height = 260 }: { readings: readonly VitalsReading[]; metric: TrendMetric; height?: number }) {
  const info = TREND_METRICS.find((m) => m.id === metric) ?? TREND_METRICS[0]!;
  const option = useMemo<ChartOption>(() => {
    const trend = trendSeries(readings, metric);
    return {
      tooltip: { trigger: 'axis' },
      legend: { show: trend.series.length > 1, top: 0 },
      grid: { left: 8, right: 16, top: trend.series.length > 1 ? 32 : 16, bottom: 8, containLabel: true },
      xAxis: { type: 'category', boundaryGap: false, data: trend.at.map((at) => LABEL.format(Date.parse(at))) },
      yAxis: { type: 'value', name: info.unit, scale: true },
      series: trend.series.map((s, index) => ({
        type: 'line' as const,
        name: s.name,
        data: s.values,
        connectNulls: true,
        symbolSize: 7,
        markLine:
          index === 0 && info.reference
            ? {
                silent: true,
                symbol: 'none',
                lineStyle: { type: 'dashed', opacity: 0.6 },
                label: { formatter: '{c}' },
                data: [{ yAxis: info.reference.low }, { yAxis: info.reference.high }],
              }
            : undefined,
      })),
    };
  }, [readings, metric, info]);

  return <EChart option={option} height={height} ariaLabel={`${info.label} trend chart. The table below lists the same values.`} />;
}
