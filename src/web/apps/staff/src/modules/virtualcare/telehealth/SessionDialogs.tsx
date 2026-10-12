import {
  Button,
  Caption1,
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
  Select,
  Spinner,
  Switch,
  Textarea,
  tokens,
} from '@fluentui/react-components';
import { CheckmarkRegular, CopyRegular } from '@fluentui/react-icons';
import { formatDateTime, formatTime, useSession } from '@hc360/core';
import { CloseRequestScope, useCloseRequests, useNotify, useUnsavedChanges, useValuesChanged } from '@hc360/ui';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { ignoreEscapeWhile } from '@/lib/dialogFocus';
import { toOptions, useDepartments, usePractitioners } from '@/lib/lookups';
import { PatientPicker, type PickedPatient } from '@/modules/patients/PatientPicker';
import { istLocalToIso, isoToIstLocal } from '@/modules/scheduling/dates';
import { useNow } from '@/modules/scheduling/useNow';
import { telehealthApi, telehealthKeys, type TelehealthLinkIssued, type TelehealthSessionDetail } from './api';

const useStyles = makeStyles({
  form: { display: 'flex', flexDirection: 'column', gap: '12px' },
  link: {
    fontFamily: tokens.fontFamilyMonospace,
    fontSize: tokens.fontSizeBase200,
    padding: '10px 12px',
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground3,
    wordBreak: 'break-all',
    userSelect: 'all',
  },
  muted: { color: tokens.colorNeutralForeground3 },
  row: { display: 'flex', gap: '12px', flexWrap: 'wrap', alignItems: 'end' },
  destructive: {
    backgroundColor: tokens.colorPaletteRedBackground3,
    ':hover': { backgroundColor: tokens.colorPaletteRedForeground1 },
  },
});

// ------------------------------------------------------------------ join link

