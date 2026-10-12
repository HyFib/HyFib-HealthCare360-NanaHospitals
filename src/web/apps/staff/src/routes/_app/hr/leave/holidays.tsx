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
import { AddRegular, CalendarStarRegular, DeleteRegular } from '@fluentui/react-icons';
import { zodResolver } from '@hookform/resolvers/zod';
import { formatDateOnly, isApiError } from '@hc360/core';
import {
  applyServerErrors,
  ConfirmDialog,
  DataTable,
  DateField,
  FilterBar,
  Page,
  PageHeader,
  Section,
  SelectField,
  StatusBadge,
  TextField,
  useNotify,
  useUnsavedChanges,
} from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { useForm } from 'react-hook-form';
import { z } from 'zod';
import { usePermission } from '@/lib/permissions';
import { hrApi, hrKeys, hrQueries, type HolidayDto } from '@/modules/hr/api';
import { HOLIDAY_KINDS, holidayKindLabel, HR_PERMISSIONS as P } from '@/modules/hr/labels';
import { LEAVE_NAV } from '@/modules/hr/nav';
import { currentMonth, dayHeader, yearOptions } from '@/modules/hr/shared/dates';
import { RequirePermission } from '@/modules/hr/shared/NoAccess';
import { SubNav } from '@/modules/hr/shared/SubNav';
import { FilterPad } from '@/modules/hr/shared/layout';

export const Route = createFileRoute('/_app/hr/leave/holidays')({
  validateSearch: (s: Record<string, unknown>): { year?: number } => ({
    year: typeof s.year === 'number' && s.year >= 2000 && s.year <= 2100 ? s.year : undefined,
  }),
  component: () => (
    <RequirePermission permission={[P.attendanceManage, P.leaveApprove]} what="the holiday list">
      <HolidaysPage />
    </RequirePermission>
  ),
});

function HolidaysPage() {
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const notify = useNotify();
  const queryClient = useQueryClient();
  const canManage = usePermission(P.attendanceManage);
  const year = search.year ?? currentMonth().year;
  const holidays = useQuery(hrQueries.holidays(year));
  const [adding, setAdding] = useState(false);
  const [deleting, setDeleting] = useState<HolidayDto | null>(null);

  const remove = useMutation({
    mutationFn: (id: string) => hrApi.deleteHoliday(id),
    onSuccess: () => {
      setDeleting(null);
      notify.success('Holiday removed', 'Recompute attendance for that day if it has passed.');
      void queryClient.invalidateQueries({ queryKey: hrKeys.masters });
    },
    onError: (error) => notify.error(error, 'Could not remove the holiday'),
  });

  return (
    <Page maxWidth={1100}>
      <PageHeader
        title="Holidays"
        subtitle="Closed days for this hospital. Restricted holidays are optional and count as working days."
        icon={<CalendarStarRegular />}
        actions={
          canManage && (
            <Button appearance="primary" icon={<AddRegular />} onClick={() => setAdding(true)}>
              Add holiday
            </Button>
          )
        }
      />
      <SubNav items={LEAVE_NAV} ariaLabel="Leave screens" />
      <Section flush>
        <FilterPad>
          <FilterBar>
            <Field label="Year">
              <Select value={String(year)} onChange={(_, d) => void navigate({ search: { year: Number(d.value) } })}>
                {yearOptions(3, 2).map((y) => (
                  <option key={y} value={y}>
                    {y}
                  </option>
                ))}
              </Select>
            </Field>
          </FilterBar>
        </FilterPad>
        <DataTable
          ariaLabel="Holidays"
          rows={holidays.data}
          loading={holidays.isLoading}
          error={holidays.error}
          onRetry={() => void holidays.refetch()}
          getRowId={(h) => h.id}
          columns={[
            { key: 'date', header: 'Date', width: 140, render: (h) => formatDateOnly(h.date) },
            { key: 'day', header: 'Day', width: 80, render: (h) => dayHeader(h.date).weekday },
            { key: 'name', header: 'Holiday', render: (h) => <Text weight="semibold">{h.name}</Text> },
            {
              key: 'kind',
              header: 'Kind',
              render: (h) => (
                <StatusBadge
                  status={h.kind}
                  tone={h.kind === 'Restricted' ? 'informative' : 'brand'}
                  label={holidayKindLabel[h.kind]}
                />
              ),
            },
          ]}
          rowActions={
            canManage
              ? (h) => (
                  <Tooltip content={`Remove ${h.name}`} relationship="label">
                    <Button size="small" appearance="subtle" icon={<DeleteRegular />} onClick={() => setDeleting(h)} />
                  </Tooltip>
                )
              : undefined
          }
          emptyTitle={`No holidays for ${year}`}
          emptyDescription={
            canManage ? 'Add national and festival holidays so attendance and leave count them correctly.' : undefined
          }
          size="small"
        />
      </Section>
      <HolidayDialog open={adding} onClose={() => setAdding(false)} year={year} />
      <ConfirmDialog
        open={deleting !== null}
        title="Remove this holiday?"
        confirmLabel="Remove"
        destructive
        busy={remove.isPending}
        onConfirm={() => deleting && remove.mutate(deleting.id)}
        onCancel={() => setDeleting(null)}
      >
        {deleting ? `${deleting.name} on ${formatDateOnly(deleting.date)} becomes a working day.` : null}
      </ConfirmDialog>
    </Page>
  );
}

