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
  Spinner,
  Textarea,
} from '@fluentui/react-components';
import { isApiError } from '@hc360/core';
import { useNotify } from '@hc360/ui';
import { useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { fromIstInput, nowIstInput } from '@/modules/hr/shared/dates';
import { crmApi, crmKeys } from './api';
import { LEAD_ACTIVITY_TYPES, leadActivityLabel, type LeadActivityType } from './labels';

/** Log a call, message, visit or note on a lead. Any contact except a note moves a new lead to "Contacted". */
export function LeadActivityDialog({ open, onClose, leadId, leadName }: { open: boolean; onClose: () => void; leadId: string; leadName: string }) {
  return (
    <Dialog open={open} onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface style={{ maxWidth: 620 }}>{open && <Body onClose={onClose} leadId={leadId} leadName={leadName} />}</DialogSurface>
    </Dialog>
  );
}

function Body({ onClose, leadId, leadName }: { onClose: () => void; leadId: string; leadName: string }) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [type, setType] = useState<LeadActivityType>('Call');
  const [occurredAt, setOccurredAt] = useState(nowIstInput());
  const [outcome, setOutcome] = useState('');
  const [notes, setNotes] = useState('');
  const [next, setNext] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const save = async () => {
    if (!outcome.trim() && !notes.trim()) {
      setError('Record the outcome or a note.');
      return;
    }
    setBusy(true);
    try {
      await crmApi.addActivity(leadId, {
        type,
        occurredAt: fromIstInput(occurredAt),
        outcome: outcome.trim() || null,
        notes: notes.trim() || null,
        nextFollowUpAt: fromIstInput(next),
      });
      notify.success('Activity recorded');
      void queryClient.invalidateQueries({ queryKey: crmKeys.leads });
      onClose();
    } catch (err) {
      if (isApiError(err) && err.isValidation) {
        setError(err.userMessage);
      } else {
        notify.error(err, 'Could not record the activity');
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
        <DialogTitle>
          Log activity
          <Caption1 block>{leadName}</Caption1>
        </DialogTitle>
        <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
          <Field label="What happened">
            <RadioGroup layout="horizontal" value={type} onChange={(_, d) => setType(d.value as LeadActivityType)}>
              {LEAD_ACTIVITY_TYPES.map((t) => (
                <Radio key={t} value={t} label={leadActivityLabel[t]} />
              ))}
            </RadioGroup>
          </Field>
          <Field label="When" hint="Hospital time">
            <Input type="datetime-local" value={occurredAt} max={nowIstInput()} onChange={(_, d) => setOccurredAt(d.value)} />
          </Field>
          <Field label="Outcome" validationMessage={error ?? undefined} validationState={error ? 'error' : 'none'}>
            <Input value={outcome} onChange={(_, d) => setOutcome(d.value)} maxLength={200} autoFocus placeholder="For example: interested, will come on Saturday" />
          </Field>
          <Field label="Notes">
            <Textarea value={notes} onChange={(_, d) => setNotes(d.value)} rows={3} maxLength={2000} />
          </Field>
          <Field label="Call back on (optional)" hint="Hospital time">
            <Input type="datetime-local" value={next} onChange={(_, d) => setNext(d.value)} />
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
