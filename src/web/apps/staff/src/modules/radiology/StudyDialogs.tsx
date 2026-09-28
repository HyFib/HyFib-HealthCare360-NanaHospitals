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
import { bridge, isApiError } from '@hc360/core';
import { useNotify } from '@hc360/ui';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useNavigate } from '@tanstack/react-router';
import { useState, type ReactNode } from 'react';
import { fromZonedInput, toZonedInput } from '@/modules/lab/common/datetime';
import { errorMessage } from '@/modules/lab/common/errors';
import { ReasonDialog } from '@/modules/lab/common/ReasonDialog';
import { radiologyApi, radiologyKeys, REPEAT_REASONS, type ImageRepeatReason, type ImagingStudyStatus } from './api';
import { repeatReasonLabel } from './labels';
import type { StudyAction } from './workflow';

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

function ScheduleDialog({ study, onClose, onDone }: { study: StudyRef; onClose: () => void; onDone: () => void }) {
  const styles = useStyles();
  const [at, setAt] = useState(() => toZonedInput(study.scheduledAt ? new Date(study.scheduledAt) : new Date()));
  const [room, setRoom] = useState(study.room ?? '');
  const [station, setStation] = useState(study.stationAeTitle ?? '');
  const instant = fromZonedInput(at);
  const stationProblem = station && !/^[A-Za-z0-9_-]{1,16}$/.test(station) ? 'Up to 16 letters, digits, - or _.' : null;
  const schedule = useMutation({
    mutationFn: () => radiologyApi.schedule(study.id, { scheduledAt: instant!, room: room.trim() || null, stationAeTitle: station.trim().toUpperCase() || null }),
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
              <Field label="Appointment (hospital time)" required validationMessage={!instant ? 'Enter the date and time.' : undefined}>
                <Input type="datetime-local" value={at} onChange={(_, data) => setAt(data.value)} autoFocus />
              </Field>
              <div className={styles.row}>
                <Field label="Room">
                  <Input value={room} onChange={(_, data) => setRoom(data.value.slice(0, 64))} placeholder="e.g. CT room 1" />
                </Field>
                <Field label="Modality station (AE title)" hint="Sends the study to that machine's worklist" validationMessage={stationProblem ?? undefined}>
                  <Input value={station} onChange={(_, data) => setStation(data.value.slice(0, 16))} placeholder="e.g. CT01" />
                </Field>
              </div>
            </DialogContent>
            <DialogActions>
              <Button onClick={onClose} disabled={schedule.isPending}>
                Cancel
              </Button>
              <Button type="submit" appearance="primary" disabled={!instant || Boolean(stationProblem) || schedule.isPending} icon={schedule.isPending ? <Spinner size="tiny" /> : undefined}>
                Schedule
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
      </DialogSurface>
    </Dialog>
  );
}

function AcquireDialog({ study, onClose, onDone }: { study: StudyRef; onClose: () => void; onDone: () => void }) {
  const styles = useStyles();
  const [contrast, setContrast] = useState(false);
  const [agent, setAgent] = useState('');
  const [volume, setVolume] = useState('');
  const [dose, setDose] = useState('');
  const [notes, setNotes] = useState('');
  const volumeNumber = volume.trim() ? Number(volume) : null;
  const volumeProblem = volumeNumber !== null && (!Number.isFinite(volumeNumber) || volumeNumber < 0 || volumeNumber > 1000) ? 'Enter 0 to 1000 mL.' : null;
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
                    <Input value={agent} onChange={(_, data) => setAgent(data.value.slice(0, 100))} placeholder="e.g. Iohexol 350" autoFocus />
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
                <Textarea value={notes} onChange={(_, data) => setNotes(data.value.slice(0, 1000))} rows={2} resize="vertical" />
              </Field>
            </DialogContent>
            <DialogActions>
              <Button onClick={onClose} disabled={acquire.isPending}>
                Cancel
              </Button>
              <Button type="submit" appearance="primary" disabled={!valid || acquire.isPending} icon={acquire.isPending ? <Spinner size="tiny" /> : undefined}>
                Complete scan
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
      </DialogSurface>
    </Dialog>
  );
}

function RepeatDialog({ study, onClose, onDone }: { study: StudyRef; onClose: () => void; onDone: () => void }) {
  const styles = useStyles();
  const [reason, setReason] = useState<ImageRepeatReason | ''>('');
  const [note, setNote] = useState('');
  const needsNote = reason === 'Other' && !note.trim();
  const record = useMutation({ mutationFn: () => radiologyApi.recordRepeat(study.id, reason as ImageRepeatReason, note.trim() || null), onSuccess: onDone });
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
              <Caption1 className={styles.muted}>Repeats are counted in the reject analysis used for radiographer training and equipment checks.</Caption1>
              {record.error && (
                <MessageBar intent="error">
                  <MessageBarBody>{errorMessage(record.error, 'The repeat could not be recorded.')}</MessageBarBody>
                </MessageBar>
              )}
              <Field label="Why was the image repeated?" required>
                <RadioGroup value={reason} onChange={(_, data) => setReason(data.value as ImageRepeatReason)} className={styles.reasons}>
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
              <Button type="submit" appearance="primary" disabled={!reason || needsNote || record.isPending} icon={record.isPending ? <Spinner size="tiny" /> : undefined}>
                Record repeat
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
      </DialogSurface>
    </Dialog>
  );
}

