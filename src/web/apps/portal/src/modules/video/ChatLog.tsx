import { makeStyles, mergeClasses, tokens } from '@fluentui/react-components';
import { useQuery } from '@tanstack/react-query';
import { ErrorBlock, LoadingCards } from '@/components/States';
import { useT } from '@/i18n';
import { useFormat } from '@/i18n/useFormat';
import type { TelehealthMessageDto } from '@/lib/types';
import { videoQueries } from './api';

const useStyles = makeStyles({
  list: { listStyle: 'none', margin: 0, padding: 0, display: 'flex', flexDirection: 'column', gap: '8px' },
  bubble: {
    maxWidth: '85%',
    padding: '8px 12px',
    borderRadius: tokens.borderRadiusXLarge,
    backgroundColor: tokens.colorNeutralBackground3,
    alignSelf: 'flex-start',
  },
  mine: { alignSelf: 'flex-end', backgroundColor: tokens.colorBrandBackground2 },
  meta: { fontSize: tokens.fontSizeBase200, color: tokens.colorNeutralForeground3 },
  text: { margin: 0, whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' },
  transcript: {
    display: 'flex',
    flexDirection: 'column',
    gap: '12px',
    padding: '16px',
    borderRadius: tokens.borderRadiusXLarge,
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  heading: { margin: 0, fontSize: tokens.fontSizeBase400, fontWeight: tokens.fontWeightSemibold },
  muted: { margin: 0, color: tokens.colorNeutralForeground2 },
});

/** Chat messages as bubbles, the patient's own on the right. `live` announces new ones (the chat during the call). */
export function ChatLog({ messages, live = false }: { messages: TelehealthMessageDto[]; live?: boolean }) {
  const styles = useStyles();
  const t = useT();
  const f = useFormat();
  return (
    <ul className={styles.list} role="log" aria-live={live ? 'polite' : 'off'}>
      {messages.map((message) => {
        const mine = message.senderRole !== 'Clinician';
        return (
          <li key={message.id} className={mergeClasses(styles.bubble, mine && styles.mine)}>
            <span className={styles.meta}>
              {mine ? t('video.call.you') : message.senderName} · {f.time(message.sentAt)}
            </span>
            <p className={styles.text}>{message.text}</p>
          </li>
        );
      })}
    </ul>
  );
}

/** After a consultation: what the patient and the doctor wrote in its chat, to read again (instructions, a dose). */
export function ChatTranscript({ patientId, sessionId }: { patientId: string; sessionId: string }) {
  const styles = useStyles();
  const t = useT();
  const messages = useQuery(videoQueries.messages(patientId, sessionId));
  return (
    <section className={styles.transcript} aria-labelledby="chat-transcript">
      <h2 id="chat-transcript" className={styles.heading}>
        {t('video.chat.history')}
      </h2>
      {messages.isPending ? (
        <LoadingCards count={1} />
      ) : !messages.data ? (
        <ErrorBlock error={messages.error} onRetry={() => void messages.refetch()} />
      ) : messages.data.length === 0 ? (
        <p className={styles.muted}>{t('video.chat.none')}</p>
      ) : (
        <ChatLog messages={messages.data} />
      )}
    </section>
  );
}
