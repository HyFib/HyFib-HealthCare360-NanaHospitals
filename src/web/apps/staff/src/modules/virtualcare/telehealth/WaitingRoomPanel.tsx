import { Avatar, Button, Caption1, makeStyles, Text, tokens } from '@fluentui/react-components';
import { PersonAvailableRegular, SendRegular } from '@fluentui/react-icons';
import { formatDateTime, formatTime } from '@hc360/core';
import { formatDuration, minutesSince } from '@/modules/scheduling/dates';
import { useNow } from '@/modules/scheduling/useNow';
import type { TelehealthSessionDetail, WaitingParticipant } from './api';
import { roleLabel } from './labels';

const useStyles = makeStyles({
  panel: { display: 'flex', flexDirection: 'column', gap: '10px' },
  person: {
    display: 'flex',
    alignItems: 'center',
    gap: '10px',
    padding: '10px',
    borderRadius: tokens.borderRadiusLarge,
    backgroundColor: tokens.colorNeutralBackground2,
  },
  text: { display: 'flex', flexDirection: 'column', minWidth: 0, flex: 1 },
  muted: { color: tokens.colorNeutralForeground3 },
  long: { color: tokens.colorPaletteRedForeground1 },
  empty: { display: 'flex', flexDirection: 'column', gap: '8px', color: tokens.colorNeutralForeground2 },
});

export interface WaitingRoomPanelProps {
  waiting: WaitingParticipant[];
  session: TelehealthSessionDetail | undefined;
  canAdmit: boolean;
  admitting: boolean;
  onAdmit: (participantId?: string) => void;
  canResend: boolean;
  resending: boolean;
  onResend: () => void;
}

/** Who is waiting to be let in (TEL-03), with one-click admit, and the join-link status when nobody is there yet. */
export function WaitingRoomPanel({
  waiting,
  session,
  canAdmit,
  admitting,
  onAdmit,
  canResend,
  resending,
  onResend,
}: WaitingRoomPanelProps) {
  const styles = useStyles();
  const now = useNow();

  if (waiting.length === 0) {
    return (
      <div className={styles.empty}>
        <Text weight="semibold">No one is waiting</Text>
        <Caption1>
          Patients join from the link sent by SMS/WhatsApp or from the patient portal. A desktop notification tells you
          when someone arrives.
        </Caption1>
        {session && (
          <Caption1 className={styles.muted}>
            {session.linkSentAt
              ? `Link sent ${session.linkSendCount} time${session.linkSendCount === 1 ? '' : 's'}, last at ${formatDateTime(session.linkSentAt)}.`
              : 'The link has not been sent yet.'}
            {session.joinTokenExpiresAt ? ` It works until ${formatTime(session.joinTokenExpiresAt)}.` : ''}
          </Caption1>
        )}
        {canResend && (
          <div>
            <Button icon={<SendRegular />} disabled={resending} onClick={onResend}>
              Send a new link
            </Button>
          </div>
        )}
      </div>
    );
  }

  return (
    <div className={styles.panel} aria-live="polite">
      {waiting.map((person) => {
        const minutes = minutesSince(person.joinedAt, now);
        return (
          <div key={person.participantId} className={styles.person}>
            <Avatar name={person.displayName} color="colorful" />
            <span className={styles.text}>
              <Text weight="semibold" truncate>
                {person.displayName}
              </Text>
              <Caption1 className={minutes >= 10 ? styles.long : styles.muted}>
                {roleLabel[person.role]} · waiting {formatDuration(minutes)}
              </Caption1>
            </span>
            {canAdmit && (
              <Button
                appearance="primary"
                icon={<PersonAvailableRegular />}
                disabled={admitting}
                onClick={() => onAdmit(person.participantId)}
              >
                Admit
              </Button>
            )}
          </div>
        );
      })}
      {canAdmit && waiting.length > 1 && (
        <Button disabled={admitting} onClick={() => onAdmit()}>
          Admit everyone
        </Button>
      )}
      <Caption1 className={styles.muted}>
        Check it is the right patient before admitting. Guests from an SMS link are shown by first name only.
      </Caption1>
    </div>
  );
}
