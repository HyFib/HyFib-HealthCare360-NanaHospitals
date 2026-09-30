import { useMemo } from 'react';
import { EChart, type ChartOption } from '@/lib/EChart';
import type { IndicatorValueDto } from './api';
import type { IndicatorDirection, IndicatorUnit } from './labels';
import { formatIndicatorValue } from './rules';

// Canvas charts cannot read CSS variables, so the status colours are fixed (readable on light and dark themes).
const MET = '#2E9E5B';
const MISSED = '#D13438';
const LINE = '#0A7CA7';
const TARGET = '#8A8886';

/** Indicator values over time with the target as a dashed line; points are green when the target is met. */
export function TrendChart({
  points,
  unit,
  target,
  direction,
  name,
  height = 280,
}: {
  points: IndicatorValueDto[];
  unit: IndicatorUnit;
  target: number | null;
  direction: IndicatorDirection;
  name: string;
  height?: number;
}) {
  const option = useMemo<ChartOption>(() => {
    const sorted = [...points].sort((a, b) => a.periodStart.localeCompare(b.periodStart));
    return {
      grid: { left: 56, right: 24, top: 28, bottom: 36 },
      tooltip: {
        trigger: 'axis',
        valueFormatter: (value) => formatIndicatorValue(typeof value === 'number' ? value : Number(value), unit),
      },
      xAxis: { type: 'category', data: sorted.map((p) => p.periodLabel), axisLabel: { hideOverlap: true } },
      yAxis: {
        type: 'value',
        scale: true,
        axisLabel: { formatter: (value: number) => formatIndicatorValue(value, unit) },
      },
      series: [
        {
          type: 'line',
          name,
          smooth: false,
          symbolSize: 9,
          lineStyle: { color: LINE, width: 2 },
          itemStyle: { color: LINE },
          data: sorted.map((p) => ({
            value: p.value,
            itemStyle: { color: p.meetsTarget === null ? LINE : p.meetsTarget ? MET : MISSED },
          })),
          markLine:
            target === null
              ? undefined
              : {
                  silent: true,
                  symbol: 'none',
                  lineStyle: { type: 'dashed', color: TARGET },
                  label: {
                    formatter: `Target ${formatIndicatorValue(target, unit)} (${direction === 'HigherIsBetter' ? 'higher is better' : 'lower is better'})`,
                    position: 'insideEndTop',
                  },
                  data: [{ yAxis: target }],
                },
        },
      ],
    };
  }, [points, unit, target, direction, name]);

  return <EChart option={option} height={height} ariaLabel={`${name}: trend of values against the target`} />;
}
