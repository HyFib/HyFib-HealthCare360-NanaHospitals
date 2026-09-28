import { makeStyles, mergeClasses, tokens } from '@fluentui/react-components';
import { WeatherMoonRegular, WeatherSunnyRegular, WeatherPartlyCloudyDayRegular } from '@fluentui/react-icons';
import type { FluentIcon } from '@fluentui/react-icons';
import { useT, type MessageKey } from '@/i18n';
import { useFormat } from '@/i18n/useFormat';
import type { PublicSlot } from '@/lib/types';
import { groupByDayPart, type DayPart } from './slots';

const PART_LABEL: Record<DayPart, MessageKey> = {
  morning: 'booking.time.morning',
  afternoon: 'booking.time.afternoon',
  evening: 'booking.time.evening',
};

const PART_ICON: Record<DayPart, FluentIcon> = {
  morning: WeatherSunnyRegular,
  afternoon: WeatherPartlyCloudyDayRegular,
  evening: WeatherMoonRegular,
};

const useStyles = makeStyles({
  root: { display: 'flex', flexDirection: 'column', gap: '16px' },
  group: { display: 'flex', flexDirection: 'column', gap: '8px', border: 'none', margin: 0, padding: 0, minWidth: 0 },
  legend: {
    display: 'flex',
    alignItems: 'center',
    gap: '6px',
    padding: 0,
    marginBottom: '8px',
    color: tokens.colorNeutralForeground2,
    fontWeight: tokens.fontWeightSemibold,
  },
  grid: { display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(96px, 1fr))', gap: '8px' },
  slot: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    justifyContent: 'center',
    gap: '2px',
    minHeight: '52px',
    padding: '6px 4px',
    borderRadius: tokens.borderRadiusLarge,
    border: `1px solid ${tokens.colorNeutralStroke1}`,
    backgroundColor: tokens.colorNeutralBackground1,
    color: tokens.colorNeutralForeground1,
    fontFamily: 'inherit',
    fontSize: tokens.fontSizeBase400,
    fontWeight: tokens.fontWeightSemibold,
    cursor: 'pointer',
    ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover, border: `1px solid ${tokens.colorBrandStroke1}` },
    ':focus-visible': { outline: `3px solid ${tokens.colorStrokeFocus2}`, outlineOffset: '2px' },
  },
  selected: {
    backgroundColor: tokens.colorBrandBackground,
    color: tokens.colorNeutralForegroundOnBrand,
    border: `1px solid ${tokens.colorBrandBackground}`,
    ':hover': { backgroundColor: tokens.colorBrandBackgroundHover, border: `1px solid ${tokens.colorBrandBackgroundHover}` },
  },
  seats: { fontSize: tokens.fontSizeBase200, fontWeight: tokens.fontWeightRegular },
});

/** Free times of one day, grouped into morning / afternoon / evening, as large buttons (a radio group). */
export function SlotPicker({ slots, selected, onSelect }: { slots: PublicSlot[]; selected: string | null; onSelect: (slot: PublicSlot) => void }) {
  const styles = useStyles();
  const t = useT();
  const f = useFormat();
  const groups = groupByDayPart(slots);
  return (
    <div className={styles.root} role="radiogroup" aria-label={t('booking.time.label')}>
      {groups.map(({ part, slots: partSlots }) => {
        const Icon = PART_ICON[part];
        return (
          <fieldset key={part} className={styles.group}>
            <legend className={styles.legend}>
              <Icon aria-hidden />
              {t(PART_LABEL[part])}
            </legend>
            <div className={styles.grid}>
              {partSlots.map((slot) => {
                const isSelected = slot.startsAt === selected;
                const time = f.clock(slot.localTime);
                const fewLeft = slot.seatsLeft > 1 && slot.seatsLeft <= 3;
                return (
                  <button
                    key={slot.startsAt}
                    type="button"
                    role="radio"
                    aria-checked={isSelected}
                    aria-label={fewLeft ? `${time}, ${t('booking.time.seatsLeft', { count: slot.seatsLeft })}` : time}
                    className={mergeClasses(styles.slot, isSelected && styles.selected)}
                    onClick={() => onSelect(slot)}
                  >
                    <span aria-hidden>{time}</span>
                    {fewLeft && (
                      <span className={styles.seats} aria-hidden>
                        {t('booking.time.seatsLeft', { count: slot.seatsLeft })}
                      </span>
                    )}
                  </button>
                );
              })}
            </div>
          </fieldset>
        );
      })}
    </div>
  );
}
