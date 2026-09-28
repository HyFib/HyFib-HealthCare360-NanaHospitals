import { useMemo } from 'react';
import { EChart, type ChartOption } from '@/lib/EChart';
import type { LabTatRow, RejectionStat } from './api';
import { rejectionReasonLabel } from './labels';
import { busiestTests, toHours } from './tat';

/**
 * Order-to-release turnaround of the busiest tests: median and 90th percentile bars against each test's target
 * (diamond). A bar past its diamond is a test the lab is releasing late.
 */
export function TatChart({ rows, count = 10, height }: { rows: readonly LabTatRow[]; count?: number; height?: number }) {
  const option = useMemo<ChartOption>(() => {
    const top = busiestTests(rows, count);
    return {
      grid: { left: 150, right: 24, top: 32, bottom: 32 },
      tooltip: { trigger: 'axis', axisPointer: { type: 'shadow' } },
      legend: { top: 0, data: ['Median', '90th percentile', 'Target'] },
      xAxis: { type: 'value', name: 'hours', nameLocation: 'end' },
      yAxis: { type: 'category', inverse: true, data: top.map((row) => row.testName), axisLabel: { width: 140, overflow: 'truncate' } },
      series: [
        { type: 'bar', name: 'Median', data: top.map((row) => toHours(row.orderToRelease.medianMinutes)), itemStyle: { color: '#0A7CA7' } },
        { type: 'bar', name: '90th percentile', data: top.map((row) => toHours(row.orderToRelease.p90Minutes)), itemStyle: { color: '#92C7E1' } },
        {
          type: 'line',
          name: 'Target',
          data: top.map((row) => row.targetHours),
          symbol: 'diamond',
          symbolSize: 12,
          lineStyle: { width: 0 },
          itemStyle: { color: '#D13438' },
        },
      ],
    };
  }, [rows, count]);

  const shown = Math.min(rows.filter((row) => row.released > 0).length, count);
  return <EChart option={option} height={height ?? Math.max(180, 60 + shown * 34)} ariaLabel="Turnaround from order to release per test, median and 90th percentile against target" />;
}

/** Rejected samples by reason (pre-analytical errors to train phlebotomists on). */
export function RejectionChart({ stats, height = 220 }: { stats: readonly RejectionStat[]; height?: number }) {
  const option = useMemo<ChartOption>(
    () => ({
      grid: { left: 150, right: 24, top: 8, bottom: 24 },
      tooltip: { trigger: 'axis', axisPointer: { type: 'shadow' } },
      xAxis: { type: 'value', minInterval: 1 },
      yAxis: { type: 'category', inverse: true, data: stats.map((stat) => rejectionReasonLabel[stat.reason]) },
      series: [{ type: 'bar', name: 'Rejected samples', data: stats.map((stat) => stat.count), itemStyle: { color: '#C19C00' } }],
    }),
    [stats],
  );
  return <EChart option={option} height={height} ariaLabel="Rejected samples by reason" />;
}
