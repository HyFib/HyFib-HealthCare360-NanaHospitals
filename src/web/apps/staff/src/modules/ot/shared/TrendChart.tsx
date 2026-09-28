import { useMemo } from 'react';
import { EChart, type ChartOption } from '@/lib/EChart';

export interface TrendPoint {
  at: string;
  value: number | null | undefined;
}

export interface TrendSeries {
  name: string;
  points: TrendPoint[];
  dashed?: boolean;
}

/**
 * Time-series chart for charted observations (intra-operative vitals, intra-dialytic readings, pain scores).
 * Always pair it with the table of values: the chart is for trends, the table is the record.
 */
export function TrendChart({
  series,
  ariaLabel,
  height = 240,
  yName,
  yMin,
  yMax,
}: {
  series: TrendSeries[];
  ariaLabel: string;
  height?: number;
  yName?: string;
  yMin?: number;
  yMax?: number;
}) {
  const option = useMemo<ChartOption>(
    () => ({
      grid: { left: 48, right: 16, top: 40, bottom: 32 },
      tooltip: { trigger: 'axis' },
      legend: { top: 0, type: 'scroll' },
      xAxis: { type: 'time' },
      yAxis: { type: 'value', name: yName, min: yMin, max: yMax, scale: yMin === undefined },
      series: series.map((s) => ({
        type: 'line',
        name: s.name,
        showSymbol: true,
        symbolSize: 6,
        connectNulls: true,
        lineStyle: s.dashed ? { type: 'dashed' } : undefined,
        data: s.points.filter((p) => p.value !== null && p.value !== undefined).map((p) => [p.at, p.value as number]),
      })),
    }),
    [series, yName, yMin, yMax],
  );
  return <EChart option={option} height={height} ariaLabel={ariaLabel} />;
}
