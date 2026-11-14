import {
  Button,
  Field,
  Input,
  makeStyles,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  Spinner,
} from '@fluentui/react-components';
import { formatTime } from '@hc360/core';
import { useState } from 'react';
import { isTerminalBusy, unconfirmedGuidance, type UnconfirmedSale } from './labels';

const useStyles = makeStyles({
  clear: { display: 'flex', flexDirection: 'column', gap: '8px', width: '100%', textAlign: 'left' },
});

/**
 * A card/UPI sale whose answer from the terminal was lost. It is not declined: the customer may still tap on the
 * terminal, so this says what to check and when a new sale may be sent.
 */
export function UnconfirmedSaleNotice({ sale, now }: { sale: UnconfirmedSale & { status: string }; now: Date }) {
  const busy = isTerminalBusy(sale, now);
  return (
    <MessageBar intent="warning" role="alert">
      <MessageBarBody>
        <MessageBarTitle>Do not send this sale again yet</MessageBarTitle>
        {unconfirmedGuidance(sale)}
        {sale.retryAfter && (
          <>
            {' '}
            {busy
              ? `Sending again is possible from ${formatTime(sale.retryAfter)}.`
              : 'The wait is over: you can send again.'}
          </>
        )}
      </MessageBarBody>
    </MessageBar>
  );
}

/**
 * The cashier looked at the terminal and it shows no sale: nothing was taken, so the terminal is freed before the wait
 * ends. A note on what was checked is required and is kept in the audit trail.
 */
export function ClearUnconfirmedSale({ onClear, pending }: { onClear: (note: string) => void; pending: boolean }) {
  const styles = useStyles();
  const [note, setNote] = useState('');
  const ready = note.trim().length >= 3;
  return (
    <form
      className={styles.clear}
      onSubmit={(event) => {
        event.preventDefault();
        event.stopPropagation();
        if (ready && !pending) {
          onClear(note.trim());
        }
      }}
    >
      <Field
        label="Terminal shows no sale?"
        hint="Say what you checked, for example “Terminal screen shows the idle menu”."
      >
        <Input value={note} onChange={(_, data) => setNote(data.value)} maxLength={200} />
      </Field>
      <Button
        type="submit"
        disabled={!ready || pending}
        icon={pending ? <Spinner size="tiny" aria-hidden /> : undefined}
      >
        The terminal shows no sale
      </Button>
    </form>
  );
}
