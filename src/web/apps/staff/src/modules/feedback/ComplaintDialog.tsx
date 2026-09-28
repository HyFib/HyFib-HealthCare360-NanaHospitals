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
  Radio,
  RadioGroup,
  Select,
  Spinner,
  Textarea,
} from '@fluentui/react-components';
import { isApiError, normalizePhone } from '@hc360/core';
import { useNotify } from '@hc360/ui';
import { useQueryClient } from '@tanstack/react-query';
import { useId, useState } from 'react';
import { toOptions, useDepartments } from '@/lib/lookups';
import { UserPicker } from '@/modules/hr/shared/UserPicker';
import type { PickedUser } from '@/modules/hr/shared/users';
import { PatientPicker, type PickedPatient } from '@/modules/patients/PatientPicker';
import { feedbackApi, feedbackKeys } from './api';
import { ASPECTS, aspectLabel, complaintSourceLabel, MANUAL_COMPLAINT_SOURCES, type ComplaintPriority, type ComplaintSource } from './labels';
import { SLA_HOURS } from './rules';

/** Areas offered as the complaint category (free text is allowed too). */
const CATEGORY_SUGGESTIONS = [...ASPECTS.filter((a) => a !== 'Overall').map((a) => aspectLabel[a]), 'Waiting time', 'Staff behaviour', 'Facilities', 'Pharmacy', 'Other'];

type Errors = Partial<Record<'description' | 'contactPhone' | 'patientId' | 'departmentId' | 'ownerUserId' | 'category' | 'contactName', string>>;

/** Record a complaint made in person, by phone, e-mail or through the portal (low feedback scores raise one automatically). */
export function ComplaintDialog({
  open,
  onClose,
  onCreated,
  patient,
}: {
  open: boolean;
  onClose: () => void;
  onCreated?: (id: string) => void;
  patient?: PickedPatient | null;
}) {
  return (
    <Dialog open={open} onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface style={{ maxWidth: 680 }}>{open && <Body onClose={onClose} onCreated={onCreated} initialPatient={patient ?? null} />}</DialogSurface>
    </Dialog>
  );
}

