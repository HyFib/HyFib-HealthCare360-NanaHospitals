import { describe, expect, it } from 'vitest';
import type { ReportChartData } from './api';
import { availableViews, defaultView, kpiTrendOption, reportChartOption, sparklinePaths } from './charts';

const ranking: ReportChartData = {
  type: 'Bar',
  categoryKey: 'department',
  categoryLabel: 'Department',
  categoryType: 'Text',
  categories: ['Cardiology', '<img src=x onerror=alert(1)>', 'Others'],
  series: [{ key: 'revenue', label: 'Revenue', type: 'Money', values: [500_000, 120_000, null] }],
  timeSeries: false,
  keys: ['d1', 'd2', null],
};

const trend: ReportChartData = {
  type: 'Line',
  categoryKey: 'date',
  categoryLabel: 'Date',
  categoryType: 'Date',
  categories: ['2026-09-01', '2026-09-02'],
  series: [
    { key: 'visits', label: 'Visits', type: 'Integer', values: [120, 140] },
    { key: 'noShow', label: 'No-show %', type: 'Percent', values: [4.5, 3.2] },
  ],
  timeSeries: true,
  keys: null,
};

type AnyOption = Record<string, unknown> & { series: Record<string, unknown>[] };

describe('views', () => {
  it('offers views that suit the report', () => {
    expect(availableViews({ type: 'Line', categoryKey: 'date', seriesKeys: ['a'], timeSeries: true, topN: 15 }, 'report')).toEqual(['Line', 'Area', 'Bar', 'Table']);
    expect(availableViews({ type: 'Bar', categoryKey: 'd', seriesKeys: ['a', 'b'], timeSeries: false, topN: 15 }, 'dashboard')).toEqual([
      'Bar',
      'Pie',
      'StackedBar',
      'Table',
      'Summary',
    ]);
    expect(availableViews(null, 'dashboard')).toEqual(['Table', 'Summary']);
    expect(defaultView(null)).toBe('Table');
    expect(defaultView({ type: 'Pie', categoryKey: 'd', seriesKeys: ['a'], timeSeries: false, topN: 15 })).toBe('Pie');
  });
});

describe('report charts', () => {
  it('draws rankings as horizontal bars with the biggest on top', () => {
    const option = reportChartOption(ranking) as unknown as AnyOption;
    expect((option.yAxis as { type: string; inverse: boolean }).type).toBe('category');
    expect((option.yAxis as { inverse: boolean }).inverse).toBe(true);
    expect(option.series[0]?.type).toBe('bar');
    expect(option.series[0]?.data).toEqual([500_000, 120_000, null]);
  });

  it('escapes category names in tooltips', () => {
    const option = reportChartOption(ranking) as unknown as AnyOption;
    const formatter = (option.tooltip as { formatter: (params: unknown) => string }).formatter;
    const html = formatter([{ axisValueLabel: '<img src=x onerror=alert(1)>', seriesName: 'Revenue', seriesIndex: 0, value: 120_000, marker: '' }]);
    expect(html).not.toContain('<img');
    expect(html).toContain('&lt;img');
    expect(html).toMatch(/1,20,000/);
  });

  it('puts a second unit on its own axis as a line', () => {
    const option = reportChartOption({ ...trend, type: 'Bar' }) as unknown as AnyOption;
    expect(Array.isArray(option.yAxis)).toBe(true);
    expect(option.series.map((s) => s.type)).toEqual(['bar', 'line']);
    expect(option.series[1]?.yAxisIndex).toBe(1);
  });

  it('stacks bars and areas when asked', () => {
    const stacked = reportChartOption({ ...ranking, type: 'StackedBar', series: [...ranking.series, { key: 'b', label: 'B', type: 'Money', values: [1, 2, 3] }] }) as unknown as AnyOption;
    expect(stacked.series.every((s) => s.stack === 'total')).toBe(true);
    const area = reportChartOption({ ...trend, type: 'Area', series: [trend.series[0]!] }) as unknown as AnyOption;
    expect(area.series[0]?.areaStyle).toBeDefined();
  });

  it('draws a donut from the first series and skips empty slices', () => {
    const option = reportChartOption({ ...ranking, type: 'Pie' }) as unknown as AnyOption;
    expect(option.series[0]?.type).toBe('pie');
    expect(option.series[0]?.data).toEqual([
      { name: 'Cardiology', value: 500_000 },
      { name: '<img src=x onerror=alert(1)>', value: 120_000 },
    ]);
  });
});

describe('KPI trends and sparklines', () => {
  it('uses bars for counts and money with an average line', () => {
    const option = kpiTrendOption({
      label: 'OP visits',
      unit: 'count',
      trend: [
        { date: '2026-09-01', value: 100 },
        { date: '2026-09-02', value: 200 },
      ],
    }) as unknown as AnyOption;
    expect(option.series[0]?.type).toBe('bar');
    expect((option.series[0]?.markLine as { data: { yAxis: number }[] }).data[0]?.yAxis).toBe(150);
  });

  it('builds sparkline paths and needs two points', () => {
    const paths = sparklinePaths([1, 3, 2], 100, 20);
    expect(paths?.line).toBe('M2,18 L50,2 L98,10');
    expect(paths?.area.endsWith('Z')).toBe(true);
    expect(sparklinePaths([5], 100, 20)).toBeNull();
    expect(sparklinePaths([5, 5], 100, 20)?.line).toBe('M2,10 L98,10');
  });
});
