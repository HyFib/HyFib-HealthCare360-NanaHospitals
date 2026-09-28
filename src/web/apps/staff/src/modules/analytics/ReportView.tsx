import { Badge, Caption1, makeStyles, mergeClasses, Tab, TabList, Text, tokens, Tooltip } from '@fluentui/react-components';
import { DatabaseRegular } from '@fluentui/react-icons';
import { formatDateTime } from '@hc360/core';
import { useMemo } from 'react';
import { EChart } from '@/lib/EChart';
import type { ReportChartData, ReportChartType, ReportFigure, ReportResult } from './api';
import { reportChartOption } from './charts';
import { formatValue } from './format';
import { VIEW_ICONS } from './icons';
import { CHART_TYPE_LABELS } from './labels';

const useStyles = makeStyles({
  figures: { display: 'grid', gap: '12px', gridTemplateColumns: 'repeat(auto-fill, minmax(170px, 1fr))' },
  figuresCompact: { display: 'grid', gap: '8px', gridTemplateColumns: 'repeat(auto-fill, minmax(130px, 1fr))' },
  figure: {
    display: 'flex',
    flexDirection: 'column',
    gap: '2px',
    padding: '12px 14px',
    borderRadius: tokens.borderRadiusLarge,
    backgroundColor: tokens.colorNeutralBackground2,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    minWidth: 0,
  },
  figureValue: {
    fontSize: tokens.fontSizeBase600,
    lineHeight: tokens.lineHeightBase600,
    fontWeight: tokens.fontWeightSemibold,
    fontVariantNumeric: 'tabular-nums',
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
  },
  figureValueCompact: { fontSize: tokens.fontSizeBase500, lineHeight: tokens.lineHeightBase500 },
  muted: { color: tokens.colorNeutralForeground3 },
  meta: { display: 'flex', flexWrap: 'wrap', gap: '6px', alignItems: 'center' },
  source: { display: 'inline-flex', alignItems: 'center', gap: '4px', color: tokens.colorNeutralForeground3 },
});

/** A report chart drawn with the shared ECharts wrapper. */
export function ReportChart({ chart, title, height = 320, compact }: { chart: ReportChartData; title: string; height?: number; compact?: boolean }) {
  const option = useMemo(() => reportChartOption(chart, { compact }), [chart, compact]);
  const series = chart.series.map((s) => s.label).join(', ');
  return <EChart option={option} height={height} ariaLabel={`${title}: ${series} by ${chart.categoryLabel.toLowerCase()}. The same figures are in the table.`} />;
}

/** Headline figures ("Net revenue", "Occupancy") as number tiles. */
export function SummaryFigures({ figures, compact }: { figures: ReportFigure[]; compact?: boolean }) {
  const styles = useStyles();
  if (figures.length === 0) {
    return null;
  }
  return (
    <div className={compact ? styles.figuresCompact : styles.figures}>
      {figures.map((figure) => {
        const text = formatValue(figure.value, figure.type, 'figure');
        return (
          <div key={figure.key} className={styles.figure}>
            <Caption1 className={styles.muted}>{figure.label}</Caption1>
            <span className={mergeClasses(styles.figureValue, compact && styles.figureValueCompact)} title={text}>
              {text}
            </span>
          </div>
        );
      })}
    </div>
  );
}

/** Chart / table switch with icons; keyboard friendly (arrow keys move between views). */
export function ViewSwitcher({ views, value, onChange, size = 'small' }: { views: ReportChartType[]; value: ReportChartType; onChange: (view: ReportChartType) => void; size?: 'small' | 'medium' }) {
  return (
    <TabList size={size} selectedValue={value} onTabSelect={(_, data) => onChange(data.value as ReportChartType)} aria-label="Show as">
      {views.map((view) => {
        const Icon = VIEW_ICONS[view];
        return (
          <Tab key={view} value={view} icon={<Icon />}>
            {CHART_TYPE_LABELS[view]}
          </Tab>
        );
      })}
    </TabList>
  );
}

/** What the report covers, in words: period, facility, filters, when it was produced and from where. */
export function ReportMeta({ result }: { result: ReportResult }) {
  const styles = useStyles();
  return (
    <div className={styles.meta}>
      {result.filters.map((filter) => (
        <Badge key={`${filter.label}:${filter.value}`} appearance="tint" color="informative" shape="rounded">
          {filter.label}: {filter.value}
        </Badge>
      ))}
      <Tooltip content={`Figures read from the ${result.dataSource.toLowerCase()}`} relationship="description">
        <Text size={200} className={styles.source}>
          <DatabaseRegular aria-hidden />
          Generated {formatDateTime(result.generatedAt)}
        </Text>
      </Tooltip>
    </div>
  );
}
