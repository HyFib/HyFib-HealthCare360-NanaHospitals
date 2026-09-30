import { Badge, Caption1, makeStyles, mergeClasses, Text, tokens, Tooltip } from '@fluentui/react-components';
import {
  ArrowExitRegular,
  BroomRegular,
  CalendarClockRegular,
  CheckmarkCircleRegular,
  ClockAlarmRegular,
  EmojiSadRegular,
  GavelRegular,
  LockClosedRegular,
  PersonRegular,
  PersonWalkingRegular,
  PillRegular,
  QuestionCircleRegular,
  ShieldKeyholeRegular,
  TimerRegular,
  WarningRegular,
  WrenchRegular,
} from '@fluentui/react-icons';
import { formatTime, genderShort } from '@hc360/core';
import type { ReactElement } from 'react';
import type { BedBoardItem } from './api';
import { BED_LEGEND, bedAlertInfo, bedStateInfo, sortAlerts, type BedIconKey, type BedTone } from './bedState';
import { formatDuration, minutesSince } from './time';

const STATE_ICONS: Record<BedIconKey, ReactElement> = {
  vacant: <CheckmarkCircleRegular />,
  occupied: <PersonRegular />,
  reserved: <CalendarClockRegular />,
  cleaning: <BroomRegular />,
  blocked: <LockClosedRegular />,
  maintenance: <WrenchRegular />,
  unknown: <QuestionCircleRegular />,
};

const ALERT_ICONS: Record<string, ReactElement> = {
  Mlc: <GavelRegular />,
  Restricted: <ShieldKeyholeRegular />,
  HighFallRisk: <PersonWalkingRegular />,
  PressureInjuryRisk: <WarningRegular />,
  SeverePain: <EmojiSadRegular />,
  MedicationOverdue: <PillRegular />,
  DischargeInitiated: <ArrowExitRegular />,
  DischargeDueToday: <CalendarClockRegular />,
  ReservationExpired: <ClockAlarmRegular />,
  CleaningDelayed: <TimerRegular />,
};