/** The patient's join link after creating a session or re-sending it; shown so it can be read out if SMS fails. */
export function LinkIssuedDialog({ link, onClose }: { link: TelehealthLinkIssued | null; onClose: () => void }) {
  const styles = useStyles();
  const [copied, setCopied] = useState(false);
  const url = link ? new URL(link.joinUrl, window.location.origin).toString() : '';

  const copy = () => {
    void navigator.clipboard?.writeText(url).then(
      () => setCopied(true),
      () => setCopied(false),
    );
  };

  return (
    <Dialog
      open={link !== null}
      onOpenChange={(_, d) => {
        if (!d.open) {
          setCopied(false);
          onClose();
        }
      }}
    >
      <DialogSurface style={{ maxWidth: 560 }}>
        <DialogBody>
          <DialogTitle>{link?.sent ? 'Link sent to the patient' : 'Share the link with the patient'}</DialogTitle>
          <DialogContent className={styles.form}>
            <MessageBar intent={link?.sent ? 'success' : 'warning'}>
              <MessageBarBody>
                {link?.sent
                  ? 'The patient received the video consultation link by SMS/WhatsApp.'
                  : 'The link could not be sent automatically. Read it out or send it from the hospital phone.'}
              </MessageBarBody>
            </MessageBar>
            <div className={styles.link}>{url}</div>
            <Caption1 className={styles.muted}>
              {link?.expiresAt ? `The link works until ${formatDateTime(link.expiresAt)}. ` : ''}Any earlier link for
              this consultation no longer works.
            </Caption1>
          </DialogContent>
          <DialogActions>
            <Button icon={copied ? <CheckmarkRegular /> : <CopyRegular />} onClick={copy}>
              {copied ? 'Copied' : 'Copy link'}
            </Button>
            <Button appearance="primary" onClick={onClose}>
              Done
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}

// ------------------------------------------------------------------ new ad-hoc consultation

const DURATIONS = [10, 15, 20, 30, 45, 60];

/** A video consultation without an appointment (for example a follow-up call arranged by the doctor). */
export function NewSessionDialog({
  open,
  onClose,
  onCreated,
}: {
  open: boolean;
  onClose: () => void;
  onCreated: (link: TelehealthLinkIssued) => void;
}) {
  const closing = useCloseRequests({ onClose });
  return (
    <Dialog open={open} onOpenChange={closing.onOpenChange}>
      <DialogSurface style={{ maxWidth: 600 }}>
        <CloseRequestScope value={closing}>
          {open && <NewSessionContent onClose={onClose} onCreated={onCreated} />}
        </CloseRequestScope>
      </DialogSurface>
    </Dialog>
  );
}

function NewSessionContent({
  onClose,
  onCreated,
}: {
  onClose: () => void;
  onCreated: (link: TelehealthLinkIssued) => void;
}) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const now = useNow();
  const myPractitionerId = useSession((s) => s.user?.practitionerId) ?? '';
  const [patient, setPatient] = useState<PickedPatient | null>(null);
  const [departmentId, setDepartmentId] = useState('');
  const [practitionerId, setPractitionerId] = useState(myPractitionerId);
  const [startsAt, setStartsAt] = useState(() => isoToIstLocal(new Date(now + 10 * 60_000).toISOString()));
  const [duration, setDuration] = useState(15);
  const [sendLink, setSendLink] = useState(true);
  const [problem, setProblem] = useState<string | null>(null);
  const departments = useDepartments('Clinical');
  const doctors = usePractitioners(departmentId || null);
  const iso = istLocalToIso(startsAt);

  const create = useMutation({
    mutationFn: () =>
      telehealthApi.create({
        appointmentId: null,
        patientId: patient!.id,
        practitionerId,
        scheduledStartAt: iso,
        durationMinutes: duration,
        sendLink,
      }),
    onSuccess: (link) => {
      void queryClient.invalidateQueries({ queryKey: telehealthKeys.all });
      onCreated(link);
    },
    onError: (error) => notify.error(error, 'Could not create the video consultation'),
  });

  const submit = () => {
    if (!patient) {
      setProblem('Choose the patient.');
    } else if (!practitionerId) {
      setProblem('Choose the doctor.');
    } else if (!iso) {
      setProblem('Choose the date and time.');
    } else {
      setProblem(null);
      create.mutate();
    }
  };

  const guard = useUnsavedChanges({
    dirty: useValuesChanged({ departmentId, practitionerId, startsAt, duration, sendLink }),
    busy: create.isPending,
    onClose,
  });

  return (
    <form
      onSubmit={(event) => {
        event.preventDefault();
        submit();
      }}
    >
      <DialogBody>
        <DialogTitle>
          New video consultation
          <Caption1 block className={styles.muted}>
            For consultations booked as appointments, the video session is created automatically.
          </Caption1>
        </DialogTitle>
        <DialogContent className={styles.form}>
          <PatientPicker value={patient} onChange={setPatient} required />
          <div className={styles.row}>
            <Field label="Department" style={{ flex: '1 1 200px' }}>
              <Select
                value={departmentId}
                onChange={(_, d) => {
                  setDepartmentId(d.value);
                  setPractitionerId('');
                }}
              >
                <option value="">All departments</option>
                {toOptions(departments.data).map((o) => (
                  <option key={o.value} value={o.value}>
                    {o.label}
                  </option>
                ))}
              </Select>
            </Field>
            <Field label="Doctor" required style={{ flex: '1 1 200px' }}>
              <Select value={practitionerId} onChange={(_, d) => setPractitionerId(d.value)}>
                <option value="">Choose doctor</option>
                {toOptions(doctors.data).map((o) => (
                  <option key={o.value} value={o.value}>
                    {o.label}
                  </option>
                ))}
              </Select>
            </Field>
          </div>
          <div className={styles.row}>
            <Field label="Date and time" required>
              <Input type="datetime-local" value={startsAt} onChange={(_, d) => setStartsAt(d.value)} />
            </Field>
            <Field label="Length">
              <Select value={String(duration)} onChange={(_, d) => setDuration(Number(d.value))}>
                {DURATIONS.map((m) => (
                  <option key={m} value={m}>
                    {m} minutes
                  </option>
                ))}
              </Select>
            </Field>
          </div>
          <Switch
            checked={sendLink}
            onChange={(_, d) => setSendLink(d.checked)}
            label="Send the join link to the patient by SMS/WhatsApp now"
          />
          {problem && (
            <MessageBar intent="error">
              <MessageBarBody>{problem}</MessageBarBody>
            </MessageBar>
          )}
        </DialogContent>
        <DialogActions>
          <Button onClick={guard.requestClose} disabled={create.isPending}>
            Cancel
          </Button>
          <Button
            type="submit"
            appearance="primary"
            disabled={create.isPending}
            icon={create.isPending ? <Spinner size="tiny" aria-hidden /> : undefined}
          >
            Create consultation
          </Button>
        </DialogActions>
      </DialogBody>
      {guard.dialogs}
    </form>
  );
}

