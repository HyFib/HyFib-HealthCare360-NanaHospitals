import { escapeHtml } from '@hc360/core';
import type { BarSeriesOption, LineSeriesOption, PieSeriesOption } from 'echarts/charts';
import type { ChartOption } from '@/lib/EChart';
import type { KpiUnit, KpiValue, ReportChartData, ReportChartHint, ReportChartType, ReportValueType } from './api';
import { formatAxisValue, formatCategory, formatKpi, formatKpiFull, formatValue } from './format';

/**
 * ECharts options for report charts, dashboard widgets and KPI trends (pure and unit-tested). Canvas charts cannot
 * read CSS variables, so the palette is fixed and chosen to stay readable on the light and dark themes.
 */
export const PALETTE = [
  '#0F6CBD',
  '#13A10E',
  '#CA5010',
  '#8764B8',
  '#038387',
  '#C239B3',
  '#986F0B',
  '#4F6BED',
  '#E3008C',
  '#498205',
  '#DA3B01',
  '#005B70',
];

export interface ChartBuildOptions {
  /** Dashboard tiles: fewer labels, no value labels, smaller legend. */
  compact?: boolean;
}

interface TooltipItem {
  axisValueLabel?: string;
  name?: string;
  marker?: string;
  seriesName?: string;
  seriesIndex?: number;
  value?: unknown;
  percent?: number;
}

function numeric(value: unknown): number | null {
  if (typeof value === 'number' && Number.isFinite(value)) {
    return value;
  }
  if (Array.isArray(value)) {
    return numeric(value[value.length - 1]);
  }
  return null;
}

function distinct<T>(values: readonly T[]): T[] {
  return [...new Set(values)];
}

const ADDITIVE: ReadonlySet<ReportValueType> = new Set(['Integer', 'Decimal', 'Money']);

/**
 * Chart views that suit a report: time series as lines/areas/bars, rankings as bars or a pie; stacked bars need two or
 * more series. Dashboards can also show the table or the headline figures.
 */
export function availableViews(
  hint: ReportChartHint | null | undefined,
  placement: 'report' | 'dashboard',
): ReportChartType[] {
  const views: ReportChartType[] = [];
  if (hint && hint.type !== 'Table' && hint.type !== 'Summary') {
    views.push(hint.type);
    const candidates: ReportChartType[] = hint.timeSeries ? ['Line', 'Area', 'Bar'] : ['Bar', 'Pie'];
    if (hint.seriesKeys.length > 1) {
      candidates.push('StackedBar');
    }
    views.push(...candidates);
  }
  views.push('Table');
  if (placement === 'dashboard') {
    views.push('Summary');
  }
  return distinct(views);
}

/** The view a report opens with: its own chart, else the table. */
export function defaultView(hint: ReportChartHint | null | undefined): ReportChartType {
  return hint && hint.type !== 'Summary' ? hint.type : 'Table';
}

/** The ECharts option for a report chart (bars, stacked bars, lines, areas or a donut). */
export function reportChartOption(chart: ReportChartData, options: ChartBuildOptions = {}): ChartOption {
  return chart.type === 'Pie' ? pieOption(chart, options) : cartesianOption(chart, options);
}

