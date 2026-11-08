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
  MessageBar,
  MessageBarBody,
  Text,
} from '@fluentui/react-components';
import { formatDateTime } from '@hc360/core';
import { useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { api } from '@/lib/api';
import { PractitionerPicker, type PickedDoctor } from '@/modules/doctor-fees/PractitionerPicker';
import { errorMessage } from '@/modules/lab/common/errors';
import { isoToLocal, localToIso } from '@/modules/quality/downtimeApi';

/** A handover between two doctors (server `DoctorHandoverDto`). */
export interface DoctorHandover {
  id: string;
  fromPractitionerId: string;
  fromName: string | null;
  toPractitionerId: string;
  toName: string | null;
  patientId: string | null;
  startsAt: string;
  until: string;
  endedAt: string | null;
  note: string | null;
  version: number;
}

const handoversKey = ['emr', 'handovers'] as const;

/**
 * The doctor hands their patients over to a covering colleague until a time (up to a week): the colleague may then
 * acknowledge the critical results the doctor ordered (maker-checker, slice 15). Shows the handovers given and received
 * now; either doctor ends one early.
 */
export function HandoverDialog({ onClose }: { onClose: () => void }) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const mine = useQuery({
    queryKey: handoversKey,
    queryFn: ({ signal }) => api.get<DoctorHandover[]>('/api/v1/emr/handovers', { signal }),
  });
  const [doctor, setDoctor] = useState<PickedDoctor | null>(null);
  const [until, setUntil] = useState(() => isoToLocal(new Date(Date.now() + 12 * 3_600_000)));
  const [note, setNote] = useState('');
  const refresh = () => void queryClient.invalidateQueries({ queryKey: handoversKey });

  const record = useMutation({
    mutationFn: () =>
      api.post<DoctorHandover>('/api/v1/emr/handovers', {
        toPractitionerId: doctor!.id,
        until: localToIso(until),
        patientId: null,
        note: note.trim() || null,
      }),
    onSuccess: (h) => {
      notify.success(
        'Handed over',
        `${h.toName ?? 'Your colleague'} covers your patients until ${formatDateTime(h.until)}.`,
      );
      setDoctor(null);
      setNote('');
      refresh();
    },
  });
  const end = useMutation({
    mutationFn: (h: DoctorHandover) =>
      api.post<DoctorHandover>(`/api/v1/emr/handovers/${h.id}/end`, { version: h.version }),
    onSuccess: refresh,
  });

  return (
    <Dialog open onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface style={{ maxWidth: 560 }}>
        <DialogBody>
          <DialogTitle>Hand over my patients</DialogTitle>
          <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
            <Caption1>
              The covering doctor may acknowledge the critical results you ordered until the handover ends. It is
              recorded on each result they acknowledge.
            </Caption1>
            <PractitionerPicker label="Covering doctor" value={doctor} onChange={setDoctor} required />
            <Field label="Until" required hint="Up to 7 days">
              <Input type="datetime-local" value={until} onChange={(_, d) => setUntil(d.value)} />
            </Field>
            <Field label="Note">
              <Input value={note} onChange={(_, d) => setNote(d.value)} maxLength={500} />
            </Field>
            {(record.error ?? end.error) && (
              <MessageBar intent="error">
                <MessageBarBody>
                  {errorMessage(record.error ?? end.error, 'The handover could not be saved.')}
                </MessageBarBody>
              </MessageBar>
            )}
            <Text weight="semibold">Handovers now</Text>
            {mine.data?.length === 0 && <Caption1>None.</Caption1>}
            <ul aria-label="Handovers now" style={{ margin: 0, paddingInlineStart: 18 }}>
              {mine.data?.map((h) => (
                <li key={h.id}>
                  {h.fromName} → {h.toName}, until {formatDateTime(h.until)}
                  {h.note ? ` (${h.note})` : ''}{' '}
                  <Button size="small" appearance="subtle" disabled={end.isPending} onClick={() => end.mutate(h)}>
                    End
                  </Button>
                </li>
              ))}
            </ul>
          </DialogContent>
          <DialogActions>
            <Button onClick={onClose}>Close</Button>
            <Button
              appearance="primary"
              disabled={!doctor || localToIso(until) === null || record.isPending}
              onClick={() => record.mutate()}
            >
              Hand over
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}