// ------------------------------------------------------------------ cancel

export interface SessionRef {
  id: string;
  patientName: string;
  scheduledStartAt: string;
}

export function CancelSessionDialog({ session, onClose }: { session: SessionRef | null; onClose: () => void }) {
  const closing = useCloseRequests({ onClose });
  return (
    <Dialog open={session !== null} onOpenChange={closing.onOpenChange} modalType="alert">
      <DialogSurface style={{ maxWidth: 520 }}>
        <CloseRequestScope value={closing}>
          {session && <CancelSessionContent key={session.id} session={session} onClose={onClose} />}
        </CloseRequestScope>
      </DialogSurface>
    </Dialog>
  );
}

function CancelSessionContent({ session, onClose }: { session: SessionRef; onClose: () => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [reason, setReason] = useState('');
  const cancel = useMutation({
    mutationFn: () => telehealthApi.cancel(session.id, reason.trim()),
    onSuccess: () => {
      notify.success('Video consultation cancelled', 'The join link no longer works.');
      void queryClient.invalidateQueries({ queryKey: telehealthKeys.all });
      onClose();
    },
    onError: (error) => notify.error(error, 'Could not cancel the consultation'),
  });

  const guard = useUnsavedChanges({ dirty: reason !== '', busy: cancel.isPending, onClose });

  return (
    <form
      onSubmit={(event) => {
        event.preventDefault();
        if (reason.trim() && !cancel.isPending) {
          cancel.mutate();
        }
      }}
      onKeyDown={ignoreEscapeWhile(cancel.isPending)}
    >
      <DialogBody>
        <DialogTitle>
          Cancel this video consultation?
          <Caption1 block className={styles.muted}>
            {session.patientName} · {formatDateTime(session.scheduledStartAt)}
          </Caption1>
        </DialogTitle>
        <DialogContent className={styles.form}>
          <Field
            label="Reason"
            required
            hint="To cancel the appointment too, cancel it from the appointment desk instead."
          >
            <Textarea
              value={reason}
              onChange={(_, d) => setReason(d.value.slice(0, 300))}
              rows={2}
              autoFocus
              resize="vertical"
            />
          </Field>
        </DialogContent>
        <DialogActions>
          <Button onClick={guard.requestClose} disabled={cancel.isPending}>
            Keep it
          </Button>
          <Button
            type="submit"
            appearance="primary"
            className={styles.destructive}
            disabled={!reason.trim() || cancel.isPending}
            // Keeps focus while cancelling (a disabled button drops it); Enter can still submit, which is ignored.
            disabledFocusable={cancel.isPending}
          >
            Cancel consultation
          </Button>
        </DialogActions>
      </DialogBody>
      {guard.dialogs}
    </form>
  );
}

// ------------------------------------------------------------------ end

export function EndSessionDialog({
  open,
  sessionId,
  onClose,
  onEnded,
}: {
  open: boolean;
  sessionId: string;
  onClose: () => void;
  onEnded: (session: TelehealthSessionDetail) => void;
}) {
  const closing = useCloseRequests({ onClose });
  return (
    <Dialog open={open} onOpenChange={closing.onOpenChange} modalType="alert">
      <DialogSurface style={{ maxWidth: 520 }}>
        <CloseRequestScope value={closing}>
          {open && <EndSessionContent sessionId={sessionId} onClose={onClose} onEnded={onEnded} />}
        </CloseRequestScope>
      </DialogSurface>
    </Dialog>
  );
}

function EndSessionContent({
  sessionId,
  onClose,
  onEnded,
}: {
  sessionId: string;
  onClose: () => void;
  onEnded: (session: TelehealthSessionDetail) => void;
}) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [notes, setNotes] = useState('');
  const end = useMutation({
    mutationFn: () => telehealthApi.end(sessionId, notes.trim() || null),
    onSuccess: (session) => {
      void queryClient.invalidateQueries({ queryKey: telehealthKeys.all });
      onEnded(session);
    },
    onError: (error) => notify.error(error, 'Could not end the consultation'),
  });

  const guard = useUnsavedChanges({ dirty: notes !== '', busy: end.isPending, onClose });

  return (
    <form
      onSubmit={(event) => {
        event.preventDefault();
        end.mutate();
      }}
    >
      <DialogBody>
        <DialogTitle>End the consultation?</DialogTitle>
        <DialogContent className={styles.form}>
          <Caption1>
            The call closes for everyone and the patient's link stops working. Notes and the prescription are written
            against the visit afterwards.
          </Caption1>
          <Field label="Call quality notes (optional)" hint="For example: patient's video froze, switched to audio.">
            <Textarea value={notes} onChange={(_, d) => setNotes(d.value.slice(0, 500))} rows={2} resize="vertical" />
          </Field>
        </DialogContent>
        <DialogActions>
          <Button onClick={guard.requestClose} disabled={end.isPending}>
            Continue the call
          </Button>
          <Button
            type="submit"
            appearance="primary"
            className={styles.destructive}
            disabled={end.isPending}
            icon={end.isPending ? <Spinner size="tiny" aria-hidden /> : undefined}
            autoFocus
          >
            End consultation
          </Button>
        </DialogActions>
      </DialogBody>
      {guard.dialogs}
    </form>
  );
}

