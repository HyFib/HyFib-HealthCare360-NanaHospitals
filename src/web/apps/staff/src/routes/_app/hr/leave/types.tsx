import { Badge, Button, Caption1, Text } from '@fluentui/react-components';
import { AddRegular, CalendarPersonRegular } from '@fluentui/react-icons';
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
  SelectField,
  StatusBadge,
  SwitchField,
  TextField,
  useNotify,
} from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute } from '@tanstack/react-router';
import { useState } from 'react';
import { useForm } from 'react-hook-form';
import { z } from 'zod';
import { usePermission } from '@/lib/permissions';
import { hrApi, hrKeys, hrQueries, type LeaveTypeDto } from '@/modules/hr/api';
import { formatDays } from '@/modules/hr/attendance';
import { HR_PERMISSIONS as P } from '@/modules/hr/labels';
import { LEAVE_NAV } from '@/modules/hr/nav';
import { EditorDrawer } from '@/modules/hr/shared/EditorDrawer';
import { RequirePermission } from '@/modules/hr/shared/NoAccess';
import { SubNav } from '@/modules/hr/shared/SubNav';

export const Route = createFileRoute('/_app/hr/leave/types')({
  component: () => (
    <RequirePermission permission={[P.employeesRead, P.employeesManage, P.leaveApprove]} what="leave types">
      <LeaveTypesPage />
    </RequirePermission>
  ),
});

function LeaveTypesPage() {
  const canManage = usePermission(P.employeesManage);
  const types = useQuery(hrQueries.leaveTypes(true));
  const [editing, setEditing] = useState<LeaveTypeDto | 'new' | null>(null);

  return (
    <Page>
      <PageHeader
        title="Leave types"
        subtitle="Quota, carry-forward and rules for each kind of leave. Changes apply to new requests and the next yearly credit."
        icon={<CalendarPersonRegular />}
        actions={
          canManage && (
            <Button appearance="primary" icon={<AddRegular />} onClick={() => setEditing('new')}>
              Add leave type
            </Button>
          )
        }
      />
      <SubNav items={LEAVE_NAV} ariaLabel="Leave screens" />
      <Section flush>
        <DataTable
          ariaLabel="Leave types"
          rows={types.data}
          loading={types.isLoading}
          error={types.error}
          onRetry={() => void types.refetch()}
          getRowId={(t) => t.id}
          onRowClick={canManage ? (t) => setEditing(t) : undefined}
          columns={[
            {
              key: 'name',
              header: 'Leave',
              render: (t) => (
                <span>
                  <Text weight="semibold">{t.name}</Text> <Caption1>{t.code}</Caption1>
                </span>
              ),
            },
            {
              key: 'quota',
              header: 'Per year',
              align: 'end',
              width: 90,
              render: (t) => (t.tracksBalance ? formatDays(t.annualQuota) : '—'),
            },
            {
              key: 'carry',
              header: 'Carry forward up to',
              align: 'end',
              priority: 'low',
              render: (t) => (t.tracksBalance ? formatDays(t.carryForwardCap) : '—'),
            },
            {
              key: 'rules',
              header: 'Rules',
              priority: 'low',
              render: (t) => (
                <span style={{ display: 'inline-flex', gap: 4, flexWrap: 'wrap' }}>
                  <Badge size="small" appearance="tint" color={t.isPaid ? 'success' : 'warning'}>
                    {t.isPaid ? 'Paid' : 'Unpaid'}
                  </Badge>
                  {t.allowHalfDay && (
                    <Badge size="small" appearance="tint">
                      Half days
                    </Badge>
                  )}
                  {t.isEncashable && (
                    <Badge size="small" appearance="tint">
                      Encashable
                    </Badge>
                  )}
                  {t.countsNonWorkingDays && (
                    <Badge size="small" appearance="tint">
                      Calendar days
                    </Badge>
                  )}
                  {t.applicableGender && (
                    <Badge size="small" appearance="tint">
                      {t.applicableGender} only
                    </Badge>
                  )}
                </span>
              ),
            },
            {
              key: 'max',
              header: 'Max per request',
              align: 'end',
              priority: 'low',
              render: (t) => (t.maxDaysPerRequest ? formatDays(t.maxDaysPerRequest) : '—'),
            },
            {
              key: 'doc',
              header: 'Document above',
              align: 'end',
              priority: 'low',
              render: (t) =>
                t.documentRequiredAfterDays !== null ? `${formatDays(t.documentRequiredAfterDays)} days` : '—',
            },
            {
              key: 'active',
              header: 'Status',
              width: 100,
              render: (t) => (
                <StatusBadge
                  status={t.isActive ? 'Active' : 'Inactive'}
                  tone={t.isActive ? 'success' : 'informative'}
                />
              ),
            },
          ]}
          emptyTitle="No leave types"
          size="small"
        />
      </Section>
      {editing && <LeaveTypeDrawer type={editing === 'new' ? null : editing} onClose={() => setEditing(null)} />}
    </Page>
  );
}

