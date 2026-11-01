import { Caption1, makeStyles, mergeClasses, Text, tokens } from '@fluentui/react-components';
import {
  CheckmarkCircleRegular,
  CircleOffRegular,
  CircleRegular,
  DismissCircleRegular,
  RecordRegular,
  SubtractCircleRegular,
  type FluentIcon,
} from '@fluentui/react-icons';
import { formatDateTime, formatTime, HOSPITAL_TIME_ZONE } from '@hc360/core';

/** Where a stage of a journey stands (the server's `StageState`). */
export type PipelineStageState = 'Done' | 'Current' | 'Pending' | 'Skipped' | 'NotApplicable' | 'Stopped';

/** One stage of a patient's journey, as the server derives it from the records (`JourneyStage`). */
export interface PipelineStage {
  key: string;
  label: string;
  state: PipelineStageState;
  /** When the stage was reached; null before it is, and where nothing stores the time. */
  at: string | null;
  /** Minutes spent in the stage, on the current stage only. */
  minutesInStage?: number | null;
  /** A short note from the records: "Token GM-012", "₹350.00 due", "1 of 3 released". */
  detail?: string | null;
}

/** Each state has its own icon shape and its own words, so it never rests on colour alone. */
const STATES: Record<PipelineStageState, { icon: FluentIcon; words: string }> = {
  Done: { icon: CheckmarkCircleRegular, words: 'Done' },
  Current: { icon: RecordRegular, words: 'Now' },
  Pending: { icon: CircleRegular, words: 'Pending' },
  Skipped: { icon: SubtractCircleRegular, words: 'Skipped' },
  NotApplicable: { icon: CircleOffRegular, words: 'Not applicable' },
  Stopped: { icon: DismissCircleRegular, words: 'Stopped' },
};

const useStyles = makeStyles({
  // The full pipeline: stages in rows that wrap, so a phone (360 px) shows two to a row and never scrolls sideways.
  list: {
    display: 'grid',
    gridTemplateColumns: 'repeat(auto-fill, minmax(132px, 1fr))',
    gap: `${tokens.spacingVerticalM} ${tokens.spacingHorizontalXS}`,
    listStyleType: 'none',
    margin: 0,
    padding: 0,
  },
  stage: {
    display: 'flex',
    flexDirection: 'column',
    gap: '2px',
    minWidth: 0,
    paddingTop: tokens.spacingVerticalS,
    borderTop: `3px solid ${tokens.colorNeutralStroke2}`,
    overflowWrap: 'anywhere',
  },
  head: { display: 'flex', alignItems: 'flex-start', gap: tokens.spacingHorizontalXS },
  icon: { fontSize: '20px', flexShrink: 0 },
  muted: { color: tokens.colorNeutralForeground3 },

  // The compact pipeline of a board row: one icon a stage, the current one named with its time.
  compactList: {
    display: 'flex',
    flexWrap: 'wrap',
    alignItems: 'center',
    gap: '2px',
    listStyleType: 'none',
    margin: 0,
    padding: 0,
  },
  compactStage: { display: 'inline-flex', alignItems: 'center', minWidth: 0 },
  compactCurrent: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXXS,
    minWidth: 0,
    paddingLeft: tokens.spacingHorizontalXXS,
    paddingRight: tokens.spacingHorizontalS,
    borderRadius: tokens.borderRadiusCircular,
    backgroundColor: tokens.colorBrandBackground2,
    overflowWrap: 'anywhere',
  },
  srOnly: {
    position: 'absolute',
    width: '1px',
    height: '1px',
    overflow: 'hidden',
    clip: 'rect(0 0 0 0)',
    whiteSpace: 'nowrap',
  },

  // The icon and the stage's stretch of the line take the state's colour; the words stay in the text colour.
  iconDone: { color: tokens.colorPaletteGreenForeground1 },
  iconCurrent: { color: tokens.colorBrandForeground1 },
  iconStopped: { color: tokens.colorPaletteRedForeground1 },
  iconMuted: { color: tokens.colorNeutralForeground3 },
  lineDone: { borderTopColor: tokens.colorPaletteGreenBorder2 },
  lineCurrent: { borderTopColor: tokens.colorBrandStroke1 },
  lineStopped: { borderTopColor: tokens.colorPaletteRedBorder2 },
});

type PipelineStyles = ReturnType<typeof useStyles>;

function iconTone(styles: PipelineStyles, state: PipelineStageState): string {
  switch (state) {
    case 'Done':
      return styles.iconDone;
    case 'Current':
      return styles.iconCurrent;
    case 'Stopped':
      return styles.iconStopped;
    default:
      return styles.iconMuted;
  }
}

function lineTone(styles: PipelineStyles, state: PipelineStageState): string | undefined {
  switch (state) {
    case 'Done':
      return styles.lineDone;
    case 'Current':
      return styles.lineCurrent;
    case 'Stopped':
      return styles.lineStopped;
    default:
      return undefined;
  }
}

