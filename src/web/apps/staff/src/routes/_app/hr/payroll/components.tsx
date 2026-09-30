import { Badge, Button, Caption1, Text, Tooltip } from '@fluentui/react-components';
import { AddRegular, LockClosedRegular, MoneyRegular } from '@fluentui/react-icons';
import { zodResolver } from '@hookform/resolvers/zod';
import { formatInr, isApiError } from '@hc360/core';
import {
  applyServerErrors,
  ChoiceField,
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
import { createFileRoute } from '@tanstack/react-router';
import { useState } from 'react';
import { useForm, useWatch } from 'react-hook-form';
import { z } from 'zod';
import { usePermission } from '@/lib/permissions';
import { hrApi, hrKeys, hrQueries, type SalaryComponentDto } from '@/modules/hr/api';
import { HR_PERMISSIONS as P, PAYROLL_VIEWERS } from '@/modules/hr/labels';
import { PAYROLL_NAV } from '@/modules/hr/nav';
import { EditorDrawer } from '@/modules/hr/shared/EditorDrawer';
import { RequirePermission } from '@/modules/hr/shared/NoAccess';
import { SubNav } from '@/modules/hr/shared/SubNav';

export const Route = createFileRoute('/_app/hr/payroll/components')({
  component: () => (
    <RequirePermission permission={PAYROLL_VIEWERS} what="pay heads">
      <ComponentsPage />
    </RequirePermission>
  ),
});

const STATUTORY = ['PF', 'ESI', 'PT', 'TDS'];

function ComponentsPage() {
  const canEdit = usePermission(P.payrollProcess);
  const components = useQuery(hrQueries.components());
  const [editing, setEditing] = useState<SalaryComponentDto | 'new' | null>(null);

  return (
    <Page maxWidth={1200}>
      <PageHeader
        title="Pay heads"
        subtitle="Earnings and deductions used in salary structures and monthly inputs. PF, ESI, professional tax and TDS are worked out by payroll."
        icon={<MoneyRegular />}
        actions={
          canEdit && (
            <Button appearance="primary" icon={<AddRegular />} onClick={() => setEditing('new')}>
              Add pay head
            </Button>
          )
        }
      />
      <SubNav items={PAYROLL_NAV} ariaLabel="Payroll screens" />
      <Section flush>
        <DataTable
          ariaLabel="Pay heads"
          rows={components.data}
          loading={components.isLoading}
          error={components.error}
          onRetry={() => void components.refetch()}
          getRowId={(c) => c.id}
          onRowClick={canEdit ? (c) => !c.isStatutory && setEditing(c) : undefined}
          columns={[
            {
              key: 'name',
              header: 'Pay head',
              render: (c) => (
                <span style={{ display: 'inline-flex', gap: 6, alignItems: 'center' }}>
                  <Text weight="semibold">{c.name}</Text> <Caption1>{c.code}</Caption1>
                  {c.isStatutory && (
                    <Tooltip content="Statutory: calculated by the payroll engine" relationship="label">
                      <LockClosedRegular />
                    </Tooltip>
                  )}
                </span>
              ),
            },
            {
              key: 'kind',
              header: 'Kind',
              width: 110,
              render: (c) => (
                <Badge appearance="tint" color={c.kind === 'Earning' ? 'success' : 'warning'}>
                  {c.kind}
                </Badge>
              ),
            },
            {
              key: 'calc',
              header: 'Default',
              render: (c) =>
                c.isStatutory
                  ? 'By law'
                  : c.calculationType === 'PercentOfBasic'
                    ? `${c.defaultValue}% of basic`
                    : c.defaultValue
                      ? formatInr(c.defaultValue)
                      : 'Set per employee',
            },
            { key: 'tax', header: 'Taxable', width: 90, priority: 'low', render: (c) => (c.isTaxable ? 'Yes' : 'No') },
            { key: 'order', header: 'Order', width: 70, align: 'end', priority: 'low', render: (c) => c.sortOrder },
            {
              key: 'active',
              header: 'Status',
              width: 100,
              render: (c) => (
                <StatusBadge
                  status={c.isActive ? 'Active' : 'Inactive'}
                  tone={c.isActive ? 'success' : 'informative'}
                />
              ),
            },
          ]}
          emptyTitle="No pay heads"
          size="small"
        />
      </Section>
      {editing && <ComponentDrawer component={editing === 'new' ? null : editing} onClose={() => setEditing(null)} />}
    </Page>
  );
}

const schema = z
  .object({
    code: z
      .string()
      .trim()
      .min(1, 'Enter a short code, e.g. NIGHT.')
      .max(24)
      .regex(/^[A-Za-z0-9_]+$/, "Use letters, digits or '_'.")
      .refine((v) => !STATUTORY.includes(v.toUpperCase()), 'PF, ESI, PT and TDS are calculated by payroll.'),
    name: z.string().trim().min(1, 'Enter the name shown on payslips.').max(100),
    kind: z.enum(['Earning', 'Deduction']),
    calculationType: z.enum(['Fixed', 'PercentOfBasic']),
    defaultValue: z.number({ error: 'Enter a default (0 if set per employee).' }).min(0),
    isTaxable: z.boolean(),
    sortOrder: z.number({ error: 'Enter the order on the payslip.' }).int().min(0).max(800),
    isActive: z.boolean(),
  })
  .refine((v) => v.calculationType !== 'PercentOfBasic' || v.defaultValue <= 100, {
    path: ['defaultValue'],
    message: 'A percentage of basic cannot exceed 100.',
  });

type Values = z.infer<typeof schema>;

function ComponentDrawer({ component, onClose }: { component: SalaryComponentDto | null; onClose: () => void }) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const form = useForm<Values>({
    resolver: zodResolver(schema),
    defaultValues: {
      code: component?.code ?? '',
      name: component?.name ?? '',
      kind: component?.kind ?? 'Earning',
      calculationType: component?.calculationType ?? 'Fixed',
      defaultValue: component?.defaultValue ?? 0,
      isTaxable: component?.isTaxable ?? true,
      sortOrder: component?.sortOrder ?? 100,
      isActive: component?.isActive ?? true,
    },
  });
  const calculationType = useWatch({ control: form.control, name: 'calculationType' });

  const save = useMutation({
    mutationFn: (v: Values) =>
      hrApi.saveComponent(component?.id ?? null, {
        ...v,
        code: v.code.toUpperCase(),
        version: component?.version ?? null,
      }),
    onSuccess: () => {
      notify.success('Pay head saved', 'It applies from the next processing.');
      void queryClient.invalidateQueries({ queryKey: hrKeys.payroll });
      onClose();
    },
    onError: (error) => {
      if (isApiError(error) && error.code === 'salary_component.code_in_use') {
        form.setError('code', { type: 'server', message: error.userMessage });
      } else if (!applyServerErrors(error, form.setError)) {
        notify.error(error, 'Could not save the pay head');
      }
    },
  });

  return (
    <EditorDrawer
      open
      title={component ? component.name : 'Add pay head'}
      onClose={onClose}
      onSubmit={form.handleSubmit((v) => save.mutate(v))}
      busy={save.isPending}
    >
      <FormGrid columns={2} minColumnWidth={160}>
        <TextField
          control={form.control}
          name="code"
          label="Code"
          required
          autoFocus
          maxLength={24}
          disabled={component?.code === 'BASIC'}
        />
        <NumberField control={form.control} name="sortOrder" label="Order on payslip" required min={0} max={800} />
      </FormGrid>
      <TextField control={form.control} name="name" label="Name on payslip" required maxLength={100} />
      <ChoiceField
        control={form.control}
        name="kind"
        label="Kind"
        disabled={component?.code === 'BASIC'}
        options={[
          { value: 'Earning', label: 'Earning' },
          { value: 'Deduction', label: 'Deduction' },
        ]}
      />
      <ChoiceField
        control={form.control}
        name="calculationType"
        label="Worked out as"
        disabled={component?.code === 'BASIC'}
        options={[
          { value: 'Fixed', label: 'Fixed amount' },
          { value: 'PercentOfBasic', label: 'Percentage of basic' },
        ]}
      />
      <NumberField
        control={form.control}
        name="defaultValue"
        label={calculationType === 'PercentOfBasic' ? 'Default % of basic' : 'Default amount (₹ per month)'}
        required
        min={0}
        step={calculationType === 'PercentOfBasic' ? 0.5 : 100}
        hint="Suggested when a salary structure is created; can be changed per employee."
      />
      <SwitchField control={form.control} name="isTaxable" label="Taxable income" />
      <SwitchField control={form.control} name="isActive" label="In use" />
    </EditorDrawer>
  );
}
