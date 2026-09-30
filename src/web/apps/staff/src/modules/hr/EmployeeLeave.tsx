import {
  Button,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Field,
  Select,
  Spinner,
  Text,
  Tooltip,
} from '@fluentui/react-components';
import { AddRegular, ArrowDownloadRegular, CalendarCancelRegular, EditRegular } from '@fluentui/react-icons';
import { zodResolver } from '@hookform/resolvers/zod';
import { formatDateTime, todayIso } from '@hc360/core';
import {
  applyServerErrors,
  DataTable,
  FormGrid,
  NumberField,
  Section,
  SelectField,
  StatusBadge,
  TextAreaField,
  useNotify,
} from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useForm } from 'react-hook-form';
import { z } from 'zod';
import { usePermission } from '@/lib/permissions';
import { DecisionButtons } from './ApprovalActions';
import { hrApi, hrKeys, hrQueries, type LeaveBalanceDto, type LeaveRequestDto } from './api';
import { formatDays } from './attendance';
import type { PickedEmployee } from './EmployeePicker';
import { BALANCE_VIEWERS, HR_PERMISSIONS } from './labels';
import { canCancelLeave, leavePeriodText } from './leave';
import { LeaveOnBehalfDialog } from './LeaveOnBehalfDialog';
import { ReasonDialog } from './shared/ReasonDialog';
import { currentMonth, yearOptions } from './shared/dates';
import { downloadStoredFile } from './shared/files';
import { Stack, Toolbar } from './shared/layout';

/** Leave balances table (opening, credited, adjusted, availed, waiting, available). */
export function BalancesTable({
  balances,
  loading,
  error,
  onRetry,
}: {
  balances: LeaveBalanceDto[] | undefined;
  loading: boolean;
  error: unknown;
  onRetry: () => void;
}) {
  return (
    <DataTable
      ariaLabel="Leave balances"
      rows={balances}
      loading={loading}
      error={error}
      onRetry={onRetry}
      getRowId={(b) => b.leaveTypeId}
      columns={[
        { key: 'type', header: 'Leave', render: (b) => `${b.leaveTypeName} (${b.leaveTypeCode})` },
        { key: 'opening', header: 'Carried over', align: 'end', priority: 'low', render: (b) => formatDays(b.opening) },
        { key: 'credited', header: 'Credited', align: 'end', priority: 'low', render: (b) => formatDays(b.credited) },
        { key: 'adjusted', header: 'Adjusted', align: 'end', priority: 'low', render: (b) => formatDays(b.adjusted) },
        { key: 'availed', header: 'Taken', align: 'end', render: (b) => formatDays(b.availed) },
        { key: 'pending', header: 'Waiting', align: 'end', render: (b) => formatDays(b.pending) },
        {
          key: 'available',
          header: 'Available',
          align: 'end',
          render: (b) => <Text weight="semibold">{formatDays(b.available)}</Text>,
        },
      ]}
      emptyTitle="No balances yet"
      emptyDescription="Balances appear once HR credits the year's leave."
      size="small"
    />
  );
}

