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
  MessageBarTitle,
  Spinner,
  Text,
} from '@fluentui/react-components';
import { zodResolver } from '@hookform/resolvers/zod';
import { formatDate, formatInr, isApiError } from '@hc360/core';
import {
  applyServerErrors,
  CloseRequestScope,
  DataTable,
  FormGrid,
  NumberField,
  SelectField,
  TextAreaField,
  useCloseRequests,
  useNotify,
  useUnsavedChanges,
} from '@hc360/ui';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useForm, useWatch } from 'react-hook-form';
import { z } from 'zod';
import { hrApi, hrKeys, hrQueries, type LeaveEncashmentDto } from './api';
import { formatDays } from './attendance';
import type { PickedEmployee } from './EmployeePicker';
import { payRefusal } from './makerChecker';
import { currentMonth, monthLabel } from './shared/dates';

/**
 * Leave paid out in money (HR-05). While in service: asked for here by HR who process payroll and approved by someone
 * else who approves payroll, never the employee; approved, the days leave the balance and the amount is paid as a taxable
 * earning in the month's payroll. On leaving: paid in the full and final settlement.
 */
export function EncashmentsTable({
  rows,
  loading,
  error,
  onRetry,
}: {
  rows: LeaveEncashmentDto[] | undefined;
  loading: boolean;
  error: unknown;
  onRetry: () => void;
}) {
  return (
    <DataTable
      ariaLabel="Leave encashed"
      rows={rows}
      loading={loading}
      error={error}
      onRetry={onRetry}
      getRowId={(r) => r.id}
      columns={[
        { key: 'type', header: 'Leave', width: 90, render: (r) => `${r.leaveTypeCode} ${r.year}` },
        { key: 'days', header: 'Days', align: 'end', width: 70, render: (r) => formatDays(r.days) },
        { key: 'amount', header: 'Amount', align: 'end', render: (r) => formatInr(r.amount) },
        {
          key: 'paid',
          header: 'Paid',
          render: (r) =>
            r.kind === 'InService'
              ? `In service, payroll ${r.payrollMonth ?? ''}`
              : `On leaving${r.lastWorkingDay ? `, ${formatDate(r.lastWorkingDay)}` : ''}`,
        },
        { key: 'reason', header: 'Reason', priority: 'low', render: (r) => r.reason },
      ]}
      emptyTitle="No leave encashed"
      size="small"
    />
  );
}

/** This month and the one before: an encashment is paid in a payroll month that has come, never a future one. */
function payMonths(): { value: string; label: string }[] {
  const { year, month } = currentMonth();
  const previous = month === 1 ? { year: year - 1, month: 12 } : { year, month: month - 1 };
  return [
    { value: `${year}-${month}`, label: monthLabel(year, month) },
    { value: `${previous.year}-${previous.month}`, label: monthLabel(previous.year, previous.month) },
  ];
}

const encashSchema = z.object({
  leaveTypeId: z.string({ error: 'Choose the leave type.' }).min(1, 'Choose the leave type.'),
  days: z
    .number({ error: 'Enter the days to encash.' })
    .positive('Enter the days to encash.')
    .refine((d) => d * 2 === Math.floor(d * 2), 'Use whole or half days.'),
  payMonth: z.string().min(1, 'Choose the payroll month.'),
  reason: z.string().trim().min(1, 'Give the reason; it is kept in the audit trail.').max(300),
});

type EncashValues = z.infer<typeof encashSchema>;

export function EncashLeaveDialog({
  open,
  onClose,
  employee,
  year,
}: {
  open: boolean;
  onClose: () => void;
  employee: PickedEmployee;
  year: number;
}) {
  const closing = useCloseRequests({ onClose });
  return (
    <Dialog open={open} onOpenChange={closing.onOpenChange}>
      <DialogSurface style={{ maxWidth: 560 }}>
        <CloseRequestScope value={closing}>
          {open && <EncashBody onClose={onClose} employee={employee} year={year} />}
        </CloseRequestScope>
      </DialogSurface>
    </Dialog>
  );
}

