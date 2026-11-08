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
    // Holds the visually hidden date: without it, a day scrolled out of the strip would widen the page.
    position: 'relative',
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
  srOnly: {
    position: 'absolute',
    width: '1px',
    height: '1px',
    overflow: 'hidden',
    clip: 'rect(0 0 0 0)',
    whiteSpace: 'nowrap',
  },
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
          const shown = loading ? '…' : free > 0 ? t('booking.day.free', { count: free }) : t('booking.day.none');
          return (
            <button
              key={day}
              type="button"
              role="radio"
              aria-checked={isSelected}
              className={mergeClasses(styles.day, isSelected && styles.selected)}
              disabled={loading || free === 0}
              onClick={() => onSelect(day)}
            >
              {/* The name is the text on the button, so a voice user can say what they see (WCAG 2.5.3); the full date
                  follows it, for a screen reader only. */}
              <span className={styles.weekday}>{f.weekday(day)}</span>{' '}
              <span className={styles.date}>{f.dayMonth(day)}</span>{' '}
              <span className={free > 0 ? styles.count : styles.none}>{shown}</span>
              <span className={styles.srOnly}>{`. ${describeDay(day, f, t, now)}, ${f.calendarDate(day)}`}</span>
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