/** Leave of one employee: balances, requests and HR actions. */
export function EmployeeLeave({ employee, readOnly }: { employee: PickedEmployee; readOnly?: boolean }) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const canSeeBalances = usePermission(BALANCE_VIEWERS);
  const canApprove = usePermission(HR_PERMISSIONS.leaveApprove) && !readOnly;
  const [year, setYear] = useState(currentMonth().year);
  const [applying, setApplying] = useState(false);
  const [adjusting, setAdjusting] = useState(false);
  const [cancelling, setCancelling] = useState<LeaveRequestDto | null>(null);
  const balances = useQuery({ ...hrQueries.leaveBalances(employee.id, year), enabled: canSeeBalances });
  const requests = useQuery({
    ...hrQueries.leaveRequests({ employeeId: employee.id, from: `${year}-01-01`, to: `${year}-12-31`, pageSize: 100 }),
    enabled: canApprove,
  });

  const cancel = useMutation({
    mutationFn: ({ id, reason }: { id: string; reason: string }) => hrApi.cancelLeave(id, reason),
    onSuccess: () => {
      setCancelling(null);
      notify.success('Leave cancelled', `${employee.name} has been notified.`);
      void queryClient.invalidateQueries({ queryKey: hrKeys.leave });
    },
    onError: (error) => notify.error(error, 'Could not cancel the leave'),
  });

  const today = todayIso();

  return (
    <Stack gap={16}>
      <Toolbar spaced={false}>
        <Field label="Year">
          <Select value={String(year)} onChange={(_, data) => setYear(Number(data.value))}>
            {yearOptions().map((y) => (
              <option key={y} value={y}>
                {y}
              </option>
            ))}
          </Select>
        </Field>
        {canApprove && (
          <>
            <Button icon={<AddRegular />} onClick={() => setApplying(true)} style={{ alignSelf: 'flex-end' }}>
              Apply leave for {employee.name.split(' ')[0]}
            </Button>
            <Button icon={<EditRegular />} onClick={() => setAdjusting(true)} style={{ alignSelf: 'flex-end' }}>
              Adjust balance
            </Button>
          </>
        )}
      </Toolbar>

      {canSeeBalances && (
        <Section title={`Balances ${year}`} flush>
          <BalancesTable
            balances={balances.data}
            loading={balances.isLoading}
            error={balances.error}
            onRetry={() => void balances.refetch()}
          />
        </Section>
      )}

      {canApprove && (
        <Section title={`Requests ${year}`} flush>
          <DataTable
            ariaLabel="Leave requests"
            rows={requests.data?.items}
            loading={requests.isLoading}
            error={requests.error}
            onRetry={() => void requests.refetch()}
            getRowId={(r) => r.id}
            columns={[
              { key: 'type', header: 'Leave', width: 80, render: (r) => r.leaveTypeCode },
              { key: 'period', header: 'Dates', render: (r) => leavePeriodText(r) },
              { key: 'days', header: 'Days', align: 'end', width: 70, render: (r) => formatDays(r.days) },
              { key: 'reason', header: 'Reason', priority: 'low', render: (r) => r.reason },
              { key: 'status', header: 'Status', render: (r) => <StatusBadge status={r.status} /> },
              { key: 'applied', header: 'Applied', priority: 'low', render: (r) => formatDateTime(r.appliedAt) },
            ]}
            rowActions={(r) => (
              <span style={{ display: 'inline-flex', gap: 4 }}>
                {r.attachmentFileId && (
                  <Tooltip content="Download the supporting document" relationship="label">
                    <Button
                      size="small"
                      appearance="subtle"
                      icon={<ArrowDownloadRegular />}
                      onClick={() =>
                        void downloadStoredFile(r.attachmentFileId!).catch((error: unknown) =>
                          notify.error(error, 'Could not open the document'),
                        )
                      }
                    />
                  </Tooltip>
                )}
                {r.status === 'Pending' && (
                  <DecisionButtons
                    subject={`${employee.name}'s leave`}
                    decide={async (approve, note) => {
                      await hrApi.decideLeave(r.id, approve, note);
                      void queryClient.invalidateQueries({ queryKey: hrKeys.leave });
                    }}
                  />
                )}
                {canCancelLeave(r, today, true) && r.status === 'Approved' && (
                  <Tooltip content="Cancel this leave" relationship="label">
                    <Button
                      size="small"
                      appearance="subtle"
                      icon={<CalendarCancelRegular />}
                      onClick={() => setCancelling(r)}
                    />
                  </Tooltip>
                )}
              </span>
            )}
            emptyTitle="No leave requests this year"
            size="small"
          />
        </Section>
      )}

      <LeaveOnBehalfDialog open={applying} onClose={() => setApplying(false)} employee={employee} />
      <AdjustBalanceDialog open={adjusting} onClose={() => setAdjusting(false)} employee={employee} year={year} />
      <ReasonDialog
        open={cancelling !== null}
        title="Cancel this leave?"
        description={
          cancelling
            ? `${leavePeriodText(cancelling)}. The days go back to the balance and past days are recomputed.`
            : undefined
        }
        label="Reason (shown to the employee)"
        confirmLabel="Cancel leave"
        destructive
        busy={cancel.isPending}
        onConfirm={(reason) => cancelling && cancel.mutate({ id: cancelling.id, reason })}
        onCancel={() => setCancelling(null)}
      />
    </Stack>
  );
}

