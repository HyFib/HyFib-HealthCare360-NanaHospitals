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
  MessageBarTitle,
  Select,
  Subtitle2,
  Switch,
  Text,
  Textarea,
} from '@fluentui/react-components';
import { AddRegular } from '@fluentui/react-icons';
import { formatDateTime, isApiError } from '@hc360/core';
import { DataTable } from '@hc360/ui';
import { queryOptions, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { api } from '@/lib/api';

/** Falls and pressure injuries on the nursing chart (`/api/v1/nursing/admissions/{id}/safety-events`, `nursing.chart`). */

export type SafetyEventKind = 'Fall' | 'PressureInjury';
export type FallInjury = 'None' | 'Minor' | 'Moderate' | 'Major' | 'Death';
export type PressureInjuryStage = 'Stage1' | 'Stage2' | 'Stage3' | 'Stage4' | 'Unstageable' | 'DeepTissue';

export interface NursingSafetyEvent {
  id: string;
  kind: SafetyEventKind;
  occurredAt: string;
  location: string;
  description: string;
  immediateAction: string | null;
  fallInjury: FallInjury | null;
  witnessed: boolean | null;
  stage: PressureInjuryStage | null;
  bodySite: string | null;
  presentOnAdmission: boolean;
  recordedAt: string;
  recordedByName: string | null;
  incidentNumber: string | null;
}

const INJURY_LABELS: Record<FallInjury, string> = {
  None: 'None',
  Minor: 'Minor (dressing, ice, cleaning)',
  Moderate: 'Moderate (sutures, steri-strips, splint)',
  Major: 'Major (fracture, surgery, casting)',
  Death: 'Death',
};

const STAGE_LABELS: Record<PressureInjuryStage, string> = {
  Stage1: 'Stage 1 (intact skin, non-blanchable redness)',
  Stage2: 'Stage 2 (partial thickness)',
  Stage3: 'Stage 3 (full thickness skin loss)',
  Stage4: 'Stage 4 (full thickness tissue loss)',
  Unstageable: 'Unstageable',
  DeepTissue: 'Deep tissue injury',
};

const key = (admissionId: string) => ['nursing', 'admission', admissionId, 'safety-events'] as const;

const safetyEvents = (admissionId: string) =>
  queryOptions({
    queryKey: key(admissionId),
    queryFn: ({ signal }) =>
      api.get<NursingSafetyEvent[]>(`/api/v1/nursing/admissions/${admissionId}/safety-events`, { signal }),
  });

const useStyles = makeStyles({
  head: { display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBlock: '16px 8px' },
  body: { display: 'flex', flexDirection: 'column', gap: '12px' },
  row: { display: 'flex', gap: '12px', flexWrap: 'wrap' },
  cell: { display: 'flex', flexDirection: 'column', gap: '2px', paddingBlock: '4px', whiteSpace: 'normal' },
});

const localNow = () => {
  const d = new Date();
  return new Date(d.getTime() - d.getTimezoneOffset() * 60_000).toISOString().slice(0, 16);
};

/**
 * Falls and pressure injuries during the stay. Every fall, and a pressure injury that developed in hospital, is also an
 * incident in the quality register, raised as it is recorded; one present on admission is documented only.
 */
export function SafetyEvents({ admissionId, canWrite }: { admissionId: string; canWrite: boolean }) {
  const styles = useStyles();
  const events = useQuery(safetyEvents(admissionId));
  const [recording, setRecording] = useState(false);
  return (
    <>
      <div className={styles.head}>
        <Subtitle2>Falls and pressure injuries</Subtitle2>
        {canWrite && (
          <Button icon={<AddRegular />} onClick={() => setRecording(true)}>
            Record a fall or pressure injury
          </Button>
        )}
      </div>
      <DataTable
        ariaLabel="Falls and pressure injuries"
        rows={events.data}
        loading={events.isLoading}
        error={events.error}
        onRetry={() => void events.refetch()}
        getRowId={(e) => e.id}
        columns={[
          {
            key: 'what',
            header: 'What',
            render: (e) => (
              <div className={styles.cell}>
                <Text weight="semibold">
                  {e.kind === 'Fall'
                    ? `Fall, injury ${e.fallInjury?.toLowerCase() ?? '—'}`
                    : `Pressure injury, ${STAGE_LABELS[e.stage ?? 'Stage1'].split(' (')[0]} at ${e.bodySite ?? '—'}`}
                </Text>
                <Caption1>{e.description}</Caption1>
              </div>
            ),
          },
          { key: 'when', header: 'When', width: 180, render: (e) => formatDateTime(e.occurredAt) },
          {
            key: 'incident',
            header: 'Incident',
            width: 190,
            render: (e) => (e.incidentNumber ? e.incidentNumber : e.presentOnAdmission ? 'Present on admission' : '—'),
          },
        ]}
        emptyTitle="None recorded"
        size="small"
      />
      {recording && <RecordSafetyEventDialog admissionId={admissionId} onClose={() => setRecording(false)} />}
    </>
  );
}

export function RecordSafetyEventDialog({ admissionId, onClose }: { admissionId: string; onClose: () => void }) {
  const styles = useStyles();
  const queryClient = useQueryClient();
  const [kind, setKind] = useState<SafetyEventKind>('Fall');
  const [occurredAt, setOccurredAt] = useState(localNow);
  const [location, setLocation] = useState('');
  const [description, setDescription] = useState('');
  const [action, setAction] = useState('');
  const [injury, setInjury] = useState<FallInjury>('None');
  const [witnessed, setWitnessed] = useState(false);
  const [stage, setStage] = useState<PressureInjuryStage>('Stage1');
  const [site, setSite] = useState('');
  const [onAdmission, setOnAdmission] = useState(false);
  const fall = kind === 'Fall';
  const record = useMutation({
    mutationFn: () =>
      api.post<NursingSafetyEvent>(`/api/v1/nursing/admissions/${admissionId}/safety-events`, {
        kind,
        occurredAt: new Date(occurredAt).toISOString(),
        location: location.trim(),
        description: description.trim(),
        immediateAction: action.trim() || null,
        fallInjury: fall ? injury : null,
        witnessed: fall ? witnessed : null,
        stage: fall ? null : stage,
        bodySite: fall ? null : site.trim(),
        presentOnAdmission: !fall && onAdmission,
      }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: key(admissionId) });
      onClose();
    },
  });
  const valid =
    location.trim().length > 0 &&
    description.trim().length >= 10 &&
    !Number.isNaN(new Date(occurredAt).getTime()) &&
    (fall || site.trim().length > 0);
  return (
    <Dialog open onOpenChange={(_, data) => !data.open && !record.isPending && onClose()}>
      <DialogSurface style={{ maxWidth: 600 }}>
        <DialogBody>
          <DialogTitle>Record a fall or pressure injury</DialogTitle>
          <DialogContent className={styles.body}>
            <div className={styles.row}>
              <Field label="What" required>
                <Select value={kind} onChange={(_, data) => setKind(data.value as SafetyEventKind)}>
                  <option value="Fall">A fall</option>
                  <option value="PressureInjury">A pressure injury found</option>
                </Select>
              </Field>
              <Field label={fall ? 'When the patient fell' : 'When it was found'} required>
                <Input type="datetime-local" value={occurredAt} onChange={(_, data) => setOccurredAt(data.value)} />
              </Field>
            </div>
            {fall ? (
              <div className={styles.row}>
                <Field label="Injury" required>
                  <Select value={injury} onChange={(_, data) => setInjury(data.value as FallInjury)}>
                    {(Object.keys(INJURY_LABELS) as FallInjury[]).map((i) => (
                      <option key={i} value={i}>
                        {INJURY_LABELS[i]}
                      </option>
                    ))}
                  </Select>
                </Field>
                <Switch
                  label="Someone saw the fall"
                  checked={witnessed}
                  onChange={(_, data) => setWitnessed(data.checked)}
                />
              </div>
            ) : (
              <>
                <div className={styles.row}>
                  <Field label="Stage" required>
                    <Select value={stage} onChange={(_, data) => setStage(data.value as PressureInjuryStage)}>
                      {(Object.keys(STAGE_LABELS) as PressureInjuryStage[]).map((s) => (
                        <option key={s} value={s}>
                          {STAGE_LABELS[s]}
                        </option>
                      ))}
                    </Select>
                  </Field>
                  <Field label="Where on the body" required>
                    <Input value={site} maxLength={100} onChange={(_, data) => setSite(data.value)} />
                  </Field>
                </div>
                <Switch
                  label="Present on admission (found at the admission skin check)"
                  checked={onAdmission}
                  onChange={(_, data) => setOnAdmission(data.checked)}
                />
              </>
            )}
            <Field label="Where" hint='For example "Bed 4, beside the commode"' required>
              <Input value={location} maxLength={150} onChange={(_, data) => setLocation(data.value)} />
            </Field>
            <Field label="What happened" required>
              <Textarea value={description} maxLength={2000} onChange={(_, data) => setDescription(data.value)} />
            </Field>
            <Field label="What was done">
              <Textarea value={action} maxLength={1000} onChange={(_, data) => setAction(data.value)} />
            </Field>
            {(fall || !onAdmission) && (
              <Caption1>This is also reported to the quality team as an incident when you record it.</Caption1>
            )}
            {record.error && (
              <MessageBar intent="error">
                <MessageBarBody>
                  <MessageBarTitle>Not saved</MessageBarTitle>
                  {isApiError(record.error) ? record.error.message : 'It could not be recorded.'}
                </MessageBarBody>
              </MessageBar>
            )}
          </DialogContent>
          <DialogActions>
            <Button onClick={onClose} disabled={record.isPending}>
              Cancel
            </Button>
            <Button appearance="primary" disabled={!valid || record.isPending} onClick={() => record.mutate()}>
              Record
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}