function EncashBody({ onClose, employee, year }: { onClose: () => void; employee: PickedEmployee; year: number }) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const encashable = useQuery(hrQueries.encashableLeave(employee.id, year));
  const months = payMonths();
  const [busy, setBusy] = useState(false);
  const [refusal, setRefusal] = useState<string | null>(null);
  const form = useForm<EncashValues>({
    resolver: zodResolver(encashSchema),
    defaultValues: { leaveTypeId: '', days: 1, payMonth: months[0]!.value, reason: '' },
  });
  const [typeId, days] = useWatch({ control: form.control, name: ['leaveTypeId', 'days'] });
  const chosen = encashable.data?.find((e) => e.leaveTypeId === typeId);

  const onValid = async (values: EncashValues) => {
    setBusy(true);
    setRefusal(null);
    const [payYear, payMonth] = values.payMonth.split('-').map(Number);
    try {
      await hrApi.encashLeave({
        employeeId: employee.id,
        leaveTypeId: values.leaveTypeId,
        year,
        days: values.days,
        payYear: payYear!,
        payMonth: payMonth!,
        reason: values.reason.trim(),
      });
      notify.success(
        'Asked for approval',
        `${values.days} day(s) for ${employee.name}. Someone else who approves payroll approves the encashment.`,
      );
      void queryClient.invalidateQueries({ queryKey: hrKeys.leave });
      onClose();
    } catch (error) {
      const words = isApiError(error) ? payRefusal(error.code) : null;
      if (words) {
        setRefusal(isApiError(error) && error.message ? `${error.message} ${words}` : words);
      } else if (!applyServerErrors(error, form.setError)) {
        notify.error(error, 'Could not ask for the encashment');
      }
    } finally {
      setBusy(false);
    }
  };

  const guard = useUnsavedChanges({ dirty: form.formState.isDirty, busy, onClose });
  const types = encashable.data ?? [];

  return (
    <form onSubmit={form.handleSubmit(onValid)} noValidate>
      <DialogBody>
        <DialogTitle>
          Encash leave · {year}
          <Text block size={200}>
            {employee.name} · {employee.code}
          </Text>
        </DialogTitle>
        <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
          {encashable.isLoading ? (
            <Spinner size="tiny" label="Reading the leave policy" />
          ) : types.length === 0 ? (
            <MessageBar intent="info" layout="multiline">
              <MessageBarBody>
                <MessageBarTitle>No leave pays out in service</MessageBarTitle>
                No leave type has an in-service encashment cap in the leave policy. Unused encashable leave is paid in
                the full and final settlement.
              </MessageBarBody>
            </MessageBar>
          ) : (
            <>
              <FormGrid columns={2} minColumnWidth={200}>
                <SelectField
                  control={form.control}
                  name="leaveTypeId"
                  label="Leave type"
                  required
                  placeholder="Choose"
                  options={types.map((t) => ({
                    value: t.leaveTypeId,
                    label: `${t.leaveTypeName} (${t.leaveTypeCode})`,
                  }))}
                />
                <NumberField control={form.control} name="days" label="Days" required step={0.5} min={0.5} max={366} />
                <SelectField control={form.control} name="payMonth" label="Paid in payroll" required options={months} />
              </FormGrid>
              {chosen && (
                <Text size={200}>
                  The policy pays out at most {formatDays(chosen.capPerYear)} day(s) a year in service and keeps{' '}
                  {formatDays(chosen.minimumBalance)} in the balance: {formatDays(chosen.encashedThisYear)} encashed
                  this year, {formatDays(chosen.waiting)} waiting, so at most {formatDays(chosen.mostDays)} now.
                  {typeof days === 'number' && days > 0
                    ? ` About ${formatInr(days * chosen.dailyRate)} at ${formatInr(chosen.dailyRate)} a day (the month's wages ÷ 30).`
                    : ''}
                </Text>
              )}
              <TextAreaField
                control={form.control}
                name="reason"
                label="Reason"
                required
                rows={2}
                maxLength={300}
                placeholder="For example: encashment asked for by the employee"
              />
              <Text size={200}>
                Someone else who approves payroll approves it, never you and never {employee.name}. Paid in service, it
                is taxable salary: s.10(10AA) exempts leave encashment only on retirement or leaving.
              </Text>
            </>
          )}
          {refusal && (
            <MessageBar intent="error" layout="multiline" politeness="assertive">
              <MessageBarBody>
                <MessageBarTitle>Not asked for</MessageBarTitle>
                {refusal}
              </MessageBarBody>
            </MessageBar>
          )}
        </DialogContent>
        <DialogActions>
          <Button onClick={guard.requestClose} disabled={busy}>
            Cancel
          </Button>
          <Button
            type="submit"
            appearance="primary"
            disabled={busy || types.length === 0}
            icon={busy ? <Spinner size="tiny" aria-hidden /> : undefined}
          >
            Ask for approval
          </Button>
        </DialogActions>
      </DialogBody>
      {guard.dialogs}
    </form>
  );
}
