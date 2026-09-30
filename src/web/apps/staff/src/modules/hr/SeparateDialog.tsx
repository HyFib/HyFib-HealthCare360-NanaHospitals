import {
  Button,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  MessageBar,
  MessageBarBody,
  Spinner,
  tokens,
} from '@fluentui/react-components';
import { zodResolver } from '@hookform/resolvers/zod';
import { humanize, todayIso } from '@hc360/core';
import { applyServerErrors, DateField, FormGrid, SelectField, TextAreaField, useNotify } from '@hc360/ui';
import { useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useForm } from 'react-hook-form';
import { z } from 'zod';
import { hrApi, hrKeys, type EmployeeDetail } from './api';
import { SEPARATION_REASONS } from './labels';

const schema = z.object({
  lastWorkingDay: z.string({ error: 'Enter the last working day.' }).min(1, 'Enter the last working day.'),
  reason: z.enum(SEPARATION_REASONS),
  note: z.string().trim().max(500),
});

type Values = z.infer<typeof schema>;

/**
 * Separation (resignation, retirement…). The record is kept for statutory retention; pending leave is cancelled, the
 * future roster is removed and the user administrators are asked to disable the sign-in.
 */
export function SeparateDialog({
  open,
  onClose,
  employee,
}: {
  open: boolean;
  onClose: () => void;
  employee: EmployeeDetail;
}) {
  return (
    <Dialog open={open} onOpenChange={(_, data) => !data.open && onClose()} modalType="alert">
      <DialogSurface style={{ maxWidth: 600 }}>{open && <Body onClose={onClose} employee={employee} />}</DialogSurface>
    </Dialog>
  );
}

function Body({ onClose, employee }: { onClose: () => void; employee: EmployeeDetail }) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [busy, setBusy] = useState(false);
  const form = useForm<Values>({
    resolver: zodResolver(schema),
    defaultValues: { lastWorkingDay: todayIso(), reason: 'Resignation', note: '' },
  });

  const onValid = async (values: Values) => {
    setBusy(true);
    try {
      await hrApi.separate(employee.id, {
        lastWorkingDay: values.lastWorkingDay,
        reason: values.reason,
        note: values.note.trim() || null,
        version: employee.version,
      });
      notify.success('Employee separated', `${employee.fullName}'s record is kept for statutory retention.`);
      void queryClient.invalidateQueries({ queryKey: hrKeys.all });
      onClose();
    } catch (error) {
      if (!applyServerErrors(error, form.setError)) {
        notify.error(error, 'Could not separate the employee');
      }
    } finally {
      setBusy(false);
    }
  };

  return (
    <form onSubmit={form.handleSubmit(onValid)} noValidate>
      <DialogBody>
        <DialogTitle>Separate {employee.fullName}?</DialogTitle>
        <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
          <MessageBar intent="warning" layout="multiline">
            <MessageBarBody>
              This cannot be undone from this screen. Pending leave is cancelled, shifts after the last working day are
              removed, and the administrators are asked to disable the sign-in. The final salary is paid in the month's
              payroll.
            </MessageBarBody>
          </MessageBar>
          <FormGrid columns={2} minColumnWidth={200}>
            <DateField
              control={form.control}
              name="lastWorkingDay"
              label="Last working day"
              required
              min={employee.joiningDate}
            />
            <SelectField
              control={form.control}
              name="reason"
              label="Reason"
              required
              options={SEPARATION_REASONS.map((r) => ({ value: r, label: humanize(r) }))}
            />
          </FormGrid>
          <TextAreaField control={form.control} name="note" label="Note" rows={2} maxLength={500} />
        </DialogContent>
        <DialogActions>
          <Button onClick={onClose} disabled={busy}>
            Cancel
          </Button>
          <Button
            type="submit"
            appearance="primary"
            disabled={busy}
            icon={busy ? <Spinner size="tiny" /> : undefined}
            style={busy ? undefined : { backgroundColor: tokens.colorPaletteRedBackground3 }}
          >
            Separate employee
          </Button>
        </DialogActions>
      </DialogBody>
    </form>
  );
}
