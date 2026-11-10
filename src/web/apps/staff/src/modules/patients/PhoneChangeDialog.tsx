import {
  Button,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Field,
  Input,
  makeStyles,
  MessageBar,
  MessageBarBody,
  Spinner,
  Text,
} from '@fluentui/react-components';
import { formatPhone, formatTime, normalizePhone } from '@hc360/core';
import { useNotify } from '@hc360/ui';
import { useMutation, useQuery } from '@tanstack/react-query';
import { useEffect, useState } from 'react';
import { patientKeys, phoneChangeApi, type PhoneChange, type PhoneChangeConfirmed } from './api';
import {
  describeChange,
  formatWait,
  newPhoneProblem,
  phoneChangeProblem,
  type PhoneChangeProblem,
} from './phoneChange';

const useStyles = makeStyles({
  form: { display: 'flex', flexDirection: 'column', gap: '12px' },
  link: { alignSelf: 'flex-start' },
});

/** The time now, a second at a time: the code's expiry and the wait before another code count down with it. */
function useNow(): number {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    const timer = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(timer);
  }, []);
  return now;
}

/**
 * Changing the patient's registered mobile number (or adding one): the new number, then "Send code", which sends a
 * 6-digit code by SMS to that number only; the patient reads it out and "Confirm" makes the change. A change still
 * waiting for its code when the dialog opens carries on at the code. The server's refusals show under the field they are
 * about; the wait before another code (three to a number in 15 minutes) counts down on the buttons.
 */
