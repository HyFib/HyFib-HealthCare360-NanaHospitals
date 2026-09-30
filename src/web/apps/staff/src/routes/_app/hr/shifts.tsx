import { Button, Field, Input, Text } from '@fluentui/react-components';
import { AddRegular, ClockRegular, WeatherMoonRegular } from '@fluentui/react-icons';
import { zodResolver } from '@hookform/resolvers/zod';
import { isApiError } from '@hc360/core';
import {
  applyServerErrors,
  DataTable,
  FormGrid,
  NumberField,
  Page,
  PageHeader,
  Section,
  StatusBadge,
  SwitchField,
  TextField,
  useNotify,
} from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { Controller, useForm, useWatch, type Control } from 'react-hook-form';
import { z } from 'zod';
import { usePermission } from '@/lib/permissions';
import { hrApi, hrKeys, hrQueries, type ShiftDto } from '@/modules/hr/api';
import { HR_PERMISSIONS as P, SHIFT_VIEWERS } from '@/modules/hr/labels';
import { ShiftChip } from '@/modules/hr/ShiftChip';
import { EditorDrawer } from '@/modules/hr/shared/EditorDrawer';
import { RequirePermission } from '@/modules/hr/shared/NoAccess';
import { formatMinutes, formatTimeOfDay, fromTimeInput, toTimeInput } from '@/modules/hr/shared/dates';

export const Route = createFileRoute('/_app/hr/shifts')({
  component: () => (
    <RequirePermission permission={SHIFT_VIEWERS} what="shifts">
      <ShiftsPage />
    </RequirePermission>
  ),
});

function ShiftsPage() {
  const navigate = useNavigate();
  const canManage = usePermission(P.rosterManage);
  const shifts = useQuery(hrQueries.shifts(true));
  const [editing, setEditing] = useState<ShiftDto | 'new' | null>(null);

  return (
    <Page maxWidth={1200}>
      <PageHeader
        title="Shifts"
        subtitle="Timings used by the roster and by attendance to judge late arrival, early exit and overtime"
        icon={<ClockRegular />}
        breadcrumbs={[
          { label: 'Duty roster', onClick: () => void navigate({ to: '/hr/roster' }) },
          { label: 'Shifts' },
        ]}
        actions={
          canManage && (
            <Button appearance="primary" icon={<AddRegular />} onClick={() => setEditing('new')}>
              Add shift
            </Button>
          )
        }
      />
      <Section flush>
        <DataTable
          ariaLabel="Shifts"
          rows={shifts.data}
          loading={shifts.isLoading}
          error={shifts.error}
          onRetry={() => void shifts.refetch()}
          getRowId={(s) => s.id}
          onRowClick={canManage ? (s) => setEditing(s) : undefined}
          columns={[
            { key: 'code', header: 'Shift', width: 110, render: (s) => <ShiftChip shift={s} /> },
            { key: 'name', header: 'Name', render: (s) => <Text weight="semibold">{s.name}</Text> },
            {
              key: 'time',
              header: 'Timing',
              render: (s) => `${formatTimeOfDay(s.startTime)} to ${formatTimeOfDay(s.endTime)}`,
            },
            {
              key: 'hours',
              header: 'Duration',
              priority: 'low',
              render: (s) => `${formatMinutes(s.durationMinutes)} (break ${formatMinutes(s.breakMinutes)})`,
            },
            { key: 'grace', header: 'Grace', priority: 'low', render: (s) => formatMinutes(s.graceMinutes) },
            {
              key: 'days',
              header: 'Half / full day',
              priority: 'low',
              render: (s) => `${formatMinutes(s.halfDayMinutes)} / ${formatMinutes(s.fullDayMinutes)}`,
            },
            {
              key: 'night',
              header: 'Night',
              width: 70,
              render: (s) => (s.isNightShift ? <WeatherMoonRegular aria-label="Night shift" /> : '—'),
            },
            {
              key: 'active',
              header: 'Status',
              width: 100,
              render: (s) => (
                <StatusBadge
                  status={s.isActive ? 'Active' : 'Inactive'}
                  tone={s.isActive ? 'success' : 'informative'}
                />
              ),
            },
          ]}
          emptyTitle="No shifts yet"
          emptyDescription="Add the hospital's shifts, for example General 09:00 to 17:30 and Night 21:00 to 07:00."
          size="small"
        />
      </Section>
      {editing && <ShiftDrawer shift={editing === 'new' ? null : editing} onClose={() => setEditing(null)} />}
    </Page>
  );
}

