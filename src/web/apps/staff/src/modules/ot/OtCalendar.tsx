import { Button, Caption1, makeStyles, mergeClasses, Text, tokens, Tooltip } from '@fluentui/react-components';
import { AddRegular, WarningRegular } from '@fluentui/react-icons';
import { formatTime } from '@hc360/core';
import { EmptyState } from '@hc360/ui';
import type { MouseEvent } from 'react';
import type { OtCalendar, OtCalendarBooking, OtCalendarTheatre } from './api';
import { anaesthesiaShort, sideShort, surgeryStatusLabel, theatreTypeLabel } from './labels';
import { dayWindow, placeBlocks } from './logic';
import { WhoProgress } from './OtBadges';
import { addDays, dayLabel, formatClock, istDate, minutesIntoIstDay } from './shared/datetime';

const HOUR_HEIGHT = 64;
const SNAP_MINUTES = 15;

const useStyles = makeStyles({
  scroller: {
    overflow: 'auto',
    maxHeight: 'calc(100vh - 250px)',
    minHeight: '320px',
    borderTop: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  grid: { display: 'grid', minWidth: 'fit-content' },
  headCell: {
    position: 'sticky',
    top: 0,
    zIndex: 3,
    backgroundColor: tokens.colorNeutralBackground1,
    borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
    padding: '8px 10px',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: '6px',
    minWidth: 0,
  },
  corner: { left: 0, zIndex: 4 },
  headText: { display: 'flex', flexDirection: 'column', minWidth: 0 },
  muted: { color: tokens.colorNeutralForeground3 },
  gutter: {
    position: 'sticky',
    left: 0,
    zIndex: 2,
    backgroundColor: tokens.colorNeutralBackground1,
    borderRight: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  hour: {
    position: 'absolute',
    right: '6px',
    transform: 'translateY(-50%)',
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase100,
  },
  column: {
    position: 'relative',
    borderRight: `1px solid ${tokens.colorNeutralStroke3}`,
    backgroundImage: `repeating-linear-gradient(to bottom, ${tokens.colorNeutralStroke3} 0, ${tokens.colorNeutralStroke3} 1px, transparent 1px, transparent ${HOUR_HEIGHT}px)`,
  },
  bookable: { cursor: 'copy', ':hover': { backgroundColor: tokens.colorSubtleBackgroundHover } },
  block: {
    position: 'absolute',
    boxSizing: 'border-box',
    overflow: 'hidden',
    textAlign: 'start',
    display: 'flex',
    flexDirection: 'column',
    gap: '1px',
    padding: '3px 6px',
    borderRadius: tokens.borderRadiusMedium,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderLeftWidth: '5px',
    fontSize: tokens.fontSizeBase200,
    lineHeight: tokens.lineHeightBase200,
    color: tokens.colorNeutralForeground1,
    cursor: 'pointer',
    boxShadow: tokens.shadow2,
    ':hover': { boxShadow: tokens.shadow8, zIndex: 1 },
    ':focus-visible': { outline: `2px solid ${tokens.colorStrokeFocus2}`, zIndex: 1 },
  },
  Scheduled: { backgroundColor: tokens.colorBrandBackground2 },
  InPreOp: { backgroundColor: tokens.colorPaletteMarigoldBackground2 },
  InProgress: { backgroundColor: tokens.colorPaletteGreenBackground2 },
  Completed: { backgroundColor: tokens.colorNeutralBackground3, color: tokens.colorNeutralForeground2 },
  other: { backgroundColor: tokens.colorNeutralBackground1 },
  Elective: { borderLeftColor: tokens.colorBrandStroke1 },
  Urgent: { borderLeftColor: tokens.colorPaletteDarkOrangeBorder2 },
  Emergency: { borderLeftColor: tokens.colorPaletteRedBorder2 },
  line: { display: 'flex', gap: '4px', alignItems: 'baseline', minWidth: 0, whiteSpace: 'nowrap' },
  strong: { fontWeight: tokens.fontWeightSemibold },
  truncate: { overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', minWidth: 0 },
  emergencyText: { color: tokens.colorPaletteRedForeground1, fontWeight: tokens.fontWeightBold },
  now: {
    position: 'absolute',
    left: 0,
    right: 0,
    height: '2px',
    backgroundColor: tokens.colorPaletteRedBackground3,
    zIndex: 2,
    pointerEvents: 'none',
  },
  nowLabel: {
    position: 'absolute',
    left: '2px',
    transform: 'translateY(-50%)',
    fontSize: tokens.fontSizeBase100,
    color: tokens.colorNeutralForegroundOnBrand,
    backgroundColor: tokens.colorPaletteRedBackground3,
    borderRadius: tokens.borderRadiusSmall,
    padding: '0 3px',
  },
  weekCell: {
    borderRight: `1px solid ${tokens.colorNeutralStroke3}`,
    borderBottom: `1px solid ${tokens.colorNeutralStroke3}`,
    padding: '6px',
    display: 'flex',
    flexDirection: 'column',
    gap: '4px',
    minHeight: '72px',
    minWidth: 0,
  },
  weekTheatre: {
    position: 'sticky',
    left: 0,
    zIndex: 2,
    backgroundColor: tokens.colorNeutralBackground1,
    borderRight: `1px solid ${tokens.colorNeutralStroke2}`,
    borderBottom: `1px solid ${tokens.colorNeutralStroke3}`,
    padding: '8px 10px',
  },
  today: { backgroundColor: tokens.colorBrandBackground2 },
  chip: {
    position: 'relative',
    padding: '2px 6px',
    fontSize: tokens.fontSizeBase200,
    minHeight: '40px',
  },
  dayButton: { justifyContent: 'flex-start', minWidth: 0, paddingLeft: '4px', paddingRight: '4px' },
});

function blockLabel(b: OtCalendarBooking): string {
  const side = sideShort(b.laterality);
  return [
    `${formatTime(b.start)} to ${formatTime(b.end)}`,
    b.patientName,
    `${b.procedureName}${side ? `, ${side.toLowerCase()} side` : ''}`,
    b.surgeonName ? `surgeon ${b.surgeonName}` : null,
    surgeryStatusLabel[b.status],
    b.priority === 'Elective' ? null : `${b.priority} priority`,
  ]
    .filter(Boolean)
    .join(', ');
}

function BookingContent({ booking, compact }: { booking: OtCalendarBooking; compact: boolean }) {
  const styles = useStyles();
  const side = sideShort(booking.laterality);
  return (
    <>
      <span className={styles.line}>
        <span className={styles.strong}>
          {formatTime(booking.start)}–{formatTime(booking.end)}
        </span>
        {booking.priority === 'Emergency' && <span className={styles.emergencyText}>EMERGENCY</span>}
        {booking.priority === 'Urgent' && <span className={styles.strong}>Urgent</span>}
        <span className={mergeClasses(styles.truncate, styles.muted)}>{surgeryStatusLabel[booking.status]}</span>
      </span>
      <span className={mergeClasses(styles.line)}>
        <span className={mergeClasses(styles.strong, styles.truncate)}>{booking.patientName}</span>
        <span className={styles.muted}>
          {booking.age ?? '—'}/{booking.gender.charAt(0)}
        </span>
      </span>
      {!compact && (
        <>
          <span className={styles.truncate}>
            {booking.procedureName}
            {side && <span className={styles.strong}> · {side}</span>}
          </span>
          <span className={mergeClasses(styles.truncate, styles.muted)}>
            {booking.surgeonName ?? 'Surgeon not set'} · {anaesthesiaShort[booking.anaesthesiaType]}
          </span>
          <span>
            <WhoProgress
              signInDone={booking.signInDone}
              timeOutDone={booking.timeOutDone}
              signOutDone={booking.signOutDone}
              compact
            />
          </span>
        </>
      )}
    </>
  );
}

function statusClass(styles: ReturnType<typeof useStyles>, booking: OtCalendarBooking): string {
  switch (booking.status) {
    case 'Scheduled':
      return styles.Scheduled;
    case 'InPreOp':
      return styles.InPreOp;
    case 'InProgress':
      return styles.InProgress;
    case 'Completed':
      return styles.Completed;
    default:
      return styles.other;
  }
}

export interface DayBoardProps {
  calendar: OtCalendar;
  date: string;
  now: number;
  canBook: boolean;
  onOpen: (booking: OtCalendarBooking) => void;
  /** `start` is an IST "yyyy-MM-ddTHH:mm" value. */
  onBookSlot: (theatre: OtCalendarTheatre, start: string) => void;
}

/**
 * One day, theatres side by side on a time axis. Cases are blocks coloured by status with the priority on the left
 * edge; the red line is "now". Clicking an empty time books that theatre (keyboard users use the + button).
 */
export function DayBoard({ calendar, date, now, canBook, onOpen, onBookSlot }: DayBoardProps) {
  const styles = useStyles();
  const theatres = calendar.theatres;
  if (theatres.length === 0) {
    return (
      <EmptyState
        title="No operation theatres are set up"
        description="Add theatres under OT → Theatres to start booking surgeries."
      />
    );
  }
  const range = dayWindow(
    theatres.flatMap((t) => t.bookings),
    date,
  );
  const height = ((range.end - range.start) / 60) * HOUR_HEIGHT;
  const hours = Array.from({ length: (range.end - range.start) / 60 + 1 }, (_, i) => range.start + i * 60);
  const nowIso = new Date(now).toISOString();
  const nowMinute = istDate(nowIso) === date ? minutesIntoIstDay(nowIso, date) : null;
  const showNow = nowMinute !== null && nowMinute >= range.start && nowMinute <= range.end;
  const columns = `64px repeat(${theatres.length}, minmax(180px, 1fr))`;

  const bookAt = (theatre: OtCalendarTheatre, event: MouseEvent<HTMLDivElement>) => {
    if (!canBook || event.target !== event.currentTarget) {
      return;
    }
    const rect = event.currentTarget.getBoundingClientRect();
    const minute =
      range.start + Math.floor(((event.clientY - rect.top) / HOUR_HEIGHT) * (60 / SNAP_MINUTES)) * SNAP_MINUTES;
    const hh = String(Math.floor(minute / 60)).padStart(2, '0');
    const mm = String(minute % 60).padStart(2, '0');
    onBookSlot(theatre, `${date}T${hh}:${mm}`);
  };

  return (
    <div className={styles.scroller}>
      <div className={styles.grid} style={{ gridTemplateColumns: columns }}>
        <div className={mergeClasses(styles.headCell, styles.corner)} />
        {theatres.map((theatre) => (
          <div key={theatre.theatreId} className={styles.headCell}>
            <div className={styles.headText}>
              <Text weight="semibold" truncate wrap={false}>
                {theatre.code} · {theatre.name}
              </Text>
              <Caption1 className={styles.muted}>
                {theatreTypeLabel[theatre.type]} · {theatre.bookings.length} case
                {theatre.bookings.length === 1 ? '' : 's'}
              </Caption1>
            </div>
            {canBook && (
              <Tooltip content={`Book ${theatre.code} on this day`} relationship="label">
                <Button
                  size="small"
                  appearance="subtle"
                  icon={<AddRegular />}
                  onClick={() => onBookSlot(theatre, `${date}T${formatHourInput(range.start)}`)}
                />
              </Tooltip>
            )}
          </div>
        ))}

        <div className={styles.gutter} style={{ position: 'sticky', height }} aria-hidden>
          <div style={{ position: 'relative', height }}>
            {hours.map((minute) => (
              <span key={minute} className={styles.hour} style={{ top: ((minute - range.start) / 60) * HOUR_HEIGHT }}>
                {formatClock(minute)}
              </span>
            ))}
            {showNow && (
              <span className={styles.nowLabel} style={{ top: (((nowMinute ?? 0) - range.start) / 60) * HOUR_HEIGHT }}>
                Now
              </span>
            )}
          </div>
        </div>

        {theatres.map((theatre) => {
          const placed = placeBlocks(theatre.bookings, date, range);
          return (
            <div
              key={theatre.theatreId}
              className={mergeClasses(styles.column, canBook && styles.bookable)}
              style={{ height }}
              onClick={(event) => bookAt(theatre, event)}
              title={canBook ? `Click an empty time to book ${theatre.code}` : undefined}
              role="group"
              aria-label={`${theatre.code} ${theatre.name}: ${theatre.bookings.length} cases`}
            >
              {showNow && (
                <div className={styles.now} style={{ top: (((nowMinute ?? 0) - range.start) / 60) * HOUR_HEIGHT }} />
              )}
              {placed.map((block) => {
                const top = ((block.startMinute - range.start) / 60) * HOUR_HEIGHT;
                const blockHeight = Math.max(26, ((block.endMinute - block.startMinute) / 60) * HOUR_HEIGHT - 2);
                const width = 100 / block.lanes;
                return (
                  <button
                    key={block.item.id}
                    type="button"
                    className={mergeClasses(styles.block, statusClass(styles, block.item), styles[block.item.priority])}
                    style={{
                      top,
                      height: blockHeight,
                      left: `calc(${block.lane * width}% + 3px)`,
                      width: `calc(${width}% - 6px)`,
                    }}
                    aria-label={blockLabel(block.item)}
                    onClick={() => onOpen(block.item)}
                  >
                    <BookingContent booking={block.item} compact={blockHeight < 70} />
                  </button>
                );
              })}
            </div>
          );
        })}
      </div>
    </div>
  );
}

function formatHourInput(minute: number): string {
  return `${String(Math.floor(minute / 60)).padStart(2, '0')}:${String(minute % 60).padStart(2, '0')}`;
}

export interface WeekBoardProps {
  calendar: OtCalendar;
  today: string;
  onOpen: (booking: OtCalendarBooking) => void;
  onOpenDay: (date: string) => void;
}

/** Monday–Sunday, one row per theatre: the week's lists at a glance. Click a day to open its time board. */
export function WeekBoard({ calendar, today, onOpen, onOpenDay }: WeekBoardProps) {
  const styles = useStyles();
  if (calendar.theatres.length === 0) {
    return (
      <EmptyState
        title="No operation theatres are set up"
        description="Add theatres under OT → Theatres to start booking surgeries."
      />
    );
  }
  const days = Array.from({ length: 7 }, (_, i) => addDays(calendar.from, i));
  return (
    <div className={styles.scroller}>
      <div className={styles.grid} style={{ gridTemplateColumns: `170px repeat(7, minmax(150px, 1fr))` }}>
        <div className={mergeClasses(styles.headCell, styles.corner)}>
          <Caption1 className={styles.muted}>Theatre</Caption1>
        </div>
        {days.map((day) => (
          <div key={day} className={mergeClasses(styles.headCell, day === today && styles.today)}>
            <Button
              appearance="transparent"
              className={styles.dayButton}
              onClick={() => onOpenDay(day)}
              aria-label={`Open ${dayLabel(day)}`}
            >
              <Text weight={day === today ? 'bold' : 'semibold'}>
                {dayLabel(day)}
                {day === today ? ' · Today' : ''}
              </Text>
            </Button>
          </div>
        ))}
        {calendar.theatres.map((theatre) => (
          <WeekRow key={theatre.theatreId} theatre={theatre} days={days} today={today} onOpen={onOpen} />
        ))}
      </div>
    </div>
  );
}

function WeekRow({
  theatre,
  days,
  today,
  onOpen,
}: {
  theatre: OtCalendarTheatre;
  days: string[];
  today: string;
  onOpen: (booking: OtCalendarBooking) => void;
}) {
  const styles = useStyles();
  return (
    <>
      <div className={styles.weekTheatre}>
        <Text weight="semibold" block>
          {theatre.code}
        </Text>
        <Caption1 className={styles.muted}>
          {theatre.name} · {theatreTypeLabel[theatre.type]}
        </Caption1>
      </div>
      {days.map((day) => {
        const cases = theatre.bookings.filter((b) => b.start !== null && istDate(b.start) === day);
        return (
          <div
            key={day}
            className={mergeClasses(styles.weekCell, day === today && styles.today)}
            role="group"
            aria-label={`${theatre.code}, ${dayLabel(day)}: ${cases.length} cases`}
          >
            {cases.length === 0 && <Caption1 className={styles.muted}>—</Caption1>}
            {cases.map((booking) => (
              <button
                key={booking.id}
                type="button"
                className={mergeClasses(
                  styles.block,
                  styles.chip,
                  statusClass(styles, booking),
                  styles[booking.priority],
                )}
                style={{ position: 'relative' }}
                aria-label={blockLabel(booking)}
                onClick={() => onOpen(booking)}
              >
                <span className={styles.line}>
                  <span className={styles.strong}>{formatTime(booking.start)}</span>
                  {booking.priority === 'Emergency' && (
                    <WarningRegular aria-hidden color={tokens.colorPaletteRedForeground1} />
                  )}
                  <span className={mergeClasses(styles.truncate, styles.muted)}>
                    {surgeryStatusLabel[booking.status]}
                  </span>
                </span>
                <span className={mergeClasses(styles.truncate, styles.strong)}>{booking.patientName}</span>
                <span className={styles.truncate}>
                  {booking.procedureName}
                  {sideShort(booking.laterality) ? ` · ${sideShort(booking.laterality)}` : ''}
                </span>
              </button>
            ))}
          </div>
        );
      })}
    </>
  );
}
