import { escapeHtml, formatDateTime } from '@hc360/core';
import { useMemo } from 'react';
import { EChart, type ChartOption } from '@/lib/EChart';
import type { LeveyJenningsChartDto } from './api';
import { qcOutcomeLabel } from './labels';
import { chartBounds, formatZ, outcomeColor, sdLines, sdLineStyle } from './qc';

/**
 * Levey-Jennings chart (SRS LIS-06): control values over time against the lot's target mean with ±1 SD (dotted),
 * ±2 SD (dashed) and ±3 SD (solid) limits. Warning runs are amber, rejected runs red diamonds; the tooltip lists the
 * Westgard rules that fired. The runs table under the chart carries the same data for screen readers.
 */
export function LeveyJenningsChart({ chart, height = 320 }: { chart: LeveyJenningsChartDto; height?: number }) {
  const option = useMemo<ChartOption>(() => {
    const lines = sdLines(chart.mean, chart.sd);
    const bounds = chartBounds(
      chart.mean,
      chart.sd,
      chart.points.map((point) => point.value),
    );
    const unit = chart.unit ? ` ${chart.unit}` : '';
    return {
      grid: { left: 60, right: 64, top: 16, bottom: 36 },
      tooltip: {
        trigger: 'item',
        formatter: (params: unknown) => {
          const index = (params as { dataIndex?: number }).dataIndex ?? -1;
          const point = chart.points[index];
          if (!point) {
            return '';
          }
          const rules = point.violations.length ? `<br/>Rules: ${escapeHtml(point.violations.join(', '))}` : '';
          return `${escapeHtml(formatDateTime(point.runAt))}<br/><b>${escapeHtml(point.value)}${escapeHtml(unit)}</b> (${escapeHtml(formatZ(point.zScore))})<br/>${escapeHtml(qcOutcomeLabel[point.outcome])}${rules}`;
        },
      },
      xAxis: { type: 'time' },
      yAxis: { type: 'value', min: bounds.min, max: bounds.max, scale: true, splitLine: { show: false } },
      series: [
        {
          type: 'line',
          name: `Level ${chart.level}`,
          data: chart.points.map((point) => ({
            value: [point.runAt, point.value],
            symbol: point.outcome === 'Rejected' ? 'diamond' : 'circle',
            symbolSize: point.outcome === 'Accepted' ? 7 : 11,
            itemStyle: { color: outcomeColor(point.outcome) },
          })),
          lineStyle: { color: '#8A8886', width: 1 },
          markLine: {
            silent: true,
            symbol: 'none',
            label: { position: 'end', formatter: '{b}' },
            data: lines.map((line) => ({ yAxis: line.value, name: line.label, lineStyle: sdLineStyle(line.sds) })),
          },
        },
      ],
    };
  }, [chart]);

  return (
    <EChart
      option={option}
      height={height}
      ariaLabel={`Levey-Jennings chart for ${chart.testName}, level ${chart.level}, lot ${chart.lotNumber}: ${chart.points.length} runs against mean ${chart.mean} and SD ${chart.sd}`}
    />
  );
}
