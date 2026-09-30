import {
  Caption1,
  makeStyles,
  mergeClasses,
  Skeleton,
  SkeletonItem,
  Text,
  tokens,
  Tooltip,
} from '@fluentui/react-components';
import { ArrowDownRegular, ArrowUpRegular, InfoRegular, SubtractRegular } from '@fluentui/react-icons';
import { Link } from '@tanstack/react-router';
import type { KpiValue, ReportInputs } from './api';
import { sparklinePaths } from './charts';
import { formatKpi, formatKpiFull, kpiChange } from './format';
import { DEFAULT_KPI_ICON, KPI_ICONS } from './icons';
import { toSearchInputs } from './params';

const useStyles = makeStyles({
  tile: {
    display: 'flex',
    flexDirection: 'column',
    gap: '6px',
    padding: '14px 16px 10px',
    borderRadius: tokens.borderRadiusXLarge,
    backgroundColor: tokens.colorNeutralBackground1,
    boxShadow: tokens.shadow4,
    color: tokens.colorNeutralForeground1,
    textDecoration: 'none',
    minWidth: 0,
    transitionProperty: 'box-shadow, transform',
    transitionDuration: tokens.durationFast,
    ':hover': { boxShadow: tokens.shadow8 },
    ':focus-visible': { outline: `2px solid ${tokens.colorStrokeFocus2}`, outlineOffset: '2px' },
  },
  compact: { padding: '10px 12px 8px', boxShadow: 'none', border: `1px solid ${tokens.colorNeutralStroke2}` },
  head: { display: 'flex', alignItems: 'center', gap: '8px', minWidth: 0 },
  icon: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    width: '28px',
    height: '28px',
    flexShrink: 0,
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorBrandBackground2,
    color: tokens.colorBrandForeground1,
    fontSize: '18px',
  },
  label: {
    flex: 1,
    minWidth: 0,
    color: tokens.colorNeutralForeground2,
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
  },
  info: { color: tokens.colorNeutralForeground3, display: 'flex', fontSize: '16px' },
  value: {
    fontSize: tokens.fontSizeHero700,
    lineHeight: tokens.lineHeightHero700,
    fontWeight: tokens.fontWeightSemibold,
    fontVariantNumeric: 'tabular-nums',
    whiteSpace: 'nowrap',
  },
  valueCompact: { fontSize: tokens.fontSizeBase600, lineHeight: tokens.lineHeightBase600 },
  foot: { display: 'flex', alignItems: 'flex-end', justifyContent: 'space-between', gap: '8px', minHeight: '28px' },
  change: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: '2px',
    fontWeight: tokens.fontWeightSemibold,
    whiteSpace: 'nowrap',
  },
  good: { color: tokens.colorPaletteGreenForeground1 },
  bad: { color: tokens.colorPaletteRedForeground1 },
  neutral: { color: tokens.colorNeutralForeground3 },
  previous: {
    color: tokens.colorNeutralForeground3,
    whiteSpace: 'nowrap',
    overflow: 'hidden',
    textOverflow: 'ellipsis',
  },
  changeBlock: { display: 'flex', flexDirection: 'column', minWidth: 0 },
  spark: { flexShrink: 0 },
});

/**
 * One executive KPI: the value, the change on the previous period (green when better, red when worse — with an arrow and
 * words, never colour alone), a sparkline and a link to the report behind it.
 */
export function KpiTile({ kpi, inputs, compact }: { kpi: KpiValue; inputs: ReportInputs; compact?: boolean }) {
  const styles = useStyles();
  const Icon = KPI_ICONS[kpi.key] ?? DEFAULT_KPI_ICON;
  const change = kpiChange(kpi);
  const Arrow =
    change?.direction === 'up' ? ArrowUpRegular : change?.direction === 'down' ? ArrowDownRegular : SubtractRegular;
  const full = formatKpiFull(kpi.value, kpi.unit);

  const body = (
    <>
      <span className={styles.head}>
        <span className={styles.icon} aria-hidden>
          <Icon />
        </span>
        <Text className={styles.label}>{kpi.label}</Text>
        {!compact && (
          <Tooltip content={kpi.definition} relationship="description" withArrow>
            <span className={styles.info} aria-label={`How ${kpi.label} is calculated`}>
              <InfoRegular />
            </span>
          </Tooltip>
        )}
      </span>
      <span className={mergeClasses(styles.value, compact && styles.valueCompact)} title={full}>
        {formatKpi(kpi.value, kpi.unit)}
      </span>
      <span className={styles.foot}>
        <span className={styles.changeBlock}>
          {change ? (
            <Caption1 className={mergeClasses(styles.change, styles[change.tone])} aria-label={change.description}>
              <Arrow aria-hidden />
              {change.text}
            </Caption1>
          ) : (
            <Caption1 className={styles.neutral}>No earlier figure</Caption1>
          )}
          {!compact && kpi.previous !== null && (
            <Caption1 className={styles.previous}>Previous period {formatKpi(kpi.previous, kpi.unit)}</Caption1>
          )}
        </span>
        {!compact && kpi.trend && kpi.trend.length > 1 && (
          <Sparkline values={kpi.trend.map((p) => p.value)} tone={change?.tone ?? 'neutral'} />
        )}
      </span>
    </>
  );

  if (!kpi.reportId) {
    return <div className={mergeClasses(styles.tile, compact && styles.compact)}>{body}</div>;
  }
  return (
    <Link
      to="/analytics/reports/$reportId"
      params={{ reportId: kpi.reportId }}
      search={toSearchInputs(inputs)}
      className={mergeClasses(styles.tile, compact && styles.compact)}
      aria-label={`${kpi.label}: ${full}. ${change?.description ?? ''}. Open the report.`}
    >
      {body}
    </Link>
  );
}

export function KpiTileSkeleton() {
  const styles = useStyles();
  return (
    <div className={styles.tile} aria-busy="true">
      <Skeleton aria-label="Loading figure">
        <SkeletonItem size={16} style={{ width: '60%' }} />
        <SkeletonItem size={40} style={{ width: '45%', marginTop: 10 }} />
        <SkeletonItem size={12} style={{ width: '70%', marginTop: 10 }} />
      </Skeleton>
    </div>
  );
}

const SPARK_WIDTH = 96;
const SPARK_HEIGHT = 28;

function Sparkline({ values, tone }: { values: number[]; tone: 'good' | 'bad' | 'neutral' }) {
  const styles = useStyles();
  const paths = sparklinePaths(values, SPARK_WIDTH, SPARK_HEIGHT);
  if (!paths) {
    return null;
  }
  const stroke =
    tone === 'bad'
      ? tokens.colorPaletteRedForeground1
      : tone === 'good'
        ? tokens.colorPaletteGreenForeground1
        : tokens.colorBrandForeground1;
  return (
    <svg
      className={styles.spark}
      width={SPARK_WIDTH}
      height={SPARK_HEIGHT}
      viewBox={`0 0 ${SPARK_WIDTH} ${SPARK_HEIGHT}`}
      aria-hidden
      focusable="false"
    >
      <path d={paths.area} fill={stroke} opacity={0.12} />
      <path d={paths.line} fill="none" stroke={stroke} strokeWidth={1.6} strokeLinejoin="round" strokeLinecap="round" />
    </svg>
  );
}