const schema = z.object({
  code: z
    .string()
    .trim()
    .min(1, 'Enter a short code, e.g. MOR.')
    .max(16)
    .regex(/^[A-Za-z0-9_-]+$/, "Use letters, digits, '-' or '_'."),
  name: z.string().trim().min(1, 'Enter the shift name.').max(100),
  startTime: z.string().regex(/^\d{2}:\d{2}$/, 'Enter the start time.'),
  endTime: z.string().regex(/^\d{2}:\d{2}$/, 'Enter the end time.'),
  breakMinutes: z.number({ error: 'Enter the break in minutes (0 if none).' }).int().min(0).max(240),
  graceMinutes: z.number({ error: 'Enter the grace minutes.' }).int().min(0).max(120),
  isNightShift: z.boolean(),
  halfDayMinutes: z.number().int().min(30).max(1440).nullable(),
  fullDayMinutes: z.number().int().min(30).max(1440).nullable(),
  overtimeThresholdMinutes: z.number({ error: 'Enter the overtime threshold.' }).int().min(0).max(240),
  color: z.string().regex(/^#[0-9A-Fa-f]{6}$/, 'Choose a colour.'),
  isActive: z.boolean(),
});

type Values = z.infer<typeof schema>;

function ShiftDrawer({ shift, onClose }: { shift: ShiftDto | null; onClose: () => void }) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const form = useForm<Values>({
    resolver: zodResolver(schema),
    defaultValues: {
      code: shift?.code ?? '',
      name: shift?.name ?? '',
      startTime: toTimeInput(shift?.startTime) || '09:00',
      endTime: toTimeInput(shift?.endTime) || '17:30',
      breakMinutes: shift?.breakMinutes ?? 30,
      graceMinutes: shift?.graceMinutes ?? 10,
      isNightShift: shift?.isNightShift ?? false,
      halfDayMinutes: shift?.halfDayMinutes ?? null,
      fullDayMinutes: shift?.fullDayMinutes ?? null,
      overtimeThresholdMinutes: shift?.overtimeThresholdMinutes ?? 30,
      color: shift?.color ?? '#0A7CA7',
      isActive: shift?.isActive ?? true,
    },
  });

  const save = useMutation({
    mutationFn: (v: Values) =>
      hrApi.saveShift(shift?.id ?? null, {
        ...v,
        code: v.code.toUpperCase(),
        startTime: fromTimeInput(v.startTime) ?? '00:00:00',
        endTime: fromTimeInput(v.endTime) ?? '00:00:00',
        version: shift?.version ?? null,
      }),
    onSuccess: () => {
      notify.success('Shift saved');
      void queryClient.invalidateQueries({ queryKey: hrKeys.masters });
      onClose();
    },
    onError: (error) => {
      if (isApiError(error) && error.code === 'shift.code_in_use') {
        form.setError('code', { type: 'server', message: error.userMessage });
      } else if (!applyServerErrors(error, form.setError)) {
        notify.error(error, 'Could not save the shift');
      }
    },
  });

  return (
    <EditorDrawer
      open
      title={shift ? `${shift.code} · ${shift.name}` : 'Add shift'}
      onClose={onClose}
      onSubmit={form.handleSubmit((v) => save.mutate(v))}
      busy={save.isPending}
    >
      <FormGrid columns={2} minColumnWidth={150}>
        <TextField control={form.control} name="code" label="Code" required autoFocus maxLength={16} />
        <ColourField control={form.control} />
      </FormGrid>
      <TextField control={form.control} name="name" label="Name" required maxLength={100} />
      <FormGrid columns={2} minColumnWidth={150}>
        <TimeField control={form.control} name="startTime" label="Starts" />
        <TimeField control={form.control} name="endTime" label="Ends" />
        <NumberField control={form.control} name="breakMinutes" label="Break (minutes)" required min={0} max={240} />
        <NumberField
          control={form.control}
          name="graceMinutes"
          label="Grace for late (minutes)"
          required
          min={0}
          max={120}
        />
        <NumberField
          control={form.control}
          name="halfDayMinutes"
          label="Half day after (minutes)"
          min={30}
          max={1440}
          hint="Blank: half the shift"
        />
        <NumberField
          control={form.control}
          name="fullDayMinutes"
          label="Full day after (minutes)"
          min={30}
          max={1440}
          hint="Blank: shift minus 30 min"
        />
        <NumberField
          control={form.control}
          name="overtimeThresholdMinutes"
          label="Overtime after (minutes extra)"
          required
          min={0}
          max={240}
        />
      </FormGrid>
      <SwitchField control={form.control} name="isNightShift" label="Night shift (ends the next day)" />
      <SwitchField control={form.control} name="isActive" label="In use" />
    </EditorDrawer>
  );
}

function TimeField({
  control,
  name,
  label,
}: {
  control: Control<Values>;
  name: 'startTime' | 'endTime';
  label: string;
}) {
  return (
    <Controller
      control={control}
      name={name}
      render={({ field, fieldState }) => (
        <Field
          label={label}
          required
          validationMessage={fieldState.error?.message}
          validationState={fieldState.error ? 'error' : 'none'}
        >
          <Input
            type="time"
            value={field.value}
            onChange={(_, d) => field.onChange(d.value)}
            onBlur={field.onBlur}
            ref={field.ref}
          />
        </Field>
      )}
    />
  );
}

function ColourField({ control }: { control: Control<Values> }) {
  const color = useWatch({ control, name: 'color' });
  return (
    <Controller
      control={control}
      name="color"
      render={({ field, fieldState }) => (
        <Field
          label="Colour on the roster"
          validationMessage={fieldState.error?.message}
          validationState={fieldState.error ? 'error' : 'none'}
        >
          <span style={{ display: 'inline-flex', gap: 8, alignItems: 'center' }}>
            <input
              type="color"
              value={field.value}
              onChange={(e) => field.onChange(e.target.value)}
              aria-label="Colour"
              style={{ width: 44, height: 32, border: 'none', background: 'none' }}
            />
            <Text font="monospace">{color}</Text>
          </span>
        </Field>
      )}
    />
  );
}
