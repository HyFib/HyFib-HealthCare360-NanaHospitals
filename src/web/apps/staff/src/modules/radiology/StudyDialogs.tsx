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
  Radio,
  RadioGroup,
  Spinner,
  Switch,
  Textarea,
  tokens,
} from '@fluentui/react-components';
import { useMutation } from '@tanstack/react-query';
import { useState } from 'react';
import { fromZonedInput, toZonedInput } from '@/modules/lab/common/datetime';
import { errorMessage } from '@/modules/lab/common/errors';
import { radiologyApi, REPEAT_REASONS, type ImageRepeatReason, type ImagingStudyStatus } from './api';
import { repeatReasonLabel } from './labels';

const useStyles = makeStyles({
  content: { display: 'flex', flexDirection: 'column', gap: '12px' },
  row: { display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(180px, 1fr))', gap: '12px' },
  timeRow: { display: 'flex', gap: '8px', alignItems: 'flex-end' },
  muted: { color: tokens.colorNeutralForeground3 },
  reasons: { display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(170px, 1fr))' },
});

/** What the dialogs need to know about a study (list rows and the detail both have it). */
export interface StudyRef {
  id: string;
  studyName: string;
  patientName: string;
  status: ImagingStudyStatus;
  scheduledAt?: string | null;
  room?: string | null;
  stationAeTitle?: string | null;
}

function DialogHeading({ title, study }: { title: string; study: StudyRef }) {
  const styles = useStyles();
  return (
    <DialogTitle>
      {title}
      <Caption1 block className={styles.muted}>
        {study.studyName} · {study.patientName}
      </Caption1>
    </DialogTitle>
  );
}

export function ScheduleDialog({
  study,
  onClose,
  onDone,
}: {
  study: StudyRef;
  onClose: () => void;
  onDone: () => void;
}) {
  const styles = useStyles();
  const [at, setAt] = useState(() => toZonedInput(study.scheduledAt ? new Date(study.scheduledAt) : new Date()));
  const [room, setRoom] = useState(study.room ?? '');
  const [station, setStation] = useState(study.stationAeTitle ?? '');
  const instant = fromZonedInput(at);
  const stationProblem = station && !/^[A-Za-z0-9_-]{1,16}$/.test(station) ? 'Up to 16 letters, digits, - or _.' : null;
  const schedule = useMutation({
    mutationFn: () =>
      radiologyApi.schedule(study.id, {
        scheduledAt: instant!,
        room: room.trim() || null,
        stationAeTitle: station.trim().toUpperCase() || null,
      }),
    onSuccess: onDone,
  });
  return (
    <Dialog open onOpenChange={(_, data) => !data.open && !schedule.isPending && onClose()}>
      <DialogSurface style={{ maxWidth: 560 }}>
        <form
          onSubmit={(event) => {
            event.preventDefault();
            if (instant && !stationProblem && !schedule.isPending) {
              schedule.mutate();
            }
          }}
        >
          <DialogBody>
            <DialogHeading title="Schedule study" study={study} />
            <DialogContent className={styles.content}>
              {schedule.error && (
                <MessageBar intent="error">
                  <MessageBarBody>{errorMessage(schedule.error, 'The study could not be scheduled.')}</MessageBarBody>
                </MessageBar>
              )}
              <Field
                label="Appointment (hospital time)"
                required
                validationMessage={!instant ? 'Enter the date and time.' : undefined}
              >
                <Input type="datetime-local" value={at} onChange={(_, data) => setAt(data.value)} autoFocus />
              </Field>
              <div className={styles.row}>
                <Field label="Room">
                  <Input
                    value={room}
                    onChange={(_, data) => setRoom(data.value.slice(0, 64))}
                    placeholder="e.g. CT room 1"
                  />
                </Field>
                <Field
                  label="Modality station (AE title)"
                  hint="Sends the study to that machine's worklist"
                  validationMessage={stationProblem ?? undefined}
                >
                  <Input
                    value={station}
                    onChange={(_, data) => setStation(data.value.slice(0, 16))}
                    placeholder="e.g. CT01"
                  />
                </Field>
              </div>
            </DialogContent>
            <DialogActions>
              <Button onClick={onClose} disabled={schedule.isPending}>
                Cancel
              </Button>
              <Button
                type="submit"
                appearance="primary"
                disabled={!instant || Boolean(stationProblem) || schedule.isPending}
                // Keeps focus while busy (a disabled button drops it); Enter can still submit, which is ignored.
                disabledFocusable={schedule.isPending}
                icon={schedule.isPending ? <Spinner size="tiny" aria-hidden /> : undefined}
              >
                Schedule
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
      </DialogSurface>
    </Dialog>
  );
}

