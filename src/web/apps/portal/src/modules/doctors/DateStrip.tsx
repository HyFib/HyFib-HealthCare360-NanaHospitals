import { Button, makeStyles, mergeClasses, tokens } from '@fluentui/react-components';
import { ChevronLeftRegular, ChevronRightRegular } from '@fluentui/react-icons';
import { useT } from '@/i18n';
import { useFormat } from '@/i18n/useFormat';
import { describeDay } from '@/lib/when';

const useStyles = makeStyles({
  root: { display: 'flex', alignItems: 'stretch', gap: '4px' },
  nav: { minWidth: '40px', flexShrink: 0, alignSelf: 'center' },
  days: {
    flex: 1,
    display: 'grid',
    gridAutoFlow: 'column',
    gridAutoColumns: 'minmax(68px, 1fr)',
    gap: '6px',
    overflowX: 'auto',
    scrollSnapType: 'x mandatory',
    padding: '4px 2px',
    scrollbarWidth: 'thin',
  },
  day: {
    scrollSnapAlign: 'start',
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    gap: '2px',
    minHeight: '76px',
    padding: '8px 4px',
    borderRadius: tokens.borderRadiusLarge,
    border: `1px solid ${tokens.colorNeutralStroke1}`,
    backgroundColor: tokens.colorNeutralBackground1,
    color: tokens.colorNeutralForeground1,
    fontFamily: 'inherit',
    cursor: 'pointer',
    ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover },
    ':focus-visible': { outline: `3px solid ${tokens.colorStrokeFocus2}`, outlineOffset: '2px' },
    ':disabled': {
      cursor: 'not-allowed',
      backgroundColor: tokens.colorNeutralBackground3,
      color: tokens.colorNeutralForeground4,
    },
  },
  selected: {
    border: `2px solid ${tokens.colorBrandStroke1}`,
    backgroundColor: tokens.colorBrandBackground2,
    color: tokens.colorBrandForeground2,
    ':hover': { backgroundColor: tokens.colorBrandBackground2Hover },
  },
  weekday: { fontSize: tokens.fontSizeBase200, fontWeight: tokens.fontWeightSemibold, textTransform: 'uppercase' },
  date: { fontSize: tokens.fontSizeBase400, fontWeight: tokens.fontWeightSemibold },
  count: { fontSize: tokens.fontSizeBase200, color: tokens.colorStatusSuccessForeground1 },
  none: { fontSize: tokens.fontSizeBase200, color: tokens.colorNeutralForeground3 },
});

/**
 * Seven days side by side (scrolls on narrow phones) with how many times are free each day. Days without free
 * times are disabled but still shown, so the week reads naturally.
 */
export function DateStrip({
  days,
  counts,
  selected,
  onSelect,
  onEarlier,
  onLater,
  loading,
  now,
}: {
  days: string[];
  counts: Map<string, number>;
  selected: string | null;
  onSelect: (day: string) => void;
  onEarlier?: () => void;
  onLater?: () => void;
  loading?: boolean;
  now: number;
}) {
  const styles = useStyles();
  const t = useT();
  const f = useFormat();
  return (
    <div className={styles.root}>
      <Button
        className={styles.nav}
        appearance="subtle"
        size="large"
        icon={<ChevronLeftRegular />}
        aria-label={t('booking.day.earlier')}
        disabled={!onEarlier}
        onClick={onEarlier}
      />
      <div className={styles.days} role="radiogroup" aria-label={t('booking.day.label')}>
        {days.map((day) => {
          const free = counts.get(day) ?? 0;
          const isSelected = day === selected;
          const label = `${describeDay(day, f, t, now)}, ${f.calendarDate(day)}. ${free > 0 ? t('booking.day.free', { count: free }) : t('booking.day.none')}`;
          return (
            <button
              key={day}
              type="button"
              role="radio"
              aria-checked={isSelected}
              aria-label={label}
              className={mergeClasses(styles.day, isSelected && styles.selected)}
              disabled={loading || free === 0}
              onClick={() => onSelect(day)}
            >
              <span className={styles.weekday} aria-hidden>
                {f.weekday(day)}
              </span>
              <span className={styles.date} aria-hidden>
                {f.dayMonth(day)}
              </span>
              <span className={free > 0 ? styles.count : styles.none} aria-hidden>
                {loading ? '…' : free > 0 ? t('booking.day.free', { count: free }) : t('booking.day.none')}
              </span>
            </button>
          );
        })}
      </div>
      <Button
        className={styles.nav}
        appearance="subtle"
        size="large"
        icon={<ChevronRightRegular />}
        aria-label={t('booking.day.later')}
        disabled={!onLater}
        onClick={onLater}
      />
    </div>
  );
}
