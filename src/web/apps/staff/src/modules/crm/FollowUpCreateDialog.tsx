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
  Select,
  Spinner,
  Textarea,
} from '@fluentui/react-components';
import { isApiError, todayIso } from '@hc360/core';
import { useNotify } from '@hc360/ui';
import { useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { addDays } from '@/modules/hr/shared/dates';
import { UserPicker } from '@/modules/hr/shared/UserPicker';
import type { PickedUser } from '@/modules/hr/shared/users';
import { PatientPicker, type PickedPatient } from '@/modules/patients/PatientPicker';
import { crmApi, crmKeys } from './api';
import { FOLLOW_UP_TYPES, followUpTypeLabel, type FollowUpPriority, type FollowUpType } from './labels';

/** Add a follow-up call by hand (discharge calls are created automatically two days after discharge). */
export function FollowUpCreateDialog({ open, onClose, patient }: { open: boolean; onClose: () => void; patient?: PickedPatient | null }) {
  return (
    <Dialog open={open} onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface style={{ maxWidth: 600 }}>{open && <Body onClose={onClose} initialPatient={patient ?? null} />}</DialogSurface>
    </Dialog>
  );
}

function Body({ onClose, initialPatient }: { onClose: () => void; initialPatient: PickedPatient | null }) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [patient, setPatient] = useState<PickedPatient | null>(initialPatient);
  const [type, setType] = useState<FollowUpType>('PostConsultation');
  const [dueDate, setDueDate] = useState(addDays(todayIso(), 1));
  const [priority, setPriority] = useState<FollowUpPriority>('Normal');
  const [assignee, setAssignee] = useState<PickedUser | null>(null);
  const [notes, setNotes] = useState('');
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [busy, setBusy] = useState(false);

  const save = async () => {
    const next: Record<string, string> = {};
    if (!patient) next.patientId = 'Choose the patient.';
    if (!dueDate || dueDate < todayIso()) next.dueDate = 'The due date cannot be in the past.';
    setErrors(next);
    if (!patient || Object.keys(next).length > 0) {
      return;
    }
    setBusy(true);
    try {
      await crmApi.createFollowUp({ patientId: patient.id, visitId: null, type, dueDate, assigneeUserId: assignee?.id ?? null, priority, notes: notes.trim() || null });
      notify.success('Follow-up added', `${patient.fullName} · ${followUpTypeLabel[type]}`);
      void queryClient.invalidateQueries({ queryKey: crmKeys.followUps });
      onClose();
    } catch (error) {
      if (isApiError(error) && error.isValidation) {
        setErrors(Object.fromEntries(Object.entries(error.fieldErrors).map(([k, v]) => [k, v[0] ?? ''])));
      } else {
        notify.error(error, 'Could not add the follow-up');
      }
    } finally {
      setBusy(false);
    }
  };

  return (
    <form
      onSubmit={(event) => {
        event.preventDefault();
        void save();
      }}
      noValidate
    >
      <DialogBody>
        <DialogTitle>Add a follow-up call</DialogTitle>
        <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
          <PatientPicker value={patient} onChange={setPatient} required validationMessage={errors.patientId} />
          <Field label="Reason for the call">
            <Select value={type} onChange={(_, d) => setType(d.value as FollowUpType)}>
              {FOLLOW_UP_TYPES.map((t) => (
                <option key={t} value={t}>
                  {followUpTypeLabel[t]}
                </option>
              ))}
            </Select>
          </Field>
          <div style={{ display: 'flex', gap: 12, flexWrap: 'wrap' }}>
            <Field label="Call on" required validationMessage={errors.dueDate} validationState={errors.dueDate ? 'error' : 'none'}>
              <Input type="date" value={dueDate} min={todayIso()} onChange={(_, d) => setDueDate(d.value)} />
            </Field>
            <Field label="Priority">
              <Select value={priority} onChange={(_, d) => setPriority(d.value as FollowUpPriority)}>
                <option value="Normal">Normal</option>
                <option value="High">High</option>
              </Select>
            </Field>
          </div>
          <UserPicker label="Assign to" value={assignee} onChange={setAssignee} hint="Leave empty for anyone on the team." />
          <Field label="Notes for the caller">
            <Textarea value={notes} onChange={(_, d) => setNotes(d.value)} rows={2} maxLength={1000} />
          </Field>
        </DialogContent>
        <DialogActions>
          <Button onClick={onClose} disabled={busy}>
            Cancel
          </Button>
          <Button type="submit" appearance="primary" disabled={busy} icon={busy ? <Spinner size="tiny" /> : undefined}>
            Add follow-up
          </Button>
        </DialogActions>
      </DialogBody>
    </form>
  );
}