const useStyles = makeStyles({
  tile: {
    appearance: 'none',
    margin: 0,
    fontFamily: 'inherit',
    fontSize: 'inherit',
    lineHeight: 'inherit',
    textAlign: 'start',
    width: '100%',
    boxSizing: 'border-box',
    display: 'flex',
    flexDirection: 'column',
    gap: '4px',
    minHeight: '118px',
    padding: '10px 12px 10px 14px',
    borderRadius: tokens.borderRadiusLarge,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderLeftWidth: '6px',
    cursor: 'pointer',
    position: 'relative',
    color: tokens.colorNeutralForeground1,
    transitionProperty: 'box-shadow, transform',
    transitionDuration: tokens.durationFaster,
    ':hover': { boxShadow: tokens.shadow8, transform: 'translateY(-1px)' },
    ':focus-visible': { outline: `3px solid ${tokens.colorStrokeFocus2}`, outlineOffset: '2px' },
    '@media (prefers-reduced-motion: reduce)': { transitionDuration: '0s', ':hover': { transform: 'none' } },
  },
  green: {
    backgroundColor: tokens.colorPaletteGreenBackground1,
    borderLeftColor: tokens.colorPaletteGreenBorderActive,
  },
  blue: { backgroundColor: tokens.colorNeutralBackground1, borderLeftColor: tokens.colorPaletteBlueBorderActive },
  marigold: {
    backgroundColor: tokens.colorPaletteMarigoldBackground1,
    borderLeftColor: tokens.colorPaletteMarigoldBorderActive,
  },
  teal: { backgroundColor: tokens.colorNeutralBackground1, borderLeftColor: tokens.colorPaletteLightTealBorderActive },
  red: { backgroundColor: tokens.colorPaletteRedBackground1, borderLeftColor: tokens.colorPaletteRedBorderActive },
  grape: { backgroundColor: tokens.colorNeutralBackground1, borderLeftColor: tokens.colorPaletteGrapeBorderActive },
  neutral: { backgroundColor: tokens.colorNeutralBackground1, borderLeftColor: tokens.colorNeutralStroke1 },
  selected: { outline: `3px solid ${tokens.colorBrandStroke1}`, outlineOffset: '1px' },
  header: { display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: '6px' },
  label: {
    fontSize: tokens.fontSizeBase400,
    fontWeight: tokens.fontWeightBold,
    fontFamily: tokens.fontFamilyNumeric,
    letterSpacing: '0.3px',
  },
  chip: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: '4px',
    padding: '1px 8px',
    borderRadius: tokens.borderRadiusCircular,
    fontSize: tokens.fontSizeBase200,
    fontWeight: tokens.fontWeightSemibold,
    whiteSpace: 'nowrap',
    '& svg': { fontSize: '14px' },
  },
  chipGreen: { backgroundColor: tokens.colorPaletteGreenBackground2, color: tokens.colorPaletteGreenForeground2 },
  chipBlue: { backgroundColor: tokens.colorPaletteBlueBackground2, color: tokens.colorPaletteBlueForeground2 },
  chipMarigold: {
    backgroundColor: tokens.colorPaletteMarigoldBackground2,
    color: tokens.colorPaletteMarigoldForeground2,
  },
  chipTeal: {
    backgroundColor: tokens.colorPaletteLightTealBackground2,
    color: tokens.colorPaletteLightTealForeground2,
  },
  chipRed: { backgroundColor: tokens.colorPaletteRedBackground2, color: tokens.colorPaletteRedForeground2 },
  chipGrape: { backgroundColor: tokens.colorPaletteGrapeBackground2, color: tokens.colorPaletteGrapeForeground2 },
  chipNeutral: { backgroundColor: tokens.colorNeutralBackground3, color: tokens.colorNeutralForeground2 },
  name: { fontWeight: tokens.fontWeightSemibold, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' },
  muted: { color: tokens.colorNeutralForeground3, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' },
  alerts: { display: 'flex', gap: '4px', flexWrap: 'wrap', marginTop: 'auto' },
  features: { display: 'flex', gap: '4px', flexWrap: 'wrap' },
  feature: {
    fontSize: tokens.fontSizeBase100,
    padding: '0 4px',
    borderRadius: tokens.borderRadiusSmall,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    color: tokens.colorNeutralForeground2,
  },
  legend: { display: 'flex', gap: '8px 16px', flexWrap: 'wrap', alignItems: 'center' },
  legendItem: { display: 'inline-flex', alignItems: 'center', gap: '6px' },
  legendButton: {
    appearance: 'none',
    margin: 0,
    padding: 0,
    border: 'none',
    backgroundColor: 'transparent',
    fontFamily: 'inherit',
    fontSize: 'inherit',
    lineHeight: 'inherit',
    color: 'inherit',
    cursor: 'pointer',
    display: 'inline-flex',
    alignItems: 'center',
    gap: '6px',
    borderRadius: tokens.borderRadiusCircular,
    ':focus-visible': { outline: `2px solid ${tokens.colorStrokeFocus2}` },
  },
  legendActive: { outline: `2px solid ${tokens.colorBrandStroke1}`, outlineOffset: '2px' },
});

type Styles = ReturnType<typeof useStyles>;

function toneClass(styles: Styles, tone: BedTone): string {
  return styles[tone];
}

function chipClass(styles: Styles, tone: BedTone): string {
  switch (tone) {
    case 'green':
      return styles.chipGreen;
    case 'blue':
      return styles.chipBlue;
    case 'marigold':
      return styles.chipMarigold;
    case 'teal':
      return styles.chipTeal;
    case 'red':
      return styles.chipRed;
    case 'grape':
      return styles.chipGrape;
    default:
      return styles.chipNeutral;
  }
}

/** The state pill: icon + word, so the state is never told by colour alone. */
export function BedStateChip({ status, count }: { status: string; count?: number }) {
  const styles = useStyles();
  const info = bedStateInfo(status);
  return (
    <span className={mergeClasses(styles.chip, chipClass(styles, info.tone))}>
      {STATE_ICONS[info.icon]}
      {info.label}
      {count !== undefined && <span aria-hidden>· {count}</span>}
    </span>
  );
}

/** Legend of bed states; with `onToggle` each entry filters the board (Enter/Space on keyboard). */
export function BedLegend({
  counts,
  active,
  onToggle,
}: {
  counts?: Partial<Record<string, number>>;
  active?: string | null;
  onToggle?: (status: string) => void;
}) {
  const styles = useStyles();
  return (
    <div className={styles.legend} role={onToggle ? 'toolbar' : 'list'} aria-label="Bed states">
      {BED_LEGEND.map((state) => {
        const count = counts?.[state.status];
        if (!onToggle) {
          return (
            <span key={state.status} className={styles.legendItem} role="listitem" title={state.description}>
              <BedStateChip status={state.status} count={count} />
            </span>
          );
        }
        const pressed = active === state.status;
        return (
          <Tooltip
            key={state.status}
            content={`${state.description}. ${pressed ? 'Show all beds' : `Show only ${state.label.toLowerCase()} beds`}`}
            relationship="description"
          >
            <button
              type="button"
              className={mergeClasses(styles.legendButton, pressed && styles.legendActive)}
              aria-pressed={pressed}
              onClick={() => onToggle(state.status)}
            >
              <BedStateChip status={state.status} count={count} />
            </button>
          </Tooltip>
        );
      })}
    </div>
  );
}

/** Alert badges, most serious first; each has an icon, a word and a tooltip with the reason. */
export function BedAlertBadges({ alerts, max = 3 }: { alerts: readonly string[]; max?: number }) {
  const styles = useStyles();
  if (alerts.length === 0) {
    return null;
  }
  const sorted = sortAlerts(alerts);
  const shown = sorted.slice(0, max);
  const hidden = sorted.slice(max);
  return (
    <span className={styles.alerts}>
      {shown.map((alert) => {
        const info = bedAlertInfo(alert);
        return (
          <Tooltip key={alert} content={info.description} relationship="description">
            <Badge
              size="small"
              appearance="tint"
              color={info.severity === 'danger' ? 'danger' : info.severity === 'warning' ? 'warning' : 'informative'}
              icon={ALERT_ICONS[alert]}
            >
              {info.label}
            </Badge>
          </Tooltip>
        );
      })}
      {hidden.length > 0 && (
        <Tooltip content={hidden.map((a) => bedAlertInfo(a).label).join(', ')} relationship="description">
          <Badge size="small" appearance="outline" color="informative">
            +{hidden.length}
          </Badge>
        </Tooltip>
      )}
    </span>
  );
}

/** Oxygen / ventilator / monitor points of a bed as short labelled chips. */
export function BedFeatures({
  oxygen,
  ventilator,
  monitor,
}: {
  oxygen: boolean;
  ventilator: boolean;
  monitor: boolean;
}) {
  const styles = useStyles();
  if (!oxygen && !ventilator && !monitor) {
    return null;
  }
  return (
    <span className={styles.features}>
      {oxygen && (
        <span className={styles.feature} title="Oxygen point">
          O₂
        </span>
      )}
      {ventilator && (
        <span className={styles.feature} title="Ventilator">
          Ventilator
        </span>
      )}
      {monitor && (
        <span className={styles.feature} title="Cardiac monitor">
          Monitor
        </span>
      )}
    </span>
  );
}

function statusLine(bed: BedBoardItem, now: number): string | null {
  switch (bed.status) {
    case 'Vacant':
      return 'Ready for a patient';
    case 'Reserved':
      return bed.reservedUntil ? `Held until ${formatTime(bed.reservedUntil)}` : 'Reserved';
    case 'Cleaning':
      return bed.statusChangedAt
        ? `Waiting ${formatDuration(minutesSince(bed.statusChangedAt, now))}`
        : 'Waiting for cleaning';
    case 'Blocked':
    case 'Maintenance':
      return bed.statusReason ?? null;
    default:
      return null;
  }
}

function describe(bed: BedBoardItem): string {
  const state = bedStateInfo(bed.status).label.toLowerCase();
  const who = bed.occupant
    ? `, ${bed.occupant.patientName}, ${bed.occupant.age ?? 'age not known'} ${bed.occupant.gender}, day ${bed.occupant.lengthOfStayDays}`
    : '';
  const alerts = bed.alerts.length ? `. Alerts: ${bed.alerts.map((a) => bedAlertInfo(a).label).join(', ')}` : '';
  return `Bed ${bed.label}, ${state}${who}${alerts}`;
}

/** One bed on the board. A real button: Tab reaches it, Enter or a tap opens its actions. */
export function BedTile({
  bed,
  now,
  selected,
  onOpen,
}: {
  bed: BedBoardItem;
  now: number;
  selected?: boolean;
  onOpen: (bed: BedBoardItem) => void;
}) {
  const styles = useStyles();
  const info = bedStateInfo(bed.status);
  const line = statusLine(bed, now);
  const occupant = bed.occupant;

  return (
    <button
      type="button"
      className={mergeClasses(styles.tile, toneClass(styles, info.tone), selected && styles.selected)}
      onClick={() => onOpen(bed)}
      aria-label={describe(bed)}
      aria-haspopup="dialog"
    >
      <span className={styles.header}>
        <span className={styles.label}>{bed.label}</span>
        <BedStateChip status={bed.status} />
      </span>
      {bed.roomNumber && <Caption1 className={styles.muted}>Room {bed.roomNumber}</Caption1>}
      {occupant ? (
        <>
          <Text className={styles.name}>{occupant.patientName}</Text>
          <Caption1 className={styles.muted}>
            {occupant.age ?? '—'} / {genderShort[occupant.gender] ?? occupant.gender.charAt(0)} · Day{' '}
            {occupant.lengthOfStayDays}
            {occupant.consultantName ? ` · ${occupant.consultantName}` : ''}
          </Caption1>
        </>
      ) : (
        line && <Caption1 className={styles.muted}>{line}</Caption1>
      )}
      <BedFeatures oxygen={bed.hasOxygen} ventilator={bed.hasVentilator} monitor={bed.hasMonitor} />
      <BedAlertBadges alerts={bed.alerts} />
    </button>
  );
}
