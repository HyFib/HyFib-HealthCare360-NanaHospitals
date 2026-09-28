import { makeStyles, mergeClasses, tokens, Tooltip } from '@fluentui/react-components';
import { WeatherMoonRegular } from '@fluentui/react-icons';
import { formatTimeOfDay } from './shared/dates';

const useStyles = makeStyles({
  chip: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: '3px',
    padding: '1px 6px',
    borderRadius: tokens.borderRadiusMedium,
    fontSize: tokens.fontSizeBase200,
    fontWeight: tokens.fontWeightSemibold,
    lineHeight: '18px',
    whiteSpace: 'nowrap',
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    color: tokens.colorNeutralForeground1,
    backgroundColor: tokens.colorNeutralBackground3,
  },
  off: { color: tokens.colorNeutralForeground3, backgroundColor: tokens.colorNeutralBackground4, fontStyle: 'italic' },
  swatch: { width: '8px', height: '8px', borderRadius: '50%', flexShrink: 0 },
});

export interface ShiftLike {
  code: string | null;
  name?: string | null;
  color?: string | null;
  startTime?: string | null;
  endTime?: string | null;
  isNightShift?: boolean;
}

/** Colour dot + shift code (the colour is never the only signal: the code is always written). */
export function ShiftChip({ shift, isWeekOff, className }: { shift?: ShiftLike | null; isWeekOff?: boolean; className?: string }) {
  const styles = useStyles();
  if (isWeekOff || !shift) {
    return <span className={mergeClasses(styles.chip, styles.off, className)}>{isWeekOff ? 'Off' : '—'}</span>;
  }
  const times = shift.startTime && shift.endTime ? `${formatTimeOfDay(shift.startTime)} to ${formatTimeOfDay(shift.endTime)}` : '';
  const label = [shift.name ?? shift.code, times].filter(Boolean).join(' · ');
  return (
    <Tooltip content={label || (shift.code ?? '')} relationship="description">
      <span className={mergeClasses(styles.chip, className)}>
        <span className={styles.swatch} style={{ backgroundColor: shift.color ?? tokens.colorBrandBackground }} aria-hidden />
        {shift.code}
        {shift.isNightShift && <WeatherMoonRegular aria-label="Night shift" />}
      </span>
    </Tooltip>
  );
}
