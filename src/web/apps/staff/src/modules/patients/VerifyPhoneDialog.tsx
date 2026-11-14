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
import { formatTime, isApiError } from '@hc360/core';
import { useNotify } from '@hc360/ui';
import { useMutation, useQuery } from '@tanstack/react-query';
import { useEffect, useState } from 'react';
import { patientKeys, phoneVerificationApi, type PhoneVerification, type PhoneVerified } from './api';
import { describeVerified, formatWait, phoneChangeProblem, type PhoneChangeProblem } from './phoneChange';

const useStyles = makeStyles({
  form: { display: 'flex', flexDirection: 'column', gap: '12px' },
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
 * Verifying the patient's registered mobile number at the desk ("Verify mobile"): "Send code" sends a 6-digit code to
 * the number on the record (by SMS, and on WhatsApp where the hospital has switched its template on); the patient reads it
 * out and "Confirm" marks the number verified. A verification still waiting for its code when the dialog opens carries
 * on at the code. Optional: nothing else waits for it. Refusals and the wait before another code (three to a number in
 * 15 minutes, number changes included) show as the number change's do (PhoneChangeDialog).
 */
export function VerifyPhoneDialog({
  patientId,
  phone,
  onClose,
  onVerified,
}: {
  patientId: string;
  /** The number on the record, as this user may see it. */
  phone: string;
  onClose: () => void;
  onVerified: (done: PhoneVerified) => void;
}) {
  const styles = useStyles();
  const notify = useNotify();
  const now = useNow();
  const pending = useQuery({
    queryKey: patientKeys.phoneVerification(patientId),
    queryFn: ({ signal }) => phoneVerificationApi.pending(patientId, signal),
    staleTime: 0,
    gcTime: 0,
  });
  /** undefined: carry on with the verification the server has waiting, if any; null: none, offer to send a code. */
  const [chosen, setChosen] = useState<PhoneVerification | null | undefined>(undefined);
  const [code, setCode] = useState('');
  const [problem, setProblem] = useState<PhoneChangeProblem | null>(null);
  const [waitUntil, setWaitUntil] = useState<number | null>(null);

  const verification = chosen === undefined ? (pending.data ?? null) : chosen;
  const waitSeconds = waitUntil === null ? 0 : Math.max(0, Math.ceil((waitUntil - now) / 1000));
  const expired = verification !== null && Date.parse(verification.expiresAt) <= now;

  const refused = (error: unknown) => {
    const refusal = phoneChangeProblem(error);
    setProblem(refusal);
    if (refusal.retryAfterSeconds) {
      setWaitUntil(Date.now() + refusal.retryAfterSeconds * 1000);
    }
    return refusal;
  };

  const send = useMutation({
    mutationFn: () => phoneVerificationApi.request(patientId),
    onSuccess: (sent) => {
      setProblem(null);
      setCode('');
      setChosen(sent);
    },
    onError: (error) => {
      refused(error);
      // Verified meanwhile (another desk, or the patient in the portal): nothing more to do here.
      if (isApiError(error) && error.code === 'patient.phone_already_verified') {
        onVerified({ maskedPhone: phone, verifiedAt: new Date().toISOString() });
      }
    },
  });

  const confirm = useMutation({
    mutationFn: (current: PhoneVerification) => phoneVerificationApi.confirm(patientId, current.verificationId, code),
    onSuccess: (done) => {
      notify.success('Mobile number verified', describeVerified(done));
      onVerified(done);
      onClose();
    },
    onError: (error, current) => {
      const refusal = refused(error);
      if (refusal.spent) {
        // The code is of no more use: back to sending one, saying why.
        setProblem({ ...refusal, field: null });
        setCode('');
        setChosen(null);
        return;
      }
      if (refusal.attemptsLeft !== undefined) {
        setChosen({ ...current, attemptsLeft: refusal.attemptsLeft });
      }
    },
  });

  const waiting = waitSeconds > 0 ? ` (${formatWait(waitSeconds)})` : '';
  const busy = send.isPending || confirm.isPending;
  const loading = chosen === undefined && pending.isLoading;
  const tries = verification
    ? `${verification.attemptsLeft} ${verification.attemptsLeft === 1 ? 'try' : 'tries'} left`
    : '';

  return (
    <Dialog open onOpenChange={(_, d) => !d.open && !busy && onClose()}>
      <DialogSurface>
        <DialogBody>
          <DialogTitle>Verify mobile number</DialogTitle>
          <DialogContent className={styles.form}>
            {loading && <Spinner size="small" label="Checking for a code already sent…" />}
            {!loading && verification === null && (
              <Text block>
                A 6-digit code goes to {phone}. Ask the patient to read it out from the message. Until the number is
                verified, reports and bills go to it without the document, and the patient portal asks whoever signs in
                with it for the patient’s date of birth first.
              </Text>
            )}
            {!loading && verification !== null && (
              <>
                <Text block>
                  A 6-digit code was sent to {verification.maskedPhone}. Ask the patient to read it out.
                </Text>
                <Field
                  label="Code"
                  required
                  hint={
                    expired
                      ? 'The code has expired. Send a new code.'
                      : `Works until ${formatTime(verification.expiresAt)} · ${tries}`
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
              </>
            )}
            {problem && problem.field !== 'code' && (
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
            {!loading && verification === null && (
              <Button
                appearance="primary"
                disabled={busy || waitSeconds > 0}
                icon={send.isPending ? <Spinner size="tiny" aria-hidden /> : undefined}
                onClick={() => send.mutate()}
              >
                {`Send code${waiting}`}
              </Button>
            )}
            {!loading && verification !== null && (
              <>
                <Button disabled={busy || waitSeconds > 0} onClick={() => send.mutate()}>
                  {`Send a new code${waiting}`}
                </Button>
                <Button
                  appearance="primary"
                  disabled={code.length !== 6 || expired || busy}
                  icon={confirm.isPending ? <Spinner size="tiny" aria-hidden /> : undefined}
                  onClick={() => confirm.mutate(verification)}
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
