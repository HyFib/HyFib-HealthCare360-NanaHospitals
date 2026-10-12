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
  Spinner,
  Textarea,
  tokens,
} from '@fluentui/react-components';
import { AttachRegular } from '@fluentui/react-icons';
import { useMutation } from '@tanstack/react-query';
import { useState, type ReactNode } from 'react';
import { usePermission } from '@/lib/permissions';
import { fromZonedInput, toZonedInput } from '@/modules/lab/common/datetime';
import { errorMessage, hasErrorCode } from '@/modules/lab/common/errors';
import { DEVICE_FILE_TYPES, deviceFileProblem, uploadFile } from '@/modules/lab/common/files';
import { cardiologyApi, type CardiologyStudyStatus, type CardiologyStudyType } from './api';
import { studyTypeLongLabel } from './labels';
import { useUnsavedChanges, useValuesChanged } from '@hc360/ui';

const useStyles = makeStyles({
  content: { display: 'flex', flexDirection: 'column', gap: '12px' },
  muted: { color: tokens.colorNeutralForeground3 },
  list: { margin: 0, paddingLeft: '18px' },
  bad: { color: tokens.colorPaletteRedForeground1 },
});

/** What the dialogs need to know about a cardiology study. */
export interface CardiologyStudyRef {
  id: string;
  patientId: string;
  studyName: string;
  studyType: CardiologyStudyType;
  patientName: string;
  status: CardiologyStudyStatus;
  scheduledAt?: string | null;
  room?: string | null;
  deviceName?: string | null;
  fileCount: number;
}

function Shell({
  study,
  title,
  busy,
  error,
  errorFallback,
  submitLabel,
  canSubmit,
  dirty,
  onSubmit,
  onClose,
  children,
}: {
  study: CardiologyStudyRef;
  title: string;
  /** The form has unsaved changes: closing it asks first (SRS UX-08). */
  dirty: boolean;
  busy: boolean;
  error: unknown;
  errorFallback: string;
  submitLabel: string;
  canSubmit: boolean;
  onSubmit: () => void;
  onClose: () => void;
  children: ReactNode;
}) {
  const styles = useStyles();
  const guard = useUnsavedChanges({ dirty, busy, onClose });
  return (
    <Dialog open onOpenChange={guard.onOpenChange}>
      <DialogSurface style={{ maxWidth: 560 }}>
        <form
          onSubmit={(event) => {
            event.preventDefault();
            if (canSubmit && !busy) {
              onSubmit();
            }
          }}
        >
          <DialogBody>
            <DialogTitle>
              {title}
              <Caption1 block className={styles.muted}>
                {study.studyName} · {study.patientName}
              </Caption1>
            </DialogTitle>
            <DialogContent className={styles.content}>
              {Boolean(error) && (
                <MessageBar intent="error">
                  <MessageBarBody>{errorMessage(error, errorFallback)}</MessageBarBody>
                </MessageBar>
              )}
              {children}
            </DialogContent>
            <DialogActions>
              <Button onClick={guard.requestClose} disabled={busy}>
                Cancel
              </Button>
              <Button
                type="submit"
                appearance="primary"
                disabled={!canSubmit || busy}
                // Keeps focus while busy (a disabled button drops it); Enter can still submit, which is ignored.
                disabledFocusable={busy}
                icon={busy ? <Spinner size="tiny" /> : undefined}
              >
                {submitLabel}
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
        {guard.dialogs}
      </DialogSurface>
    </Dialog>
  );
}

export function ScheduleCardiologyDialog({
  study,
  onClose,
  onDone,
}: {
  study: CardiologyStudyRef;
  onClose: () => void;
  onDone: () => void;
}) {
  const [at, setAt] = useState(() => toZonedInput(study.scheduledAt ? new Date(study.scheduledAt) : new Date()));
  const [room, setRoom] = useState(study.room ?? '');
  const dirty = useValuesChanged({ at, room });
  const instant = fromZonedInput(at);
  const schedule = useMutation({
    mutationFn: () => cardiologyApi.schedule(study.id, instant!, room.trim() || null),
    onSuccess: onDone,
  });
  return (
    <Shell
      study={study}
      title="Schedule study"
      busy={schedule.isPending}
      error={schedule.error}
      errorFallback="The study could not be scheduled."
      submitLabel="Schedule"
      canSubmit={Boolean(instant)}
      dirty={dirty}
      onSubmit={() => schedule.mutate()}
      onClose={onClose}
    >
      <Field label="Appointment (hospital time)" required>
        <Input type="datetime-local" value={at} onChange={(_, data) => setAt(data.value)} autoFocus />
      </Field>
      <Field label="Room">
        <Input
          value={room}
          onChange={(_, data) => setRoom(data.value.slice(0, 64))}
          placeholder="e.g. Cardiology lab 2"
        />
      </Field>
    </Shell>
  );
}