const schema = z.object({
  code: z
    .string()
    .trim()
    .min(1, 'Enter a short code, e.g. CL.')
    .max(16)
    .regex(/^[A-Za-z0-9_]+$/, "Use letters, digits or '_'."),
  name: z.string().trim().min(1, 'Enter the name.').max(100),
  annualQuota: z
    .number({ error: 'Enter the days per year (0 if not tracked).' })
    .min(0)
    .max(366)
    .refine((v) => v * 2 === Math.floor(v * 2), 'Use whole or half days.'),
  carryForwardCap: z.number({ error: 'Enter the carry-forward limit (0 if none).' }).min(0).max(366),
  isEncashable: z.boolean(),
  isPaid: z.boolean(),
  allowHalfDay: z.boolean(),
  tracksBalance: z.boolean(),
  countsNonWorkingDays: z.boolean(),
  maxDaysPerRequest: z.number().min(0.5).max(366).nullable(),
  documentRequiredAfterDays: z.number().min(0).max(366).nullable(),
  applicableGender: z.enum(['Male', 'Female', 'Other']).nullable(),
  isActive: z.boolean(),
});

type Values = z.infer<typeof schema>;

function genderOrNull(gender: string | null | undefined): Values['applicableGender'] {
  return gender === 'Male' || gender === 'Female' || gender === 'Other' ? gender : null;
}

function LeaveTypeDrawer({ type, onClose }: { type: LeaveTypeDto | null; onClose: () => void }) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const form = useForm<Values>({
    resolver: zodResolver(schema),
    defaultValues: {
      code: type?.code ?? '',
      name: type?.name ?? '',
      annualQuota: type?.annualQuota ?? 12,
      carryForwardCap: type?.carryForwardCap ?? 0,
      isEncashable: type?.isEncashable ?? false,
      isPaid: type?.isPaid ?? true,
      allowHalfDay: type?.allowHalfDay ?? true,
      tracksBalance: type?.tracksBalance ?? true,
      countsNonWorkingDays: type?.countsNonWorkingDays ?? false,
      maxDaysPerRequest: type?.maxDaysPerRequest ?? null,
      documentRequiredAfterDays: type?.documentRequiredAfterDays ?? null,
      applicableGender: genderOrNull(type?.applicableGender),
      isActive: type?.isActive ?? true,
    },
  });

  const save = useMutation({
    mutationFn: (v: Values) =>
      hrApi.saveLeaveType(type?.id ?? null, { ...v, code: v.code.toUpperCase(), version: type?.version ?? null }),
    onSuccess: () => {
      notify.success('Leave type saved');
      void queryClient.invalidateQueries({ queryKey: hrKeys.masters });
      onClose();
    },
    onError: (error) => {
      if (isApiError(error) && error.code === 'leave_type.code_in_use') {
        form.setError('code', { type: 'server', message: error.userMessage });
      } else if (!applyServerErrors(error, form.setError)) {
        notify.error(error, 'Could not save the leave type');
      }
    },
  });

  return (
    <EditorDrawer
      open
      title={type ? type.name : 'Add leave type'}
      onClose={onClose}
      onSubmit={form.handleSubmit((v) => save.mutate(v))}
      busy={save.isPending}
      size="medium"
    >
      <FormGrid columns={2} minColumnWidth={160}>
        <TextField control={form.control} name="code" label="Code" required autoFocus maxLength={16} />
        <TextField control={form.control} name="name" label="Name" required maxLength={100} />
      </FormGrid>
      <SwitchField control={form.control} name="tracksBalance" label="Keeps a yearly balance" />
      <FormGrid columns={2} minColumnWidth={160}>
        <NumberField
          control={form.control}
          name="annualQuota"
          label="Days per year"
          required
          min={0}
          max={366}
          step={0.5}
        />
        <NumberField
          control={form.control}
          name="carryForwardCap"
          label="Carry forward up to (days)"
          required
          min={0}
          max={366}
          step={0.5}
        />
        <NumberField
          control={form.control}
          name="maxDaysPerRequest"
          label="Most days in one request"
          min={0.5}
          max={366}
          step={0.5}
          hint="Blank: no limit"
        />
        <NumberField
          control={form.control}
          name="documentRequiredAfterDays"
          label="Document needed above (days)"
          min={0}
          max={366}
          step={0.5}
          hint="Blank: never"
        />
      </FormGrid>
      <SelectField
        control={form.control}
        name="applicableGender"
        label="Only for"
        placeholder="Everyone"
        options={[
          { value: 'Female', label: 'Women (e.g. maternity)' },
          { value: 'Male', label: 'Men (e.g. paternity)' },
          { value: 'Other', label: 'Other' },
        ]}
      />
      <SwitchField control={form.control} name="isPaid" label="Paid leave (unpaid leave is loss of pay)" />
      <SwitchField control={form.control} name="allowHalfDay" label="Can be taken in half days" />
      <SwitchField
        control={form.control}
        name="countsNonWorkingDays"
        label="Counts holidays and weekly offs (calendar days, e.g. maternity)"
      />
      <SwitchField control={form.control} name="isEncashable" label="Unused days can be encashed" />
      <SwitchField control={form.control} name="isActive" label="In use" />
    </EditorDrawer>
  );
}
