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
  Spinner,
} from '@fluentui/react-components';
import { zodResolver } from '@hookform/resolvers/zod';
import { todayIso } from '@hc360/core';
import { applyServerErrors, ChoiceField, DateField, FormGrid, TextAreaField, useNotify } from '@hc360/ui';
import { useState } from 'react';
import { Controller, useForm, useWatch, type Control } from 'react-hook-form';
import { z } from 'zod';
import type { RequestRegularisationRequest } from './api';
import { EmployeePicker, type PickedEmployee } from './EmployeePicker';
import { addDays, fromTimeInput } from './shared/dates';

/** Requests older than this are refused by the server (RegularisationService.MaxAgeDays). */
const MAX_AGE_DAYS = 45;

const schema = z
  .object({
    date: z.string({ error: 'Choose the day.' }).min(1, 'Choose the day.'),
    kind: z.enum(['MissedPunch', 'OnDuty']),
    requestedIn: z.string(),
    requestedOut: z.string(),
    reason: z.string().trim().min(1, 'Explain what happened.').max(500),
  })
  .superRefine((v, ctx) => {
    if (v.kind === 'MissedPunch' && !v.requestedIn && !v.requestedOut) {
      ctx.addIssue({ code: 'custom', path: ['requestedIn'], message: 'Enter the missed in-time and/or out-time.' });
    }
  });

type Values = z.infer<typeof schema>;

/**
 * Attendance correction: a missed punch (with the real in/out times) or a day on official duty away from the terminal.
 * HR raises it for an employee (and it is decided like any request); staff raise their own from self-service.
 */
export function RegularisationDialog({
  open,
  onClose,
  employee,
  pickEmployee,
  defaultDate,
  submit,
}: {
  open: boolean;
  onClose: () => void;
  /** Fixed employee (HR, from a profile or the attendance sheet). */
  employee?: PickedEmployee | null;
  /** Let HR choose the employee in the dialog. */
  pickEmployee?: boolean;
  defaultDate?: string;
  submit: (employeeId: string | null, request: RequestRegularisationRequest) => Promise<unknown>;
}) {
  return (
    <Dialog open={open} onOpenChange={(_, data) => !data.open && onClose()}>
      <DialogSurface style={{ maxWidth: 620 }}>
        {open && <Body onClose={onClose} employee={employee} pickEmployee={pickEmployee} defaultDate={defaultDate} submit={submit} />}
      </DialogSurface>
    </Dialog>
  );
}

function Body({
  onClose,
  employee,
  pickEmployee,
  defaultDate,
  submit,
}: {
  onClose: () => void;
  employee?: PickedEmployee | null;
  pickEmployee?: boolean;
  defaultDate?: string;
  submit: (employeeId: string | null, request: RequestRegularisationRequest) => Promise<unknown>;
}) {
  const notify = useNotify();
  const today = todayIso();
  const [picked, setPicked] = useState<PickedEmployee | null>(employee ?? null);
  const [pickError, setPickError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const form = useForm<Values>({
    resolver: zodResolver(schema),
    defaultValues: { date: defaultDate ?? addDays(today, -1), kind: 'MissedPunch', requestedIn: '', requestedOut: '', reason: '' },
  });
  const kind = useWatch({ control: form.control, name: 'kind' });

  const onValid = async (values: Values) => {
    if (pickEmployee && !picked) {
      setPickError('Choose the employee.');
      return;
    }
    setBusy(true);
    try {
      await submit(picked?.id ?? null, {
        date: values.date,
        kind: values.kind,
        requestedIn: fromTimeInput(values.requestedIn),
        requestedOut: fromTimeInput(values.requestedOut),
        reason: values.reason.trim(),
      });
      notify.success('Request sent', 'The attendance for that day is corrected once it is approved.');
      onClose();
    } catch (error) {
      if (!applyServerErrors(error, form.setError)) {
        notify.error(error, 'Could not send the request');
      }
    } finally {
      setBusy(false);
    }
  };

  return (
    <form onSubmit={form.handleSubmit(onValid)} noValidate>
      <DialogBody>
        <DialogTitle>
          Correct attendance
          {employee && (
            <Caption1 block>
              {employee.name} · {employee.code}
            </Caption1>
          )}
        </DialogTitle>
        <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
          {pickEmployee && !employee && (
            <EmployeePicker
              value={picked}
              onChange={(value) => {
                setPicked(value);
                setPickError(null);
              }}
              required
              validationMessage={pickError ?? undefined}
              autoFocus
            />
          )}
          <FormGrid columns={2} minColumnWidth={200}>
            <DateField control={form.control} name="date" label="Day" required min={addDays(today, -MAX_AGE_DAYS)} max={today} hint={`Within the last ${MAX_AGE_DAYS} days`} />
            <ChoiceField
              control={form.control}
              name="kind"
              label="What happened"
              options={[
                { value: 'MissedPunch', label: 'Missed a punch' },
                { value: 'OnDuty', label: 'On official duty' },
              ]}
            />
          </FormGrid>
          <FormGrid columns={2} minColumnWidth={200}>
            <TimeInput control={form.control} name="requestedIn" label={kind === 'OnDuty' ? 'Duty started (optional)' : 'Actual in-time'} />
            <TimeInput control={form.control} name="requestedOut" label={kind === 'OnDuty' ? 'Duty ended (optional)' : 'Actual out-time'} />
          </FormGrid>
          <TextAreaField
            control={form.control}
            name="reason"
            label="Reason"
            required
            rows={3}
            maxLength={500}
            placeholder={kind === 'OnDuty' ? 'For example: health camp at Tambaram' : 'For example: terminal at the main gate was not working'}
          />
        </DialogContent>
        <DialogActions>
          <Button onClick={onClose} disabled={busy}>
            Cancel
          </Button>
          <Button type="submit" appearance="primary" disabled={busy} icon={busy ? <Spinner size="tiny" /> : undefined}>
            Send for approval
          </Button>
        </DialogActions>
      </DialogBody>
    </form>
  );
}

function TimeInput({ control, name, label }: { control: Control<Values>; name: 'requestedIn' | 'requestedOut'; label: string }) {
  return (
    <Controller
      control={control}
      name={name}
      render={({ field, fieldState }) => (
        <Field label={label} validationMessage={fieldState.error?.message} validationState={fieldState.error ? 'error' : 'none'}>
          <Input type="time" value={field.value} onChange={(_, data) => field.onChange(data.value)} onBlur={field.onBlur} ref={field.ref} />
        </Field>
      )}
    />
  );
}
