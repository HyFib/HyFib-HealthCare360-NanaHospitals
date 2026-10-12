import { Button, Text } from '@fluentui/react-components';
import { AddRegular, BriefcaseRegular } from '@fluentui/react-icons';
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
import { hrApi, hrKeys, hrQueries, type DesignationDto } from '@/modules/hr/api';
import { HR_PERMISSIONS as P, STAFF_CATEGORIES } from '@/modules/hr/labels';
import { EditorDrawer } from '@/modules/hr/shared/EditorDrawer';
import { RequirePermission } from '@/modules/hr/shared/NoAccess';

export const Route = createFileRoute('/_app/hr/designations')({
  component: () => (
    <RequirePermission permission={[P.employeesRead, P.employeesManage]} what="designations">
      <DesignationsPage />
    </RequirePermission>
  ),
});

function DesignationsPage() {
  const canManage = usePermission(P.employeesManage);
  const designations = useQuery(hrQueries.designations(true));
  const [editing, setEditing] = useState<DesignationDto | 'new' | null>(null);

  return (
    <Page maxWidth={1100}>
      <PageHeader
        title="Designations"
        subtitle="Posts used on the employee record, payslips and NABH staffing returns"
        icon={<BriefcaseRegular />}
        actions={
          canManage && (
            <Button appearance="primary" icon={<AddRegular />} onClick={() => setEditing('new')}>
              Add designation
            </Button>
          )
        }
      />
      <Section flush>
        <DataTable
          ariaLabel="Designations"
          rows={designations.data}
          loading={designations.isLoading}
          error={designations.error}
          onRetry={() => void designations.refetch()}
          getRowId={(d) => d.id}
          onRowClick={canManage ? (d) => setEditing(d) : undefined}
          columns={[
            { key: 'code', header: 'Code', width: 120, render: (d) => <Text font="monospace">{d.code}</Text> },
            { key: 'name', header: 'Designation', render: (d) => <Text weight="semibold">{d.name}</Text> },
            { key: 'category', header: 'Staff category', render: (d) => d.category },
            { key: 'grade', header: 'Grade', width: 90, align: 'end', render: (d) => d.grade },
            {
              key: 'active',
              header: 'Status',
              width: 110,
              render: (d) => (
                <StatusBadge
                  status={d.isActive ? 'Active' : 'Inactive'}
                  tone={d.isActive ? 'success' : 'informative'}
                />
              ),
            },
          ]}
          emptyTitle="No designations yet"
          size="small"
        />
      </Section>
      {editing && (
        <DesignationDrawer designation={editing === 'new' ? null : editing} onClose={() => setEditing(null)} />
      )}
    </Page>
  );
}

const schema = z.object({
  code: z
    .string()
    .trim()
    .min(1, 'Enter a short code.')
    .max(24)
    .regex(/^[A-Za-z0-9_-]+$/, "Use letters, digits, '-' or '_'."),
  name: z.string().trim().min(1, 'Enter the designation.').max(100),
  category: z.enum(STAFF_CATEGORIES),
  grade: z.number({ error: 'Enter the grade (0 to 99).' }).int().min(0).max(99),
  isActive: z.boolean(),
});

type Values = z.infer<typeof schema>;

function DesignationDrawer({ designation, onClose }: { designation: DesignationDto | null; onClose: () => void }) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const form = useForm<Values>({
    resolver: zodResolver(schema),
    defaultValues: {
      code: designation?.code ?? '',
      name: designation?.name ?? '',
      category: designation?.category ?? 'Nursing',
      grade: designation?.grade ?? 1,
      isActive: designation?.isActive ?? true,
    },
  });
  const save = useMutation({
    mutationFn: (v: Values) =>
      hrApi.saveDesignation(designation?.id ?? null, {
        ...v,
        code: v.code.toUpperCase(),
        version: designation?.version ?? null,
      }),
    onSuccess: () => {
      notify.success('Designation saved');
      void queryClient.invalidateQueries({ queryKey: hrKeys.masters });
      onClose();
    },
    onError: (error) => {
      if (isApiError(error) && error.code === 'designation.code_in_use') {
        form.setError('code', { type: 'server', message: error.userMessage });
      } else if (!applyServerErrors(error, form.setError)) {
        notify.error(error, 'Could not save the designation');
      }
    },
  });

  return (
    <EditorDrawer
      open
      title={designation ? designation.name : 'Add designation'}
      dirty={form.formState.isDirty}
      onClose={onClose}
      onSubmit={form.handleSubmit((v) => save.mutate(v))}
      busy={save.isPending}
    >
      <FormGrid columns={2} minColumnWidth={160}>
        <TextField control={form.control} name="code" label="Code" required autoFocus maxLength={24} />
        <NumberField control={form.control} name="grade" label="Grade" required min={0} max={99} />
      </FormGrid>
      <TextField control={form.control} name="name" label="Designation" required maxLength={100} />
      <SelectField
        control={form.control}
        name="category"
        label="Staff category"
        required
        options={STAFF_CATEGORIES.map((c) => ({ value: c, label: c }))}
      />
      <SwitchField control={form.control} name="isActive" label="In use" />
    </EditorDrawer>
  );
}