export function AcquireDialog({
  study,
  onClose,
  onDone,
}: {
  study: StudyRef;
  onClose: () => void;
  onDone: () => void;
}) {
  const styles = useStyles();
  const [contrast, setContrast] = useState(false);
  const [agent, setAgent] = useState('');
  const [volume, setVolume] = useState('');
  const [dose, setDose] = useState('');
  const [notes, setNotes] = useState('');
  const volumeNumber = volume.trim() ? Number(volume) : null;
  const volumeProblem =
    volumeNumber !== null && (!Number.isFinite(volumeNumber) || volumeNumber < 0 || volumeNumber > 1000)
      ? 'Enter 0 to 1000 mL.'
      : null;
  const agentProblem = contrast && !agent.trim() ? 'Enter the contrast agent used.' : null;
  const acquire = useMutation({
    mutationFn: () =>
      radiologyApi.acquire(study.id, {
        contrastUsed: contrast,
        contrastAgent: contrast ? agent.trim() : null,
        contrastVolumeMl: contrast ? volumeNumber : null,
        doseNote: dose.trim() || null,
        notes: notes.trim() || null,
      }),
    onSuccess: onDone,
  });
  const valid = !agentProblem && !volumeProblem;
  return (
    <Dialog open onOpenChange={(_, data) => !data.open && !acquire.isPending && onClose()}>
      <DialogSurface style={{ maxWidth: 600 }}>
        <form
          onSubmit={(event) => {
            event.preventDefault();
            if (valid && !acquire.isPending) {
              acquire.mutate();
            }
          }}
        >
          <DialogBody>
            <DialogHeading title="Complete scan" study={study} />
            <DialogContent className={styles.content}>
              {acquire.error && (
                <MessageBar intent="error">
                  <MessageBarBody>{errorMessage(acquire.error, 'The study could not be completed.')}</MessageBarBody>
                </MessageBar>
              )}
              <Switch checked={contrast} onChange={(_, data) => setContrast(data.checked)} label="Contrast was given" />
              {contrast && (
                <div className={styles.row}>
                  <Field label="Contrast agent" required validationMessage={agentProblem ?? undefined}>
                    <Input
                      value={agent}
                      onChange={(_, data) => setAgent(data.value.slice(0, 100))}
                      placeholder="e.g. Iohexol 350"
                      autoFocus
                    />
                  </Field>
                  <Field label="Volume (mL)" validationMessage={volumeProblem ?? undefined}>
                    <Input value={volume} onChange={(_, data) => setVolume(data.value)} inputMode="decimal" />
                  </Field>
                </div>
              )}
              <Field label="Radiation dose" hint="e.g. CTDIvol 12 mGy, DLP 450 mGy·cm">
                <Input value={dose} onChange={(_, data) => setDose(data.value.slice(0, 200))} />
              </Field>
              <Field label="Technologist notes">
                <Textarea
                  value={notes}
                  onChange={(_, data) => setNotes(data.value.slice(0, 1000))}
                  rows={2}
                  resize="vertical"
                />
              </Field>
            </DialogContent>
            <DialogActions>
              <Button onClick={onClose} disabled={acquire.isPending}>
                Cancel
              </Button>
              <Button
                type="submit"
                appearance="primary"
                disabled={!valid || acquire.isPending}
                // Keeps focus while busy (a disabled button drops it); Enter can still submit, which is ignored.
                disabledFocusable={acquire.isPending}
                icon={acquire.isPending ? <Spinner size="tiny" aria-hidden /> : undefined}
              >
                Complete scan
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
      </DialogSurface>
    </Dialog>
  );
}

export function RepeatDialog({ study, onClose, onDone }: { study: StudyRef; onClose: () => void; onDone: () => void }) {
  const styles = useStyles();
  const [reason, setReason] = useState<ImageRepeatReason | ''>('');
  const [note, setNote] = useState('');
  const needsNote = reason === 'Other' && !note.trim();
  const record = useMutation({
    mutationFn: () => radiologyApi.recordRepeat(study.id, reason as ImageRepeatReason, note.trim() || null),
    onSuccess: onDone,
  });
  return (
    <Dialog open onOpenChange={(_, data) => !data.open && !record.isPending && onClose()}>
      <DialogSurface style={{ maxWidth: 600 }}>
        <form
          onSubmit={(event) => {
            event.preventDefault();
            if (reason && !needsNote && !record.isPending) {
              record.mutate();
            }
          }}
        >
          <DialogBody>
            <DialogHeading title="Record a repeated image" study={study} />
            <DialogContent className={styles.content}>
              <Caption1 className={styles.muted}>
                Repeats are counted in the reject analysis used for radiographer training and equipment checks.
              </Caption1>
              {record.error && (
                <MessageBar intent="error">
                  <MessageBarBody>{errorMessage(record.error, 'The repeat could not be recorded.')}</MessageBarBody>
                </MessageBar>
              )}
              <Field label="Why was the image repeated?" required>
                <RadioGroup
                  value={reason}
                  onChange={(_, data) => setReason(data.value as ImageRepeatReason)}
                  className={styles.reasons}
                >
                  {REPEAT_REASONS.map((value) => (
                    <Radio key={value} value={value} label={repeatReasonLabel[value]} />
                  ))}
                </RadioGroup>
              </Field>
              <Field label="Note" required={reason === 'Other'}>
                <Input value={note} onChange={(_, data) => setNote(data.value.slice(0, 500))} />
              </Field>
            </DialogContent>
            <DialogActions>
              <Button onClick={onClose} disabled={record.isPending}>
                Cancel
              </Button>
              <Button
                type="submit"
                appearance="primary"
                disabled={!reason || needsNote || record.isPending}
                // Keeps focus while busy (a disabled button drops it); Enter can still submit, which is ignored.
                disabledFocusable={record.isPending}
                icon={record.isPending ? <Spinner size="tiny" aria-hidden /> : undefined}
              >
                Record repeat
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
      </DialogSurface>
    </Dialog>
  );
}
