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
  Radio,
  RadioGroup,
  Select,
  Spinner,
} from '@fluentui/react-components';
import { isApiError } from '@hc360/core';
import { useNotify } from '@hc360/ui';
import { useQueryClient } from '@tanstack/react-query';
import { useId, useState } from 'react';
import { toOptions, useDepartments } from '@/lib/lookups';
import { UserPicker } from '@/modules/hr/shared/UserPicker';
import { useUserName, type PickedUser } from '@/modules/hr/shared/users';
import { feedbackApi, feedbackKeys, type ComplaintDetail } from './api';
import { ASPECTS, aspectLabel, categoryLabel, type ComplaintPriority } from './labels';
import { SLA_HOURS } from './rules';

const CATEGORY_SUGGESTIONS = [...ASPECTS.map((a) => aspectLabel[a]), 'Waiting time', 'Staff behaviour', 'Facilities', 'Pharmacy', 'Other'];

/** Change who handles a complaint, its priority, department and area. Raising the priority can bring the deadline forward. */
export function ComplaintEditDialog({ open, onClose, complaint }: { open: boolean; onClose: () => void; complaint: ComplaintDetail }) {
  return (
    <Dialog open={open} onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface style={{ maxWidth: 560 }}>{open && <Body onClose={onClose} complaint={complaint} />}</DialogSurface>
    </Dialog>
  );
}

function Body({ onClose, complaint }: { onClose: () => void; complaint: ComplaintDetail }) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const departments = useDepartments();
  const listId = useId();
  const currentOwnerName = useUserName(complaint.ownerUserId);
  // undefined = unchanged, so the current owner's name can finish loading.
  const [owner, setOwner] = useState<PickedUser | null | undefined>(undefined);
  const [priority, setPriority] = useState<ComplaintPriority>(complaint.priority);
  const [departmentId, setDepartmentId] = useState(complaint.departmentId ?? '');
  const [category, setCategory] = useState(complaint.category ? categoryLabel(complaint.category) : '');
  const [error, setError] = useState<string | undefined>();
  const [busy, setBusy] = useState(false);

  const shownOwner: PickedUser | null =
    owner !== undefined ? owner : complaint.ownerUserId ? { id: complaint.ownerUserId, name: currentOwnerName ?? '' } : null;

  const save = async () => {
    setBusy(true);
    setError(undefined);
    try {
      await feedbackApi.updateComplaint(complaint.id, {
        ownerUserId: shownOwner?.id ?? null,
        priority,
        departmentId: departmentId || null,
        category: category.trim() || null,
        version: complaint.version,
      });
      notify.success('Complaint updated', owner && owner.id !== complaint.ownerUserId ? `${owner.name} has been notified.` : undefined);
      void queryClient.invalidateQueries({ queryKey: feedbackKeys.complaints });
      onClose();
    } catch (err) {
      if (isApiError(err) && err.code === 'concurrency') {
        notify.warning('Someone else changed this complaint', 'The latest details have been loaded. Make your change again.');
        void queryClient.invalidateQueries({ queryKey: feedbackKeys.complaints });
        onClose();
      } else if (isApiError(err) && err.isValidation) {
        setError(err.userMessage);
      } else {
        notify.error(err, 'Could not update the complaint');
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
        <DialogTitle>Complaint {complaint.complaintNumber}</DialogTitle>
        <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
          <UserPicker label="Owner" value={shownOwner} onChange={setOwner} hint="A new owner is notified with the deadline." validationMessage={error} />
          <Field
            label="Priority"
            hint={`High: ${SLA_HOURS.High} hours from when it was raised; Normal: ${SLA_HOURS.Normal} hours. Lowering the priority never moves the deadline later.`}
          >
            <RadioGroup layout="horizontal" value={priority} onChange={(_, d) => setPriority(d.value as ComplaintPriority)}>
              <Radio value="Normal" label="Normal" />
              <Radio value="High" label="High" />
            </RadioGroup>
          </Field>
          <Field label="Department">
            <Select value={departmentId} onChange={(_, d) => setDepartmentId(d.value)}>
              <option value="">Not specific</option>
              {toOptions(departments.data).map((o) => (
                <option key={o.value} value={o.value}>
                  {o.label}
                </option>
              ))}
            </Select>
          </Field>
          <Field label="About (area)">
            <Input value={category} onChange={(_, d) => setCategory(d.value)} maxLength={64} list={listId} />
            <datalist id={listId}>
              {CATEGORY_SUGGESTIONS.map((c) => (
                <option key={c} value={c} />
              ))}
            </datalist>
          </Field>
        </DialogContent>
        <DialogActions>
          <Button onClick={onClose} disabled={busy}>
            Cancel
          </Button>
          <Button type="submit" appearance="primary" disabled={busy} icon={busy ? <Spinner size="tiny" /> : undefined}>
            Save
          </Button>
        </DialogActions>
      </DialogBody>
    </form>
  );
}
