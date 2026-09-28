import {
  Badge,
  Button,
  Caption1,
  Field,
  Input,
  makeStyles,
  mergeClasses,
  MessageBar,
  MessageBarBody,
  Skeleton,
  SkeletonItem,
  Text,
  tokens,
  Tooltip,
  useArrowNavigationGroup,
} from '@fluentui/react-components';
import { ChevronLeftRegular, ChevronRightRegular, WeatherMoonRegular, WeatherPartlyCloudyDayRegular, WeatherSunnyRegular } from '@fluentui/react-icons';
import { todayIso } from '@hc360/core';
import { EmptyState, ErrorState } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { schedulingQueries, type PreferredSession, type SlotDto } from './api';
import { addDays, dateRange, dayParts, daysBetween, formatClock, formatDayLong, formatDayShort, isIsoDate, maxDate, minDate, type IsoDate } from './dates';
import {
  firstDayWithFreeSlots,
  groupSlotsByPartOfDay,
  isOnLeaveAllDay,
  restrictionsOn,
  sameInstant,
  slotsForMode,
  slotState,
  summarizeDays,
  type DayRestriction,
  type PartOfDay,
  type VisitMode,
} from './slots';

const useStyles = makeStyles({
  root: { display: 'flex', flexDirection: 'column', gap: '12px', minWidth: 0 },
  stripRow: { display: 'flex', alignItems: 'center', gap: '4px', minWidth: 0 },
  strip: {
    display: 'flex',
    gap: '6px',
    overflowX: 'auto',
    padding: '2px 2px 6px',
    flex: 1,
    minWidth: 0,
    scrollbarWidth: 'thin',
  },
  dateChip: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    gap: '1px',
    minWidth: '64px',
    padding: '6px 4px',
    borderRadius: tokens.borderRadiusLarge,
    border: `1px solid ${tokens.colorNeutralStroke1}`,
    backgroundColor: tokens.colorNeutralBackground1,
    color: tokens.colorNeutralForeground1,
    cursor: 'pointer',
    fontFamily: 'inherit',
    flexShrink: 0,
    ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover },
    ':focus-visible': { outline: `2px solid ${tokens.colorStrokeFocus2}`, outlineOffset: '1px' },
  },
  dateChipSelected: {
    backgroundColor: tokens.colorBrandBackground,
    color: tokens.colorNeutralForegroundOnBrand,
    border: `1px solid ${tokens.colorBrandBackground}`,
    ':hover': { backgroundColor: tokens.colorBrandBackgroundHover },
  },
  dateChipQuiet: { color: tokens.colorNeutralForeground3 },
  dateWeekday: { fontSize: tokens.fontSizeBase200 },
  dateDay: { fontSize: tokens.fontSizeBase500, fontWeight: tokens.fontWeightSemibold, lineHeight: tokens.lineHeightBase500 },
  dateStatus: { fontSize: tokens.fontSizeBase100, whiteSpace: 'nowrap' },
  statusFree: { color: tokens.colorPaletteGreenForeground1 },
  statusLeave: { color: tokens.colorPaletteRedForeground1 },
  onBrand: { color: 'inherit' },
  toolbar: { display: 'flex', alignItems: 'end', justifyContent: 'space-between', gap: '8px', flexWrap: 'wrap' },
  group: { display: 'flex', flexDirection: 'column', gap: '6px' },
  groupHeader: { display: 'flex', alignItems: 'center', gap: '8px', color: tokens.colorNeutralForeground2 },
  groupIcon: { fontSize: '18px', display: 'flex' },
  muted: { color: tokens.colorNeutralForeground3 },
  chips: { display: 'grid', gap: '6px', gridTemplateColumns: 'repeat(auto-fill, minmax(92px, 1fr))' },
  chip: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    justifyContent: 'center',
    minHeight: '44px',
    padding: '4px 6px',
    borderRadius: tokens.borderRadiusMedium,
    border: `1px solid ${tokens.colorNeutralStroke1}`,
    backgroundColor: tokens.colorNeutralBackground1,
    color: tokens.colorNeutralForeground1,
    cursor: 'pointer',
    fontFamily: 'inherit',
    fontSize: tokens.fontSizeBase300,
    fontWeight: tokens.fontWeightSemibold,
    ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover, border: `1px solid ${tokens.colorBrandStroke1}` },
    ':focus-visible': { outline: `2px solid ${tokens.colorStrokeFocus2}`, outlineOffset: '1px' },
    ':disabled': {
      cursor: 'not-allowed',
      color: tokens.colorNeutralForegroundDisabled,
      backgroundColor: tokens.colorNeutralBackground3,
      border: `1px solid ${tokens.colorNeutralStroke2}`,
    },
  },
  chipSelected: {
    backgroundColor: tokens.colorBrandBackground,
    color: tokens.colorNeutralForegroundOnBrand,
    border: `1px solid ${tokens.colorBrandBackground}`,
    ':hover': { backgroundColor: tokens.colorBrandBackgroundHover, border: `1px solid ${tokens.colorBrandBackgroundHover}` },
  },
  chipFew: { border: `1px solid ${tokens.colorPaletteMarigoldBorder2}` },
  chipOverbook: { border: `1px dashed ${tokens.colorNeutralStroke1}`, color: tokens.colorNeutralForeground3 },
  chipNote: { fontSize: tokens.fontSizeBase100, fontWeight: tokens.fontWeightRegular },
  notices: { display: 'flex', flexDirection: 'column', gap: '6px' },
  skeletonChips: { display: 'grid', gap: '6px', gridTemplateColumns: 'repeat(auto-fill, minmax(92px, 1fr))' },
});