/** Opens the PACS viewer outside the app (HC360 Desktop opens the system browser or viewer). */
export async function openViewerUrl(url: string): Promise<void> {
  if (bridge.isDesktop) {
    await bridge.openExternal(url);
    return;
  }
  window.open(url, '_blank', 'noopener,noreferrer');
}

/**
 * Technologist actions on an imaging study (schedule, arrival, scan, repeats, cancel, PACS linking, viewer) with their
 * dialogs. Used by the worklist rows and the study page so both behave the same.
 */
export function useStudyActions(): {
  run: (action: StudyAction, study: StudyRef) => void;
  openImages: (studyId: string) => Promise<void>;
  busy: boolean;
  dialogs: ReactNode;
} {
  const notify = useNotify();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const [dialog, setDialog] = useState<{ action: 'schedule' | 'acquire' | 'repeat' | 'cancel'; study: StudyRef } | null>(null);

  const refresh = () => void queryClient.invalidateQueries({ queryKey: radiologyKeys.all });
  const done = (message: string) => {
    setDialog(null);
    notify.success(message);
    refresh();
  };

  const quick = useMutation({
    mutationFn: ({ action, study }: { action: 'arrive' | 'start' | 'reconcile'; study: StudyRef }) =>
      action === 'arrive' ? radiologyApi.arrive(study.id) : action === 'start' ? radiologyApi.start(study.id) : radiologyApi.reconcile(study.id),
    onSuccess: (detail, { action }) => {
      refresh();
      if (action === 'reconcile') {
        notify.success('Images linked from PACS', detail.seriesCount ? `${detail.seriesCount} series, ${detail.instanceCount ?? 0} images` : undefined);
      } else {
        notify.success(action === 'arrive' ? 'Patient marked as arrived' : 'Scan started');
      }
    },
    onError: (error, { action }) => {
      if (action === 'reconcile' && isApiError(error) && error.code?.startsWith('pacs.')) {
        notify.info('Images not linked', error.userMessage);
      } else {
        notify.error(error, 'The study could not be updated');
      }
      refresh();
    },
  });

  const cancel = useMutation({
    mutationFn: ({ study, reason }: { study: StudyRef; reason: string }) => radiologyApi.cancel(study.id, reason),
    onSuccess: () => done('Study cancelled'),
    onError: (error) => notify.error(error, 'The study could not be cancelled'),
  });

  const run = (action: StudyAction, study: StudyRef) => {
    switch (action) {
      case 'arrive':
      case 'start':
      case 'reconcile':
        quick.mutate({ action, study });
        break;
      case 'report':
        void navigate({ to: '/radiology/studies/$studyId', params: { studyId: study.id } });
        break;
      default:
        setDialog({ action, study });
    }
  };

  const openImages = async (studyId: string) => {
    try {
      const link = await radiologyApi.viewer(studyId);
      await openViewerUrl(link.url);
    } catch (error) {
      if (isApiError(error) && error.code?.startsWith('pacs.')) {
        notify.info('Images are not available', error.userMessage);
      } else {
        notify.error(error, 'Could not open the images');
      }
    }
  };

  const dialogs = (
    <>
      {dialog?.action === 'schedule' && <ScheduleDialog study={dialog.study} onClose={() => setDialog(null)} onDone={() => done('Study scheduled')} />}
      {dialog?.action === 'acquire' && <AcquireDialog study={dialog.study} onClose={() => setDialog(null)} onDone={() => done('Scan completed. The study is on the reporting worklist.')} />}
      {dialog?.action === 'repeat' && <RepeatDialog study={dialog.study} onClose={() => setDialog(null)} onDone={() => done('Repeat recorded')} />}
      {dialog?.action === 'cancel' && (
        <ReasonDialog
          key={dialog.study.id}
          open
          title={`Cancel ${dialog.study.studyName}?`}
          description={<Caption1>The charge is reversed on the bill. A study that has been reported cannot be cancelled.</Caption1>}
          suggestions={['Patient declined', 'Doctor cancelled the order', 'Patient not fit for the scan', 'Duplicate order']}
          confirmLabel="Cancel study"
          destructive
          busy={cancel.isPending}
          onCancel={() => setDialog(null)}
          onConfirm={(reason) => cancel.mutate({ study: dialog.study, reason })}
        />
      )}
    </>
  );

  return { run, openImages, busy: quick.isPending, dialogs };
}
