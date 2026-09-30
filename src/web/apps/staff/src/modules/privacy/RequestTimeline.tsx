import { Badge, Caption1, makeStyles, Text, tokens } from '@fluentui/react-components';
import {
  CheckmarkCircleRegular,
  ContactCardRegular,
  EraserRegular,
  EyeOffRegular,
  MailInboxRegular,
  NoteRegular,
  PersonArrowRightRegular,
  PlayRegular,
  SendRegular,
  TaskListLtrRegular,
  ArrowUndoRegular,
} from '@fluentui/react-icons';
import { formatDateTime } from '@hc360/core';
import { Section } from '@hc360/ui';
import type { ReactNode } from 'react';
import type { DataRequestEventDto } from './api';
import { eventKindLabel, type DataRequestEventKind } from './labels';

const useStyles = makeStyles({
  list: { listStyle: 'none', margin: 0, padding: 0, display: 'flex', flexDirection: 'column' },
  item: {
    display: 'grid',
    gridTemplateColumns: '28px minmax(0, 1fr)',
    gap: '10px',
    padding: '10px 0',
    borderTop: `1px solid ${tokens.colorNeutralStroke3}`,
    ':first-child': { borderTop: 'none' },
  },
  icon: { color: tokens.colorBrandForeground1, fontSize: '20px', display: 'flex', paddingTop: '2px' },
  noteIcon: { color: tokens.colorPaletteMarigoldForeground1 },
  body: { display: 'flex', flexDirection: 'column', gap: '2px', minWidth: 0 },
  head: { display: 'flex', gap: '8px', alignItems: 'center', flexWrap: 'wrap' },
  text: { whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' },
  muted: { color: tokens.colorNeutralForeground3 },
  private: { display: 'inline-flex', alignItems: 'center', gap: '4px', color: tokens.colorNeutralForeground3 },
});

const icons: Record<DataRequestEventKind, ReactNode> = {
  Received: <MailInboxRegular />,
  IdentityVerified: <ContactCardRegular />,
  Started: <PlayRegular />,
  Assigned: <PersonArrowRightRegular />,
  Note: <NoteRegular />,
  CorrectionsDecided: <TaskListLtrRegular />,
  Erased: <EraserRegular />,
  Closed: <SendRegular />,
  Withdrawn: <ArrowUndoRegular />,
};

/** The request's history, newest first. Staff notes and assignments are never shown to the requester. */
export function RequestTimeline({ events }: { events: DataRequestEventDto[] }) {
  const styles = useStyles();
  const newestFirst = [...events].reverse();
  return (
    <Section title="History" description="Newest first. Lines marked as staff-only are never shown to the patient.">
      {newestFirst.length === 0 ? (
        <Text className={styles.muted}>Nothing has happened yet.</Text>
      ) : (
        <ol className={styles.list}>
          {newestFirst.map((event, index) => (
            <li key={`${event.at}-${index}`} className={styles.item}>
              <span className={event.kind === 'Note' ? `${styles.icon} ${styles.noteIcon}` : styles.icon} aria-hidden>
                {icons[event.kind] ?? <CheckmarkCircleRegular />}
              </span>
              <div className={styles.body}>
                <div className={styles.head}>
                  <Text weight="semibold">{eventKindLabel[event.kind] ?? event.kind}</Text>
                  {event.kind === 'Note' ? (
                    <Badge appearance="tint" color="warning" icon={<EyeOffRegular />}>
                      Staff note — not shown to the patient
                    </Badge>
                  ) : (
                    !event.isPublic && (
                      <Caption1 className={styles.private}>
                        <EyeOffRegular aria-hidden /> Staff only
                      </Caption1>
                    )
                  )}
                </div>
                <Text className={styles.text}>{event.text}</Text>
                <Caption1 className={styles.muted}>
                  {formatDateTime(event.at)}
                  {event.by ? ` · ${event.by}` : ''}
                </Caption1>
              </div>
            </li>
          ))}
        </ol>
      )}
    </Section>
  );
}