const PART_ICON: Record<PartOfDay, ReactNode> = {
  Morning: <WeatherSunnyRegular />,
  Afternoon: <WeatherPartlyCloudyDayRegular />,
  Evening: <WeatherMoonRegular />,
};

export interface SlotPickerProps {
  practitionerId: string;
  practitionerName?: string;
  mode: VisitMode;
  date: IsoDate;
  onDateChange: (date: IsoDate) => void;
  value: SlotDto | null;
  onChange: (slot: SlotDto | null) => void;
  /** First bookable date (default today). */
  minDate?: IsoDate;
  /** Last bookable date (for example the end of a waitlist request). */
  maxDate?: IsoDate;
  /** Days shown in the date strip (the slot request is capped at 31 days by the server). */
  days?: number;
  /** Staff with `scheduling.appointments.manage` may put a patient into a full slot. */
  allowOverbook?: boolean;
  /** Highlights the group matching the patient's preferred part of the day. */
  preferredSession?: PreferredSession;
  /** The appointment's current time when rescheduling (shown, not selectable). */
  currentStartsAt?: string | null;
  /** Jump to the first day with a free slot when the doctor or mode changes (default on). */
  autoAdvance?: boolean;
  /** Select this slot once it has loaded (for example a free slot clicked on the calendar). */
  preselectStartsAt?: string;
}

/**
 * Date strip + slot chips grouped into morning, afternoon and evening. Full slots stay visible (the desk can see the
 * day is busy and, with permission, overbook); leave and blocked time are explained instead of silently missing.
 */