// ------------------------------------------------------------------ recording consent

/**
 * Records whether the patient agreed to the consultation being recorded (TEL-05). HealthCare360 never records a
 * call; the consent authorises a recording made by other means, and it is kept with the session for audit.
 */
export function RecordingConsentDialog({
  open,
  sessionId,
  onClose,
}: {
  open: boolean;
  sessionId: string;
  onClose: () => void;
}) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const record = useMutation({
    mutationFn: (granted: boolean) => telehealthApi.recordingConsent(sessionId, granted),
    onSuccess: (_, granted) => {
      notify.success(
        granted ? 'Consent to record noted' : 'Patient declined recording',
        `Recorded at ${formatTime(new Date().toISOString())}.`,
      );
      void queryClient.invalidateQueries({ queryKey: telehealthKeys.session(sessionId) });
      onClose();
    },
    onError: (error) => notify.error(error, 'Could not record the consent'),
  });

  return (
    <Dialog open={open} onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface style={{ maxWidth: 520 }}>
        <DialogBody>
          <DialogTitle>Consent to record</DialogTitle>
          <DialogContent>
            Ask the patient: "Do you agree to this consultation being recorded for your medical record?" Record their
            answer. HealthCare360 does not record calls by itself.
          </DialogContent>
          <DialogActions>
            <Button onClick={onClose} disabled={record.isPending}>
              Not now
            </Button>
            <Button onClick={() => record.mutate(false)} disabled={record.isPending}>
              Patient declines
            </Button>
            <Button appearance="primary" onClick={() => record.mutate(true)} disabled={record.isPending}>
              Patient agrees
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}