const adjustSchema = z.object({
  leaveTypeId: z.string({ error: 'Choose the leave type.' }).min(1, 'Choose the leave type.'),
  days: z
    .number({ error: 'Enter the days to add (+) or remove (−).' })
    .refine((d) => d !== 0, 'Enter a number other than zero.')
    .refine((d) => d * 2 === Math.floor(d * 2), 'Use whole or half days.')
    .refine((d) => Math.abs(d) <= 366, 'That is more than a year.'),
  reason: z.string().trim().min(1, 'Give the reason; it is kept in the audit trail.').max(300),
});

type AdjustValues = z.infer<typeof adjustSchema>;

/** Comp-off credits and corrections (±, half days allowed). The reason is written to the audit trail. */
export function AdjustBalanceDialog({
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
  return (
    <Dialog open={open} onOpenChange={(_, data) => !data.open && onClose()}>
      <DialogSurface style={{ maxWidth: 560 }}>
        {open && <AdjustBody onClose={onClose} employee={employee} year={year} />}
      </DialogSurface>
    </Dialog>
  );
}

function AdjustBody({ onClose, employee, year }: { onClose: () => void; employee: PickedEmployee; year: number }) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const types = useQuery(hrQueries.leaveTypes());
  const [busy, setBusy] = useState(false);
  const form = useForm<AdjustValues>({
    resolver: zodResolver(adjustSchema),
    defaultValues: { leaveTypeId: '', days: 1, reason: '' },
  });

  const onValid = async (values: AdjustValues) => {
    setBusy(true);
    try {
      await hrApi.adjustLeave({
        employeeId: employee.id,
        leaveTypeId: values.leaveTypeId,
        year,
        days: values.days,
        reason: values.reason.trim(),
      });
      notify.success('Balance adjusted', `${values.days > 0 ? '+' : ''}${values.days} day(s) for ${employee.name}`);
      void queryClient.invalidateQueries({ queryKey: hrKeys.leave });
      void queryClient.invalidateQueries({ queryKey: hrKeys.me });
      onClose();
    } catch (error) {
      if (!applyServerErrors(error, form.setError)) {
        notify.error(error, 'Could not adjust the balance');
      }
    } finally {
      setBusy(false);
    }
  };

  return (
    <form onSubmit={form.handleSubmit(onValid)} noValidate>
      <DialogBody>
        <DialogTitle>
          Adjust leave balance · {year}
          <Text block size={200}>
            {employee.name} · {employee.code}
          </Text>
        </DialogTitle>
        <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
          <FormGrid columns={2} minColumnWidth={200}>
            <SelectField
              control={form.control}
              name="leaveTypeId"
              label="Leave type"
              required
              placeholder="Choose"
              options={(types.data ?? [])
                .filter((t) => t.tracksBalance)
                .map((t) => ({ value: t.id, label: `${t.name} (${t.code})` }))}
            />
            <NumberField
              control={form.control}
              name="days"
              label="Days (+ add, − remove)"
              required
              step={0.5}
              min={-366}
              max={366}
            />
          </FormGrid>
          <TextAreaField
            control={form.control}
            name="reason"
            label="Reason"
            required
            rows={2}
            maxLength={300}
            placeholder="For example: comp-off for working on 15 Aug"
          />
        </DialogContent>
        <DialogActions>
          <Button onClick={onClose} disabled={busy}>
            Cancel
          </Button>
          <Button type="submit" appearance="primary" disabled={busy} icon={busy ? <Spinner size="tiny" /> : undefined}>
            Adjust
          </Button>
        </DialogActions>
      </DialogBody>
    </form>
  );
}