export function SlotPicker({
  practitionerId,
  practitionerName,
  mode,
  date,
  onDateChange,
  value,
  onChange,
  minDate: minBookable,
  maxDate: maxBookable,
  days = 14,
  allowOverbook = false,
  preferredSession = 'Any',
  currentStartsAt,
  autoAdvance = true,
  preselectStartsAt,
}: SlotPickerProps) {
  const styles = useStyles();
  const today = todayIso();
  const first = minBookable && minBookable > today ? minBookable : today;
  const windowSize = Math.max(1, Math.min(days, maxBookable ? daysBetween(first, maxBookable) + 1 : days));
  const [windowStart, setWindowStart] = useState<IsoDate>(() => clampWindow(date, first, maxBookable, windowSize));
  const windowEnd = addDays(windowStart, windowSize - 1);
  const stripNavigation = useArrowNavigationGroup({ axis: 'horizontal' });
  const selectedChip = useRef<HTMLButtonElement | null>(null);

  // Keep the strip showing the selected date when it changes from outside (calendar click, auto-advance).
  if (date < windowStart || date > windowEnd) {
    const next = clampWindow(date, first, maxBookable, windowSize);
    // A date outside the bookable range cannot be shown; only move when the window actually changes.
    if (next !== windowStart) {
      setWindowStart(next);
    }
  }

  useEffect(() => {
    selectedChip.current?.scrollIntoView({ block: 'nearest', inline: 'nearest' });
  }, [date, windowStart]);

  const slotsQuery = useQuery(schedulingQueries.slots({ practitionerId, from: windowStart, to: windowEnd }));
  const exceptionsQuery = useQuery({ ...schedulingQueries.exceptions({ practitionerId, from: windowStart, to: windowEnd }), retry: false });

  const allSlots = useMemo(() => slotsQuery.data?.find((d) => d.practitionerId === practitionerId)?.slots ?? [], [slotsQuery.data, practitionerId]);
  const modeSlots = useMemo(() => slotsForMode(allSlots, mode), [allSlots, mode]);
  const summaries = useMemo(() => summarizeDays(dateRange(windowStart, windowSize), modeSlots), [windowStart, windowSize, modeSlots]);
  const exceptions = useMemo(() => exceptionsQuery.data ?? [], [exceptionsQuery.data]);
  const daySlots = useMemo(() => modeSlots.filter((slot) => slot.date === date), [modeSlots, date]);
  const groups = useMemo(() => groupSlotsByPartOfDay(daySlots), [daySlots]);
  const restrictions = useMemo(() => restrictionsOn(date, exceptions, practitionerId), [date, exceptions, practitionerId]);
  const otherModeHasSlots = useMemo(() => allSlots.some((slot) => slot.date === date) && daySlots.length === 0, [allSlots, daySlots, date]);

  // A slot chosen elsewhere (calendar) is selected once, as soon as it is known to exist.
  const preselected = useRef<string | null>(null);
  useEffect(() => {
    if (!preselectStartsAt || preselected.current === preselectStartsAt || !slotsQuery.data) {
      return;
    }
    preselected.current = preselectStartsAt;
    const match = modeSlots.find((slot) => sameInstant(slot.startsAt, preselectStartsAt));
    if (match && (match.available > 0 || allowOverbook)) {
      onChange(match);
    }
  }, [preselectStartsAt, slotsQuery.data, modeSlots, allowOverbook, onChange]);

  // Fewest clicks: when the doctor or visit mode changes, move to the first day that still has a free slot.
  const advancedFor = useRef<string | null>(null);
  useEffect(() => {
    if (!autoAdvance || !slotsQuery.data || preselectStartsAt) {
      return;
    }
    const key = `${practitionerId}|${mode}`;
    if (advancedFor.current === key) {
      return;
    }
    advancedFor.current = key;
    const selected = summaries.find((d) => d.date === date);
    if (!selected || selected.free === 0) {
      const next = firstDayWithFreeSlots(summaries);
      if (next && next !== date) {
        onDateChange(next);
      }
    }
  }, [autoAdvance, slotsQuery.data, preselectStartsAt, practitionerId, mode, summaries, date, onDateChange]);

  // A chosen slot that no longer fits (mode switched, slot vanished after a refresh) is cleared.
  useEffect(() => {
    if (value && slotsQuery.data && !modeSlots.some((slot) => sameInstant(slot.startsAt, value.startsAt))) {
      onChange(null);
    }
  }, [value, modeSlots, slotsQuery.data, onChange]);

  const canGoBack = windowStart > first;
  const canGoForward = !maxBookable || windowEnd < maxBookable;
  const shift = (direction: -1 | 1) => {
    const start = clampWindow(addDays(windowStart, direction * windowSize), first, maxBookable, windowSize);
    setWindowStart(start);
    onDateChange(start);
  };

  const nextFree = firstDayWithFreeSlots(summaries.filter((d) => d.date > date));

  return (
    <div className={styles.root}>
      <div className={styles.toolbar}>
        <Text weight="semibold">{formatDayLong(date)}</Text>
        <Field label="Go to date" orientation="horizontal" size="small">
          <Input
            type="date"
            size="small"
            value={date}
            min={first}
            max={maxBookable}
            onChange={(_, d) => {
              if (isIsoDate(d.value) && d.value >= first && (!maxBookable || d.value <= maxBookable)) {
                onDateChange(d.value);
              }
            }}
          />
        </Field>
      </div>

      <div className={styles.stripRow}>
        <Button appearance="subtle" icon={<ChevronLeftRegular />} aria-label="Earlier dates" disabled={!canGoBack} onClick={() => shift(-1)} />
        <div className={styles.strip} role="group" aria-label="Choose a date" {...stripNavigation}>
          {summaries.map((day) => {
            const parts = dayParts(day.date);
            const selected = day.date === date;
            const dayRestrictions = restrictionsOn(day.date, exceptions, practitionerId);
            const leave = isOnLeaveAllDay(dayRestrictions);
            const status = slotsQuery.isLoading ? '…' : leave ? 'On leave' : day.total === 0 ? 'No OPD' : day.free === 0 ? 'Full' : `${day.free} free`;
            return (
              <button
                key={day.date}
                type="button"
                ref={selected ? selectedChip : undefined}
                aria-pressed={selected}
                aria-label={`${formatDayLong(day.date)}: ${status}`}
                className={mergeClasses(styles.dateChip, selected && styles.dateChipSelected, !selected && day.free === 0 && styles.dateChipQuiet)}
                onClick={() => onDateChange(day.date)}
              >
                <span className={styles.dateWeekday}>{day.date === today ? 'Today' : parts.weekday}</span>
                <span className={styles.dateDay}>{parts.day}</span>
                <span className={styles.dateWeekday}>{parts.month}</span>
                <span className={mergeClasses(styles.dateStatus, selected ? styles.onBrand : leave ? styles.statusLeave : day.free > 0 ? styles.statusFree : undefined)}>{status}</span>
              </button>
            );
          })}
        </div>
        <Button appearance="subtle" icon={<ChevronRightRegular />} aria-label="Later dates" disabled={!canGoForward} onClick={() => shift(1)} />
      </div>

      {restrictions.length > 0 && <Restrictions restrictions={restrictions} practitionerName={practitionerName} />}

      {slotsQuery.error ? (
        <ErrorState error={slotsQuery.error} onRetry={() => void slotsQuery.refetch()} title="Could not load the slots" />
      ) : slotsQuery.isLoading ? (
        <div className={styles.skeletonChips} aria-busy="true" aria-label="Loading slots">
          {Array.from({ length: 12 }, (_, i) => (
            <Skeleton key={i}>
              <SkeletonItem style={{ height: 44 }} />
            </Skeleton>
          ))}
        </div>
      ) : groups.length === 0 ? (
        <EmptyState
          title={isOnLeaveAllDay(restrictions) ? 'The doctor is on leave' : otherModeHasSlots ? (mode === 'Telehealth' ? 'No video slots on this day' : 'Only video slots on this day') : 'No OPD session on this day'}
          description={nextFree ? `The next day with a free slot is ${formatDayShort(nextFree)}.` : 'Try later dates, or add the patient to the waitlist.'}
          action={
            nextFree ? (
              <Button appearance="primary" onClick={() => onDateChange(nextFree)}>
                Go to {formatDayShort(nextFree)}
              </Button>
            ) : undefined
          }
        />
      ) : (
        groups.map((group) => (
          <section key={group.part} className={styles.group} aria-label={`${group.part} slots`}>
            <div className={styles.groupHeader}>
              <span className={styles.groupIcon} aria-hidden>
                {PART_ICON[group.part]}
              </span>
              <Text weight="semibold">{group.part}</Text>
              <Caption1 className={styles.muted}>
                {group.free} of {group.slots.length} free
              </Caption1>
              {preferredSession !== 'Any' && preferredSession === group.part && (
                <Badge appearance="tint" color="brand" size="small">
                  Preferred by patient
                </Badge>
              )}
            </div>
            <div className={styles.chips}>
              {group.slots.map((slot) => {
                const state = slotState(slot);
                const selected = sameInstant(value?.startsAt, slot.startsAt);
                const current = sameInstant(currentStartsAt, slot.startsAt);
                const full = state === 'full';
                const disabled = current || (full && !allowOverbook);
                const label = formatClock(slot.localStart);
                const note = current ? 'Current time' : full ? (allowOverbook ? 'Full · overbook' : 'Full') : slot.capacity > 1 ? `${slot.available} of ${slot.capacity} left` : null;
                const chip = (
                  <button
                    key={slot.startsAt}
                    type="button"
                    aria-pressed={selected}
                    aria-label={`${label}${note ? `, ${note}` : ''}`}
                    disabled={disabled}
                    className={mergeClasses(
                      styles.chip,
                      state === 'few' && styles.chipFew,
                      full && allowOverbook && !current && styles.chipOverbook,
                      selected && styles.chipSelected,
                    )}
                    onClick={() => onChange(selected ? null : slot)}
                  >
                    <span>{label}</span>
                    {note && <span className={styles.chipNote}>{note}</span>}
                  </button>
                );
                return full && allowOverbook && !current ? (
                  <Tooltip key={slot.startsAt} content="This slot is full. Choosing it books one extra patient (overbooking)." relationship="description">
                    {chip}
                  </Tooltip>
                ) : (
                  chip
                );
              })}
            </div>
          </section>
        ))
      )}
    </div>
  );
}

function Restrictions({ restrictions, practitionerName }: { restrictions: DayRestriction[]; practitionerName?: string }) {
  const styles = useStyles();
  const who = practitionerName ?? 'The doctor';
  return (
    <div className={styles.notices}>
      {restrictions.map((r, index) => (
        <MessageBar key={index} intent={r.kind === 'leave' ? 'error' : 'warning'}>
          <MessageBarBody>
            {r.kind === 'leave' ? `${who} is on leave` : 'Not available'}
            {r.allDay ? '' : ` from ${formatClock(r.start)} to ${formatClock(r.end)}`}: {r.reason}
          </MessageBarBody>
        </MessageBar>
      ))}
    </div>
  );
}

/** Start of a strip window that contains `date` and stays inside the bookable range. */
function clampWindow(date: IsoDate, first: IsoDate, last: IsoDate | undefined, size: number): IsoDate {
  let start = maxDate(date, first);
  if (last) {
    start = minDate(start, maxDate(first, addDays(last, -(size - 1))));
  }
  // A date inside the first window keeps the strip starting at the first bookable day (today), so "Today" stays visible.
  return daysBetween(first, start) > 0 && daysBetween(first, date) < size ? first : start;
}