function cartesianOption(chart: ReportChartData, options: ChartBuildOptions): ChartOption {
  const compact = options.compact ?? false;
  const categories = chart.categories.map((c) => formatCategory(c, chart.categoryType));
  const units = distinct(chart.series.map((s) => s.type));
  const dual = units.length === 2;
  const isLine = chart.type === 'Line' || chart.type === 'Area';
  // Rankings read best as horizontal bars (long department names, biggest on top); time runs left to right.
  const horizontal = !chart.timeSeries && !dual && !isLine;
  const allAdditive = chart.series.every((s) => ADDITIVE.has(s.type));
  const showValueLabels = !compact && !isLine && chart.series.length === 1 && categories.length <= 15;
  const axisIndex = (type: ReportValueType) => (dual && type === units[1] ? 1 : 0);

  const series: (BarSeriesOption | LineSeriesOption)[] = chart.series.map((s) => {
    // With two units (for example visits and a percentage) the second unit is drawn as a line on its own axis.
    const asLine = isLine || (dual && axisIndex(s.type) === 1);
    if (asLine) {
      const line: LineSeriesOption = {
        type: 'line',
        name: s.label,
        data: s.values,
        yAxisIndex: axisIndex(s.type),
        showSymbol: categories.length <= 31,
        symbolSize: 6,
        connectNulls: false,
        lineStyle: { width: 2 },
      };
      if (chart.type === 'Area') {
        line.areaStyle = { opacity: chart.series.length > 1 ? 0.12 : 0.2 };
        if (chart.series.length > 1 && allAdditive) {
          line.stack = 'total';
        }
      }
      return line;
    }
    const bar: BarSeriesOption = {
      type: 'bar',
      name: s.label,
      data: s.values,
      barMaxWidth: 36,
      stack: chart.type === 'StackedBar' ? 'total' : undefined,
      emphasis: { focus: 'series' },
    };
    if (!horizontal) {
      bar.yAxisIndex = axisIndex(s.type);
    }
    if (showValueLabels) {
      bar.label = {
        show: true,
        position: horizontal ? 'right' : 'top',
        fontSize: 11,
        formatter: (params: { value?: unknown }) => {
          const value = numeric(params.value);
          return value === null ? '' : formatAxisValue(value, s.type);
        },
      };
    }
    return bar;
  });

  const valueAxis = (type: ReportValueType) => ({
    type: 'value' as const,
    axisLabel: { formatter: (value: number) => formatAxisValue(value, type), fontSize: compact ? 10 : 12 },
    splitLine: { lineStyle: { opacity: 0.5 } },
  });
  const categoryAxis = {
    type: 'category' as const,
    data: categories,
    inverse: horizontal,
    axisTick: { alignWithLabel: true },
    axisLabel: horizontal
      ? { width: compact ? 90 : 160, overflow: 'truncate' as const, fontSize: compact ? 10 : 12 }
      : { hideOverlap: true, fontSize: compact ? 10 : 12 },
  };
  const firstUnit = units[0] ?? 'Decimal';
  const legend = chart.series.length > 1;

  return {
    color: PALETTE,
    grid: {
      left: 8,
      right: horizontal && showValueLabels ? 48 : 16,
      top: legend ? 36 : 16,
      bottom: 8,
      containLabel: true,
    },
    legend: legend ? { type: 'scroll', top: 0, textStyle: { fontSize: compact ? 11 : 12 } } : undefined,
    tooltip: {
      trigger: 'axis',
      axisPointer: { type: isLine ? 'line' : 'shadow' },
      confine: true,
      formatter: (params: unknown) => axisTooltip(params, chart),
    },
    xAxis: horizontal ? valueAxis(firstUnit) : categoryAxis,
    yAxis: horizontal ? categoryAxis : dual ? units.map((unit) => valueAxis(unit)) : valueAxis(firstUnit),
    series,
  };
}

function axisTooltip(params: unknown, chart: ReportChartData): string {
  const items = (Array.isArray(params) ? params : [params]) as TooltipItem[];
  const title = escapeHtml(items[0]?.axisValueLabel ?? items[0]?.name ?? '');
  const lines = items.map((item) => {
    const series = chart.series[item.seriesIndex ?? 0];
    const value = numeric(item.value);
    return `${item.marker ?? ''}${escapeHtml(item.seriesName ?? '')}: <b>${escapeHtml(formatValue(value, series?.type ?? 'Decimal', 'figure'))}</b>`;
  });
  return [`<b>${title}</b>`, ...lines].join('<br/>');
}

function pieOption(chart: ReportChartData, options: ChartBuildOptions): ChartOption {
  const compact = options.compact ?? false;
  const series = chart.series[0];
  const data = chart.categories
    .map((category, index) => ({
      name: formatCategory(category, chart.categoryType),
      value: series?.values[index] ?? null,
    }))
    .filter((item): item is { name: string; value: number } => item.value !== null && item.value > 0);
  const pie: PieSeriesOption = {
    type: 'pie',
    name: series?.label ?? '',
    radius: compact ? ['38%', '70%'] : ['45%', '72%'],
    center: compact ? ['50%', '50%'] : ['38%', '52%'],
    avoidLabelOverlap: true,
    itemStyle: { borderWidth: 1 },
    label: { show: !compact, formatter: '{d}%', fontSize: 11 },
    data,
  };
  return {
    color: PALETTE,
    tooltip: {
      trigger: 'item',
      confine: true,
      formatter: (params: unknown) => {
        const item = params as TooltipItem;
        const value = numeric(item.value);
        const percent = typeof item.percent === 'number' ? ` (${item.percent.toFixed(1)}%)` : '';
        return `${item.marker ?? ''}${escapeHtml(item.name ?? '')}: <b>${escapeHtml(formatValue(value, series?.type ?? 'Decimal', 'figure'))}</b>${percent}`;
      },
    },
    legend: compact
      ? undefined
      : {
          type: 'scroll',
          orient: 'vertical',
          right: 0,
          top: 'middle',
          textStyle: { fontSize: 12, width: 160, overflow: 'truncate' },
        },
    series: [pie],
  };
}