export function StartCardiologyDialog({
  study,
  onClose,
  onDone,
}: {
  study: CardiologyStudyRef;
  onClose: () => void;
  onDone: () => void;
}) {
  const [device, setDevice] = useState(study.deviceName ?? '');
  const dirty = useValuesChanged(device);
  const start = useMutation({
    mutationFn: () => cardiologyApi.start(study.id, device.trim() || null),
    onSuccess: onDone,
  });
  return (
    <Shell
      study={study}
      title={`Start ${studyTypeLongLabel[study.studyType]}`}
      busy={start.isPending}
      error={start.error}
      errorFallback="The study could not be started."
      submitLabel="Start recording"
      canSubmit
      dirty={dirty}
      onSubmit={() => start.mutate()}
      onClose={onClose}
    >
      <Field label="Machine" hint="Printed on the report for traceability">
        <Input
          value={device}
          onChange={(_, data) => setDevice(data.value.slice(0, 100))}
          placeholder="e.g. BPL Cardiart 9108"
          autoFocus
        />
      </Field>
    </Shell>
  );
}

/**
 * Uploads the device output (PDF tracing, echo images, DICOM or SCP-ECG files) to the encrypted file store against the
 * patient, then attaches it to the study.
 */
export function AttachFilesDialog({
  study,
  onClose,
  onDone,
}: {
  study: CardiologyStudyRef;
  onClose: () => void;
  onDone: () => void;
}) {
  const styles = useStyles();
  const canUpload = usePermission('files.write');
  const [files, setFiles] = useState<File[]>([]);
  const dirty = files.length > 0;
  const problems = files.map(deviceFileProblem).filter((problem): problem is string => problem !== null);
  const attach = useMutation({
    mutationFn: async () => {
      const stored = [];
      for (const file of files) {
        stored.push(await uploadFile(file, { category: 'cardiology-device', patientId: study.patientId }));
      }
      return cardiologyApi.attachFiles(
        study.id,
        stored.map((item) => item.id),
      );
    },
    onSuccess: onDone,
  });
  return (
    <Shell
      study={study}
      title="Attach the tracing"
      busy={attach.isPending}
      error={attach.error}
      errorFallback="The files could not be attached."
      submitLabel={files.length > 1 ? `Attach ${files.length} files` : 'Attach'}
      canSubmit={canUpload && files.length > 0 && files.length <= 20 && problems.length === 0}
      dirty={dirty}
      onSubmit={() => attach.mutate()}
      onClose={onClose}
    >
      {canUpload ? (
        <>
          <Field
            label="Files from the machine (PDF, images, DICOM or SCP-ECG, up to 25 MB each)"
            hint="Select several files at once if the machine exported more than one"
          >
            <input
              type="file"
              accept={DEVICE_FILE_TYPES}
              multiple
              onChange={(event) => setFiles(Array.from(event.target.files ?? []))}
              aria-label="Device output files"
            />
          </Field>
          {files.length > 0 && (
            <ul className={styles.list}>
              {files.map((file) => (
                <li key={`${file.name}-${file.size}`}>
                  <AttachRegular aria-hidden /> {file.name} ({Math.ceil(file.size / 1024)} KB)
                </li>
              ))}
            </ul>
          )}
          {problems.map((problem) => (
            <Caption1 key={problem} className={styles.bad} block>
              {problem}
            </Caption1>
          ))}
          {files.length > 20 && <Caption1 className={styles.bad}>Attach at most 20 files at a time.</Caption1>}
        </>
      ) : (
        <Caption1>You cannot upload files. Ask an administrator for document upload access.</Caption1>
      )}
    </Shell>
  );
}

export function CompleteCardiologyDialog({
  study,
  onClose,
  onDone,
}: {
  study: CardiologyStudyRef;
  onClose: () => void;
  onDone: () => void;
}) {
  const styles = useStyles();
  const [notes, setNotes] = useState('');
  const dirty = notes !== '';
  const complete = useMutation({
    mutationFn: () => cardiologyApi.complete(study.id, notes.trim() || null),
    onSuccess: onDone,
  });
  const noTracing = hasErrorCode(complete.error, 'cardiology.no_tracing') || study.fileCount === 0;
  return (
    <Shell
      study={study}
      title="Complete study"
      busy={complete.isPending}
      error={hasErrorCode(complete.error, 'cardiology.no_tracing') ? null : complete.error}
      errorFallback="The study could not be completed."
      submitLabel="Complete study"
      canSubmit={study.fileCount > 0}
      dirty={dirty}
      onSubmit={() => complete.mutate()}
      onClose={onClose}
    >
      {noTracing ? (
        <MessageBar intent="warning">
          <MessageBarBody>
            Attach the device output (tracing, loops or report) first. The cardiologist reports from it.
          </MessageBarBody>
        </MessageBar>
      ) : (
        <Caption1 className={styles.muted}>
          {study.fileCount} {study.fileCount === 1 ? 'file is' : 'files are'} attached. The study moves to the
          cardiologist's reporting worklist.
        </Caption1>
      )}
      <Field label="Technician notes">
        <Textarea
          value={notes}
          onChange={(_, data) => setNotes(data.value.slice(0, 1000))}
          rows={3}
          resize="vertical"
          autoFocus
        />
      </Field>
    </Shell>
  );
}
