import { tokens } from '@fluentui/react-components';
import { useResolvedTheme } from '@hc360/ui';
import { BarChart, LineChart, PieChart } from 'echarts/charts';
import { GridComponent, LegendComponent, MarkLineComponent, TooltipComponent } from 'echarts/components';
import { init, use as registerChartParts, type ComposeOption, type ECharts } from 'echarts/core';
import type { BarSeriesOption, LineSeriesOption, PieSeriesOption } from 'echarts/charts';
import type { GridComponentOption, LegendComponentOption, TooltipComponentOption } from 'echarts/components';
import { CanvasRenderer } from 'echarts/renderers';
import { useEffect, useRef } from 'react';

registerChartParts([LineChart, BarChart, PieChart, GridComponent, TooltipComponent, LegendComponent, MarkLineComponent, CanvasRenderer]);

export type ChartOption = ComposeOption<
  BarSeriesOption | LineSeriesOption | PieSeriesOption | GridComponentOption | TooltipComponentOption | LegendComponentOption
>;

/** Thin ECharts wrapper (tree-shaken: line, bar, pie) that follows the app theme and resizes with its container. */
export function EChart({ option, height = 260, ariaLabel }: { option: ChartOption; height?: number; ariaLabel: string }) {
  const ref = useRef<HTMLDivElement>(null);
  const chart = useRef<ECharts | null>(null);
  const { isDark } = useResolvedTheme();

  useEffect(() => {
    if (!ref.current) {
      return;
    }
    const instance = init(ref.current, isDark ? 'dark' : undefined, { renderer: 'canvas' });
    chart.current = instance;
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