const istDay = new Intl.DateTimeFormat('en-CA', {
  timeZone: HOSPITAL_TIME_ZONE,
  year: 'numeric',
  month: '2-digit',
  day: '2-digit',
});

/** The time a stage was reached: the time alone today, the date with it on another day. */
export function formatStageTime(iso: string, now = Date.now()): string {
  return istDay.format(new Date(iso)) === istDay.format(new Date(now)) ? formatTime(iso) : formatDateTime(iso);
}

/** "25 min", "2 h 5 min", "3 d 4 h": how long the patient has been in a stage. */
export function formatStageDuration(minutes: number): string {
  if (minutes < 60) {
    return `${minutes} min`;
  }
  if (minutes < 24 * 60) {
    const rest = minutes % 60;
    return rest ? `${Math.floor(minutes / 60)} h ${rest} min` : `${Math.floor(minutes / 60)} h`;
  }
  const hours = Math.floor((minutes % (24 * 60)) / 60);
  return hours ? `${Math.floor(minutes / (24 * 60))} d ${hours} h` : `${Math.floor(minutes / (24 * 60))} d`;
}

/** "Done, 9:05 am", "Now, 25 min (since 10:40 am)", "Pending": a stage's state in words, with its time. */
export function describeStage(stage: PipelineStage, now = Date.now()): string {
  const words = STATES[stage.state].words;
  if (stage.state === 'Current') {
    const spent = stage.minutesInStage == null ? null : formatStageDuration(stage.minutesInStage);
    const since = stage.at ? `since ${formatStageTime(stage.at, now)}` : null;
    return [words, spent && since ? `${spent} (${since})` : (spent ?? since)].filter(Boolean).join(', ');
  }
  return stage.state === 'Done' && stage.at ? `${words}, ${formatStageTime(stage.at, now)}` : words;
}

/**
 * A patient's journey as a line of stages in order (a visit, a stay, an operation, a lab accession): each with its
 * label, its state as an icon and in words, and its time; the stage the patient is in is marked `aria-current="step"`.
 * `compact` is the form for a row of a list: one icon a stage, named for screen readers and on hover, with only the
 * current stage written out with the time spent in it. The stages wrap at narrow widths rather than scroll.
 */
export function Pipeline({
  stages,
  label,
  compact = false,
  now,
}: {
  stages: readonly PipelineStage[];
  /** What the list is, for screen readers: "Out-patient visit OP2600981". */
  label: string;
  compact?: boolean;
  /** The time to measure "today" against; the clock when not given (tests give one). */
  now?: number;
}) {
  const styles = useStyles();
  if (compact) {
    return (
      <ol className={styles.compactList} aria-label={label}>
        {stages.map((stage) => {
          const { icon: Icon } = STATES[stage.state];
          const words = describeStage(stage, now);
          if (stage.state === 'Current') {
            return (
              <li
                key={stage.key}
                className={styles.compactCurrent}
                aria-current="step"
                title={stage.detail ?? undefined}
              >
                <Icon className={mergeClasses(styles.icon, styles.iconCurrent)} aria-hidden />
                <Text size={200} weight="semibold">
                  {stage.label}
                </Text>
                {stage.minutesInStage == null ? (
                  // No time spent to show: when the stage began, if that is stored, for screen readers alone.
                  stage.at && <span className={styles.srOnly}>{`, since ${formatStageTime(stage.at, now)}`}</span>
                ) : (
                  <Text size={200}>
                    <span className={styles.srOnly}>, </span>
                    {formatStageDuration(stage.minutesInStage)}
                  </Text>
                )}
              </li>
            );
          }
          return (
            <li key={stage.key} className={styles.compactStage} title={`${stage.label}: ${words}`}>
              <Icon className={mergeClasses(styles.icon, iconTone(styles, stage.state))} aria-hidden />
              <span className={styles.srOnly}>{`${stage.label}: ${words}`}</span>
            </li>
          );
        })}
      </ol>
    );
  }

  return (
    <ol className={styles.list} aria-label={label}>
      {stages.map((stage) => {
        const { icon: Icon } = STATES[stage.state];
        const current = stage.state === 'Current';
        return (
          <li
            key={stage.key}
            className={mergeClasses(styles.stage, lineTone(styles, stage.state))}
            aria-current={current ? 'step' : undefined}
          >
            <span className={styles.head}>
              <Icon className={mergeClasses(styles.icon, iconTone(styles, stage.state))} aria-hidden />
              <Text weight={current ? 'bold' : 'semibold'}>{stage.label}</Text>
            </span>
            <Caption1 className={current ? undefined : styles.muted}>{describeStage(stage, now)}</Caption1>
            {stage.detail && <Caption1 className={styles.muted}>{stage.detail}</Caption1>}
          </li>
        );
      })}
    </ol>
  );
}
