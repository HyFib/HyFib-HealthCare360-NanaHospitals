import { Button, Caption1, makeStyles, mergeClasses, Text, Textarea, tokens } from '@fluentui/react-components';
import { SendRegular } from '@fluentui/react-icons';
import { formatTime } from '@hc360/core';
import { useNotify } from '@hc360/ui';
import { useEffect, useRef, useState } from 'react';
import { hubErrorMessage } from '../signalling/hubProtocol';
import type { TelehealthMessageDto } from './api';

const MAX_LENGTH = 2000;

const useStyles = makeStyles({
  panel: { display: 'flex', flexDirection: 'column', gap: '8px', height: '100%', minHeight: '320px' },
  list: { display: 'flex', flexDirection: 'column', gap: '8px', overflowY: 'auto', flex: 1, maxHeight: '48vh', paddingRight: '4px' },
  bubble: {
    maxWidth: '85%',
    padding: '8px 10px',
    borderRadius: tokens.borderRadiusLarge,
    backgroundColor: tokens.colorNeutralBackground3,
    display: 'flex',
    flexDirection: 'column',
    gap: '2px',
    alignSelf: 'flex-start',
    whiteSpace: 'pre-wrap',
    overflowWrap: 'anywhere',
  },
  mine: { alignSelf: 'flex-end', backgroundColor: tokens.colorBrandBackground2 },
  muted: { color: tokens.colorNeutralForeground3 },
  composer: { display: 'flex', flexDirection: 'column', gap: '6px' },
  row: { display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: '8px' },
});

/** In-call chat (TEL-02); messages are stored with the consultation. Enter sends, Shift+Enter starts a new line. */
export function ChatPanel({
  messages,
  disabled,
  onSend,
  readOnly = false,
}: {
  messages: TelehealthMessageDto[];
  disabled: boolean;
  onSend?: (text: string) => Promise<void>;
  /** Transcript only (closed consultations). */
  readOnly?: boolean;
}) {
  const styles = useStyles();
  const notify = useNotify();
  const [text, setText] = useState('');
  const [sending, setSending] = useState(false);
  const list = useRef<HTMLDivElement>(null);

  useEffect(() => {
    const element = list.current;
    if (element) {
      element.scrollTop = element.scrollHeight;
    }
  }, [messages.length]);

  const send = () => {
    const clean = text.trim();
    if (!clean || sending || disabled || !onSend) {
      return;
    }
    setSending(true);
    onSend(clean).then(
      () => {
        setText('');
        setSending(false);
      },
      (error: unknown) => {
        setSending(false);
        notify.warning('Message not sent', hubErrorMessage(error));
      },
    );
  };

  return (
    <div className={styles.panel}>
      <div className={styles.list} ref={list} role="log" aria-label="Chat messages" aria-live="polite">
        {messages.length === 0 ? (
          <Caption1 className={styles.muted}>No messages yet. Use chat for spellings, instructions or when the audio is poor.</Caption1>
        ) : (
          messages.map((m) => (
            <div key={m.id} className={mergeClasses(styles.bubble, m.senderRole === 'Clinician' && styles.mine)}>
              <Caption1 className={styles.muted}>
                {m.senderName} · {formatTime(m.sentAt)}
              </Caption1>
              <Text>{m.text}</Text>
            </div>
          ))
        )}
      </div>
      {!readOnly && (
        <div className={styles.composer}>
          <Textarea
            value={text}
            onChange={(_, d) => setText(d.value.slice(0, MAX_LENGTH))}
            onKeyDown={(event) => {
              if (event.key === 'Enter' && !event.shiftKey) {
                event.preventDefault();
                send();
              }
            }}
            placeholder={disabled ? 'Chat opens when you join' : 'Type a message'}
            disabled={disabled}
            rows={2}
            resize="vertical"
            aria-label="Chat message"
          />
          <div className={styles.row}>
            <Caption1 className={styles.muted}>
              {text.length}/{MAX_LENGTH}
            </Caption1>
            <Button appearance="primary" icon={<SendRegular />} disabled={disabled || sending || !text.trim()} onClick={send}>
              Send
            </Button>
          </div>
        </div>
      )}
    </div>
  );
}