export function PhoneChangeDialog({
  patientId,
  currentPhone,
  onClose,
  onChanged,
}: {
  patientId: string;
  /** The number on the record (E.164), or null for none. */
  currentPhone: string | null;
  onClose: () => void;
  onChanged: (done: PhoneChangeConfirmed) => void;
}) {
  const styles = useStyles();
  const notify = useNotify();
  const now = useNow();
  const pending = useQuery({
    queryKey: patientKeys.phoneChange(patientId),
    queryFn: ({ signal }) => phoneChangeApi.pending(patientId, signal),
    staleTime: 0,
    gcTime: 0,
  });
  const [phone, setPhone] = useState('');
  /** The number the latest code went to, as typed here; unknown for a change carried on from an earlier dialog. */
  const [sentTo, setSentTo] = useState<string | null>(null);
  /** undefined: carry on with the change the server has waiting, if any; null: none, ask for the number. */
  const [chosen, setChosen] = useState<PhoneChange | null | undefined>(undefined);
  const [code, setCode] = useState('');
  const [problem, setProblem] = useState<PhoneChangeProblem | null>(null);
  const [waitUntil, setWaitUntil] = useState<number | null>(null);

  const change = chosen === undefined ? (pending.data ?? null) : chosen;
  const waitSeconds = waitUntil === null ? 0 : Math.max(0, Math.ceil((waitUntil - now) / 1000));
  const expired = change !== null && Date.parse(change.expiresAt) <= now;

  const send = useMutation({
    mutationFn: (number: string) => phoneChangeApi.request(patientId, number),
    onSuccess: (sent, number) => {
      setProblem(null);
      setCode('');
      setSentTo(number);
      setChosen(sent);
    },
    onError: (error) => {
      const refusal = phoneChangeProblem(error);
      setProblem(refusal);
      if (refusal.retryAfterSeconds) {
        setWaitUntil(Date.now() + refusal.retryAfterSeconds * 1000);
      }
    },
  });

  const confirm = useMutation({
    mutationFn: (current: PhoneChange) => phoneChangeApi.confirm(patientId, current.changeId, code),
    onSuccess: (done) => {
      notify.success('Mobile number changed', describeChange(done));
      onChanged(done);
      onClose();
    },
    onError: (error, current) => {
      const refusal = phoneChangeProblem(error);
      if (refusal.spent) {
        // The code is of no more use: back to the number, saying why.
        setProblem({ ...refusal, field: null });
        setCode('');
        setChosen(null);
        return;
      }
      setProblem(refusal);
      if (refusal.attemptsLeft !== undefined) {
        setChosen({ ...current, attemptsLeft: refusal.attemptsLeft });
      }
    },
  });

  const sendCode = (number: string) => {
    const local = newPhoneProblem(number, currentPhone);
    if (local) {
      setProblem({ field: 'phone', message: local, spent: false });
      return;
    }
    setProblem(null);
    send.mutate(normalizePhone(number) ?? number);
  };

  const sendAgain = () => {
    if (sentTo) {
      send.mutate(sentTo);
      return;
    }
    // Carried on from an earlier dialog: the full number is not shown, so it is typed again.
    setProblem(null);
    setCode('');
    setChosen(null);
  };

  const chooseAnother = () => {
    setProblem(null);
    setCode('');
    setChosen(null);
  };

  const waiting = waitSeconds > 0 ? ` (${formatWait(waitSeconds)})` : '';
  const busy = send.isPending || confirm.isPending;
  const loading = chosen === undefined && pending.isLoading;
  const tries = change ? `${change.attemptsLeft} ${change.attemptsLeft === 1 ? 'try' : 'tries'} left` : '';

  return (
    <Dialog open onOpenChange={(_, d) => !d.open && !busy && onClose()}>
      <DialogSurface>
        <DialogBody>
          <DialogTitle>{currentPhone ? 'Change mobile number' : 'Add mobile number'}</DialogTitle>
          <DialogContent className={styles.form}>
            {loading && <Spinner size="small" label="Checking for a code already sent…" />}
            {!loading && change === null && (
              <>
                <Text block>
                  The patient signs in to the portal with this number, so it changes only with a code sent by SMS to the
                  new number. Nothing is sent to the old number
                  {currentPhone ? ` (${formatPhone(currentPhone)})` : ''}.
                </Text>
                <Field
                  label="New mobile number"
                  required
                  hint="10 digits. Any other number goes in Alternate phone."
                  validationMessage={problem?.field === 'phone' ? problem.message : undefined}
                  validationState={problem?.field === 'phone' ? 'error' : 'none'}
                >
                  <Input
                    type="tel"
                    value={phone}
                    maxLength={16}
                    autoComplete="off"
                    onChange={(_, d) => {
                      setPhone(d.value);
                      if (problem?.field === 'phone') {
                        setProblem(null);
                      }
                    }}
                  />
                </Field>
              </>
            )}
            {!loading && change !== null && (
              <>
                <Text block>
                  A 6-digit code was sent by SMS to {change.maskedPhone}. Ask the patient to read it out.
                </Text>
                <Field
                  label="Code"
                  required
                  hint={
                    expired
                      ? 'The code has expired. Send a new code.'
                      : `Works until ${formatTime(change.expiresAt)} · ${tries}`
                  }
                  validationMessage={problem?.field === 'code' ? problem.message : undefined}
                  validationState={problem?.field === 'code' ? 'error' : 'none'}
                >
                  <Input
                    value={code}
                    inputMode="numeric"
                    maxLength={6}
                    autoComplete="off"
                    onChange={(_, d) => setCode(d.value.replace(/\D/g, ''))}
                  />
                </Field>
                <Button
                  className={styles.link}
                  appearance="transparent"
                  size="small"
                  onClick={chooseAnother}
                  disabled={busy}
                >
                  Use another number
                </Button>
              </>
            )}
            {problem && problem.field === null && (
              <MessageBar intent={problem.retryAfterSeconds ? 'warning' : 'error'} layout="multiline">
                <MessageBarBody>
                  {problem.message}
                  {waitSeconds > 0 ? ` Another code can be sent in ${formatWait(waitSeconds)}.` : ''}
                </MessageBarBody>
              </MessageBar>
            )}
          </DialogContent>
          <DialogActions>
            <Button onClick={onClose} disabled={busy}>
              Cancel
            </Button>
            {!loading && change === null && (
              <Button
                appearance="primary"
                disabled={busy || waitSeconds > 0}
                icon={send.isPending ? <Spinner size="tiny" aria-hidden /> : undefined}
                onClick={() => sendCode(phone)}
              >
                {`Send code${waiting}`}
              </Button>
            )}
            {!loading && change !== null && (
              <>
                <Button disabled={busy || waitSeconds > 0} onClick={sendAgain}>
                  {`Send a new code${waiting}`}
                </Button>
                <Button
                  appearance="primary"
                  disabled={code.length !== 6 || expired || busy}
                  icon={confirm.isPending ? <Spinner size="tiny" aria-hidden /> : undefined}
                  onClick={() => confirm.mutate(change)}
                >
                  Confirm
                </Button>
              </>
            )}
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}
