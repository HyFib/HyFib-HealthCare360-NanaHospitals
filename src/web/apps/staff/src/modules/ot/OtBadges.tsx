import {
  Badge,
  Link,
  makeStyles,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  Text,
  tokens,
} from '@fluentui/react-components';
import { CheckmarkCircleRegular, CircleRegular, WarningRegular } from '@fluentui/react-icons';
import { formatDate, formatTime } from '@hc360/core';
import { StatusBadge } from '@hc360/ui';
import { useNavigate } from '@tanstack/react-router';
import type { Laterality, SchedulingConflict, SurgeryPriority, SurgeryStatus } from './api';
import { conflictLabel, priorityLabel, priorityTone, sideShort, surgeryStatusLabel, surgeryStatusTone } from './labels';

const useStyles = makeStyles({
  who: { display: 'inline-flex', gap: '4px', flexWrap: 'wrap', alignItems: 'center' },
  pill: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: '2px',
    padding: '0 6px',
    borderRadius: tokens.borderRadiusCircular,
    fontSize: tokens.fontSizeBase100,
    lineHeight: '18px',
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    color: tokens.colorNeutralForeground3,
    whiteSpace: 'nowrap',
  },
  pillDone: {
    border: `1px solid ${tokens.colorPaletteGreenBorder2}`,
    color: tokens.colorPaletteGreenForeground1,
    backgroundColor: tokens.colorPaletteGreenBackground1,
  },
  side: { fontWeight: tokens.fontWeightBold, letterSpacing: '0.5px' },
  conflicts: { margin: '4px 0 0', paddingLeft: '18px' },
});

export function SurgeryStatusBadge({ status }: { status: SurgeryStatus }) {
  return <StatusBadge status={status} tone={surgeryStatusTone[status]} label={surgeryStatusLabel[status]} />;
}

/** Elective cases get no badge (the default); urgent and emergency cases always say so in words. */
export function PriorityBadge({ priority, showElective }: { priority: SurgeryPriority; showElective?: boolean }) {
  if (priority === 'Elective' && !showElective) {
    return null;
  }
  return (
    <Badge
      appearance={priority === 'Emergency' ? 'filled' : 'tint'}
      color={priorityTone[priority]}
      shape="rounded"
      icon={priority === 'Emergency' ? <WarningRegular /> : undefined}
    >
      {priorityLabel[priority]}
    </Badge>
  );
}

/** Operative side in capitals (wrong-site surgery prevention). Nothing for midline procedures. */
export function SideBadge({
  laterality,
  size = 'medium',
}: {
  laterality: Laterality;
  size?: 'small' | 'medium' | 'large';
}) {
  const styles = useStyles();
  const side = sideShort(laterality);
  if (!side) {
    return null;
  }
  return (
    <Badge
      appearance="outline"
      color="important"
      size={size}
      className={styles.side}
      aria-label={`Operative side: ${side.toLowerCase()}`}
    >
      {side}
    </Badge>
  );
}

/** Sign-in / Time-out / Sign-out progress, with ticks and words (colour is never the only signal). */
export function WhoProgress({
  signInDone,
  timeOutDone,
  signOutDone,
  compact,
}: {
  signInDone: boolean;
  timeOutDone: boolean;
  signOutDone: boolean;
  compact?: boolean;
}) {
  const styles = useStyles();
  const phases: [string, string, boolean][] = [
    ['Sign-in', 'SI', signInDone],
    ['Time-out', 'TO', timeOutDone],
    ['Sign-out', 'SO', signOutDone],
  ];
  const summary = phases.map(([name, , done]) => `${name} ${done ? 'done' : 'not done'}`).join(', ');
  return (
    <span className={styles.who} role="img" aria-label={`WHO checklist: ${summary}`}>
      {phases.map(([name, short, done]) => (
        <span key={name} className={done ? `${styles.pill} ${styles.pillDone}` : styles.pill} aria-hidden>
          {done ? <CheckmarkCircleRegular /> : <CircleRegular />}
          {compact ? short : name}
        </span>
      ))}
    </span>
  );
}

/**
 * Double-booking refused by the server (409 `surgery.slot_conflict`): each clash in words with its time, and a
 * link to the booking in the way.
 */
export function ConflictAlert({ conflicts }: { conflicts: SchedulingConflict[] }) {
  const styles = useStyles();
  const navigate = useNavigate();
  return (
    <MessageBar intent="error" layout="multiline" politeness="assertive">
      <MessageBarBody>
        <MessageBarTitle>This slot clashes with another booking</MessageBarTitle>
        <Text>Choose another time or theatre, or save it as a request and schedule it later.</Text>
        {conflicts.length > 0 && (
          <ul className={styles.conflicts}>
            {conflicts.map((conflict, index) => (
              <li key={`${conflict.bookingId}-${conflict.kind}-${index}`}>
                {conflictLabel[conflict.kind]} from {formatTime(conflict.start)} to {formatTime(conflict.end)} on{' '}
                {formatDate(conflict.start)}.{' '}
                <Link
                  as="button"
                  inline
                  onClick={() => void navigate({ to: '/ot/bookings/$id', params: { id: conflict.bookingId } })}
                >
                  Open that booking
                </Link>
              </li>
            ))}
          </ul>
        )}
      </MessageBarBody>
    </MessageBar>
  );
}