function Body({ onClose, onCreated, initialPatient }: { onClose: () => void; onCreated?: (id: string) => void; initialPatient: PickedPatient | null }) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const departments = useDepartments();
  const listId = useId();
  const [source, setSource] = useState<ComplaintSource>('WalkIn');
  const [patient, setPatient] = useState<PickedPatient | null>(initialPatient);
  const [departmentId, setDepartmentId] = useState('');
  const [category, setCategory] = useState('');
  const [description, setDescription] = useState('');
  const [contactName, setContactName] = useState('');
  const [contactPhone, setContactPhone] = useState('');
  const [priority, setPriority] = useState<ComplaintPriority>('Normal');
  const [owner, setOwner] = useState<PickedUser | null>(null);
  const [errors, setErrors] = useState<Errors>({});
  const [busy, setBusy] = useState(false);

  const save = async () => {
    const next: Errors = {};
    if (!description.trim()) next.description = 'Describe the complaint in the complainant’s words.';
    if (contactPhone.trim() && normalizePhone(contactPhone) === null) next.contactPhone = 'Enter a valid phone number.';
    setErrors(next);
    if (Object.keys(next).length > 0) {
      return;
    }
    setBusy(true);
    try {
      const id = await feedbackApi.createComplaint({
        source,
        patientId: patient?.id ?? null,
        departmentId: departmentId || null,
        category: category.trim() || null,
        description: description.trim(),
        contactName: contactName.trim() || null,
        contactPhone: contactPhone.trim() ? normalizePhone(contactPhone) : null,
        priority,
        ownerUserId: owner?.id ?? null,
      });
      notify.success('Complaint recorded', `Resolve within ${SLA_HOURS[priority]} hours. The complaint handlers have been alerted.`);
      void queryClient.invalidateQueries({ queryKey: feedbackKeys.complaints });
      void queryClient.invalidateQueries({ queryKey: feedbackKeys.analytics });
      onClose();
      onCreated?.(id);
    } catch (error) {
      if (isApiError(error) && error.isValidation) {
        setErrors(Object.fromEntries(Object.entries(error.fieldErrors).map(([key, messages]) => [key, messages[0] ?? ''])) as Errors);
      } else {
        notify.error(error, 'Could not record the complaint');
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
        <DialogTitle>Record a complaint</DialogTitle>
        <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
          <Field label="How was it made?">
            <RadioGroup layout="horizontal" value={source} onChange={(_, d) => setSource(d.value as ComplaintSource)}>
              {MANUAL_COMPLAINT_SOURCES.map((s) => (
                <Radio key={s} value={s} label={complaintSourceLabel[s]} />
              ))}
            </RadioGroup>
          </Field>
          <Field
            label="What happened?"
            required
            hint="In the complainant's words: what, where and when. Avoid clinical details that are not needed."
            validationMessage={errors.description}
            validationState={errors.description ? 'error' : 'none'}
          >
            <Textarea value={description} onChange={(_, d) => setDescription(d.value)} rows={4} maxLength={4000} resize="vertical" autoFocus />
          </Field>
          <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(200px, 1fr))', gap: 12 }}>
            <Field label="About (area)" validationMessage={errors.category} validationState={errors.category ? 'error' : 'none'}>
              <Input value={category} onChange={(_, d) => setCategory(d.value)} maxLength={64} list={listId} placeholder="For example: Billing" />
              <datalist id={listId}>
                {CATEGORY_SUGGESTIONS.map((c) => (
                  <option key={c} value={c} />
                ))}
              </datalist>
            </Field>
            <Field label="Department" validationMessage={errors.departmentId} validationState={errors.departmentId ? 'error' : 'none'}>
              <Select value={departmentId} onChange={(_, d) => setDepartmentId(d.value)}>
                <option value="">Not specific</option>
                {toOptions(departments.data).map((o) => (
                  <option key={o.value} value={o.value}>
                    {o.label}
                  </option>
                ))}
              </Select>
            </Field>
          </div>
          <PatientPicker
            value={patient}
            onChange={setPatient}
            label="Patient (if registered)"
            hint="Linking the patient shows the complaint in their Patient 360 view."
            validationMessage={errors.patientId}
          />
          <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(200px, 1fr))', gap: 12 }}>
            <Field label="Contact name" validationMessage={errors.contactName} validationState={errors.contactName ? 'error' : 'none'}>
              <Input value={contactName} onChange={(_, d) => setContactName(d.value)} maxLength={200} autoComplete="off" />
            </Field>
            <Field label="Contact phone" validationMessage={errors.contactPhone} validationState={errors.contactPhone ? 'error' : 'none'}>
              <Input type="tel" value={contactPhone} onChange={(_, d) => setContactPhone(d.value)} maxLength={20} autoComplete="off" />
            </Field>
          </div>
          <Field label="Priority" hint={`High: resolve within ${SLA_HOURS.High} hours. Normal: within ${SLA_HOURS.Normal} hours.`}>
            <RadioGroup layout="horizontal" value={priority} onChange={(_, d) => setPriority(d.value as ComplaintPriority)}>
              <Radio value="Normal" label="Normal" />
              <Radio value="High" label="High (safety, dignity or a repeat complaint)" />
            </RadioGroup>
          </Field>
          <UserPicker label="Owner" value={owner} onChange={setOwner} hint="Who will resolve it." validationMessage={errors.ownerUserId} />
          <Caption1>Everyone who handles complaints is alerted with the complaint number and area only, never the description.</Caption1>
        </DialogContent>
        <DialogActions>
          <Button onClick={onClose} disabled={busy}>
            Cancel
          </Button>
          <Button type="submit" appearance="primary" disabled={busy} icon={busy ? <Spinner size="tiny" /> : undefined}>
            Record complaint
          </Button>
        </DialogActions>
      </DialogBody>
    </form>
  );
}
