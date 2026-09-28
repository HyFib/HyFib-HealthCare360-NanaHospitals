import { tokens } from '@fluentui/react-components';
import { useResolvedTheme } from '@hc360/ui';
import { BarChart, LineChart, PieChart } from 'echarts/charts';
import { GridComponent, LegendComponent, MarkAreaComponent, MarkLineComponent, TooltipComponent } from 'echarts/components';
import { init, use as registerChartParts, type ComposeOption, type ECharts } from 'echarts/core';
import type { BarSeriesOption, LineSeriesOption, PieSeriesOption } from 'echarts/charts';
import type { GridComponentOption, LegendComponentOption, TooltipComponentOption } from 'echarts/components';
import { CanvasRenderer } from 'echarts/renderers';
import { useEffect, useEffectEvent, useRef } from 'react';

registerChartParts([LineChart, BarChart, PieChart, GridComponent, TooltipComponent, LegendComponent, MarkLineComponent, MarkAreaComponent, CanvasRenderer]);

export type ChartOption = ComposeOption<
  BarSeriesOption | LineSeriesOption | PieSeriesOption | GridComponentOption | TooltipComponentOption | LegendComponentOption
>;

/** The data point a user clicked (for drill-down from a chart to the rows behind it). */
export interface ChartPoint {
  name: string;
  seriesName?: string;
  dataIndex: number;
  value: unknown;
}

/**
 * Thin ECharts wrapper (tree-shaken: line, bar, pie, mark lines and shaded mark areas) that follows the app theme and
 * resizes with its container. Pass `onPointClick` to make data points clickable.
 */
export function EChart({
  option,
  height = 260,
  ariaLabel,
  onPointClick,
}: {
  option: ChartOption;
  height?: number;
  ariaLabel: string;
  onPointClick?: (point: ChartPoint) => void;
}) {
  const ref = useRef<HTMLDivElement>(null);
  const chart = useRef<ECharts | null>(null);
  const { isDark } = useResolvedTheme();
  const pointClicked = useEffectEvent((point: ChartPoint) => onPointClick?.(point));

  useEffect(() => {
    if (!ref.current) {
      return;
    }
    const instance = init(ref.current, isDark ? 'dark' : undefined, { renderer: 'canvas' });
    chart.current = instance;
    instance.on('click', (params) => {
      const p = params as { name?: string; seriesName?: string; dataIndex?: number; value?: unknown };
      pointClicked({ name: p.name ?? '', seriesName: p.seriesName, dataIndex: p.dataIndex ?? -1, value: p.value });
    });
    const observer = new ResizeObserver(() => instance.resize());
    observer.observe(ref.current);
    return () => {
      observer.disconnect();
      instance.dispose();
      chart.current = null;
    };
  }, [isDark]);

  useEffect(() => {
    chart.current?.setOption({ backgroundColor: 'transparent', textStyle: { fontFamily: 'Segoe UI, sans-serif' }, ...option }, true);
  }, [option, isDark]);

  return <div ref={ref} role="img" aria-label={ariaLabel} style={{ width: '100%', height, color: tokens.colorNeutralForeground1 }} />;
}