const schema = z.object({
  date: z.string({ error: 'Choose the date.' }).min(1, 'Choose the date.'),
  name: z.string().trim().min(1, 'Enter the holiday name.').max(100),
  kind: z.enum(HOLIDAY_KINDS),
});

type Values = z.infer<typeof schema>;

function HolidayDialog({ open, onClose, year }: { open: boolean; onClose: () => void; year: number }) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const form = useForm<Values>({
    resolver: zodResolver(schema),
    defaultValues: { date: `${year}-01-01`, name: '', kind: 'Festival' },
  });
  const save = useMutation({
    mutationFn: (v: Values) => hrApi.addHoliday({ date: v.date, name: v.name.trim(), kind: v.kind }),
    onSuccess: (_, v) => {
      notify.success('Holiday added', `${v.name} · ${formatDateOnly(v.date)}`);
      void queryClient.invalidateQueries({ queryKey: hrKeys.masters });
      form.reset({ date: `${year}-01-01`, name: '', kind: 'Festival' });
      onClose();
    },
    onError: (error) => {
      if (isApiError(error) && error.code === 'holiday.exists') {
        form.setError('date', { type: 'server', message: error.userMessage });
      } else if (!applyServerErrors(error, form.setError)) {
        notify.error(error, 'Could not add the holiday');
      }
    },
  });

  const guard = useUnsavedChanges({ dirty: form.formState.isDirty, busy: save.isPending, open, onClose });

  return (
    <Dialog open={open} onOpenChange={guard.onOpenChange}>
      <DialogSurface style={{ maxWidth: 520 }}>
        <form onSubmit={form.handleSubmit((v) => save.mutate(v))} noValidate>
          <DialogBody>
            <DialogTitle>Add holiday</DialogTitle>
            <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
              <DateField control={form.control} name="date" label="Date" required />
              <TextField
                control={form.control}
                name="name"
                label="Holiday"
                required
                autoFocus
                placeholder="For example: Pongal"
              />
              <SelectField
                control={form.control}
                name="kind"
                label="Kind"
                required
                options={HOLIDAY_KINDS.map((k) => ({ value: k, label: holidayKindLabel[k] }))}
              />
            </DialogContent>
            <DialogActions>
              <Button onClick={guard.requestClose} disabled={save.isPending}>
                Cancel
              </Button>
              <Button
                type="submit"
                appearance="primary"
                disabled={save.isPending}
                icon={save.isPending ? <Spinner size="tiny" aria-hidden /> : undefined}
              >
                Add holiday
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
        {guard.dialogs}
      </DialogSurface>
    </Dialog>
  );
}