// ------------------------------------------------------------------------------------------------ KPI trend

/** Bars for counts and money, a line for rates; a dashed line marks the period average. */
export function kpiTrendOption(kpi: Pick<KpiValue, 'label' | 'unit' | 'trend'>): ChartOption {
  const points = kpi.trend ?? [];
  const values = points.map((p) => p.value);
  const average = values.length > 0 ? values.reduce((sum, v) => sum + v, 0) / values.length : null;
  const asBars = kpi.unit === 'count' || kpi.unit === 'inr';
  const format = (value: number) => (kpi.unit === 'inr' ? formatKpi(value, 'inr') : formatKpi(value, kpi.unit));
  const markLine =
    average === null
      ? undefined
      : {
          silent: true,
          symbol: 'none' as const,
          lineStyle: { type: 'dashed' as const, opacity: 0.8 },
          label: { formatter: `Average ${format(average)}`, position: 'insideEndTop' as const, fontSize: 11 },
          data: [{ yAxis: average }],
        };
  const series: BarSeriesOption | LineSeriesOption = asBars
    ? { type: 'bar', name: kpi.label, data: values, barMaxWidth: 28, markLine }
    : {
        type: 'line',
        name: kpi.label,
        data: values,
        showSymbol: points.length <= 31,
        symbolSize: 6,
        areaStyle: { opacity: 0.12 },
        markLine,
      };
  return {
    color: [PALETTE[0] ?? '#0F6CBD'],
    grid: { left: 8, right: 16, top: 24, bottom: 8, containLabel: true },
    tooltip: {
      trigger: 'axis',
      confine: true,
      formatter: (params: unknown) => {
        const item = (Array.isArray(params) ? params[0] : params) as TooltipItem | undefined;
        const value = numeric(item?.value);
        return `<b>${escapeHtml(item?.axisValueLabel ?? '')}</b><br/>${item?.marker ?? ''}${escapeHtml(kpi.label)}: <b>${escapeHtml(formatKpiFull(value, kpi.unit))}</b>`;
      },
    },
    xAxis: {
      type: 'category',
      data: points.map((p) => formatCategory(p.date, 'Date')),
      axisLabel: { hideOverlap: true },
    },
    yAxis: { type: 'value', axisLabel: { formatter: (value: number) => kpiAxisLabel(value, kpi.unit) } },
    series: [series],
  };
}

function kpiAxisLabel(value: number, unit: KpiUnit): string {
  switch (unit) {
    case 'inr':
      return formatAxisValue(value, 'Money');
    case 'percent':
      return formatAxisValue(value, 'Percent');
    case 'minutes':
      return formatAxisValue(value, 'Minutes');
    case 'days':
      return formatAxisValue(value, 'Days');
    default:
      return formatAxisValue(value, 'Integer');
  }
}

// ------------------------------------------------------------------------------------------------ sparkline

/** SVG paths for a tiny trend line and its shaded area inside a `width`×`height` box. */
export function sparklinePaths(
  values: readonly number[],
  width: number,
  height: number,
  padding = 2,
): { line: string; area: string } | null {
  const finite = values.filter((v) => Number.isFinite(v));
  if (finite.length < 2) {
    return null;
  }
  const min = Math.min(...finite);
  const max = Math.max(...finite);
  const span = max - min || 1;
  const stepX = (width - padding * 2) / (finite.length - 1);
  const points = finite.map((value, index) => {
    const x = padding + index * stepX;
    // A flat series sits in the middle rather than on the floor.
    const y = max === min ? height / 2 : padding + (1 - (value - min) / span) * (height - padding * 2);
    return `${round(x)},${round(y)}`;
  });
  const line = `M${points.join(' L')}`;
  const area = `${line} L${round(padding + (finite.length - 1) * stepX)},${height} L${padding},${height} Z`;
  return { line, area };
}

function round(value: number): number {
  return Math.round(value * 10) / 10;
}
