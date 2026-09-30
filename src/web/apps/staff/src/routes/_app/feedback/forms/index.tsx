import { Badge, Button, Caption1, Checkbox, Field, Select, Text, tokens } from '@fluentui/react-components';
import { AddRegular, QrCodeRegular } from '@fluentui/react-icons';
import { DataTable, FilterBar, Page, PageHeader, Section } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useDepartments } from '@/lib/lookups';
import { usePermission } from '@/lib/permissions';
import { feedbackQueries, type FeedbackFormDto } from '@/modules/feedback/api';
import {
  aspectLabel,
  FEEDBACK_PERMISSIONS as F,
  FORM_TYPES,
  FORM_VIEWERS,
  formTypeLabel,
  type FeedbackFormType,
} from '@/modules/feedback/labels';
import { FilterPad } from '@/modules/hr/shared/layout';
import { RequirePermission } from '@/modules/hr/shared/NoAccess';

interface FormsSearch {
  type?: FeedbackFormType;
  all?: boolean;
}

export const Route = createFileRoute('/_app/feedback/forms/')({
  validateSearch: (s: Record<string, unknown>): FormsSearch => ({
    type: FORM_TYPES.includes(s.type as FeedbackFormType) ? (s.type as FeedbackFormType) : undefined,
    all: s.all === true ? true : undefined,
  }),
  component: () => (
    <RequirePermission permission={FORM_VIEWERS} what="feedback forms">
      <FormsPage />
    </RequirePermission>
  ),
});

function questionSummary(form: FeedbackFormDto): string {
  const parts: string[] = [];
  if (form.aspects.length > 0) {
    parts.push(
      form.aspects.length <= 3 ? form.aspects.map((a) => aspectLabel[a]).join(', ') : `${form.aspects.length} ratings`,
    );
  }
  if (form.includesNps) parts.push('NPS');
  if (form.allowsComment) parts.push('comment');
  return parts.join(' · ');
}

function FormsPage() {
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const canManage = usePermission(F.manage);
  const departments = useDepartments();
  const forms = useQuery(feedbackQueries.forms({ type: search.type, activeOnly: search.all ? undefined : true }));
  const departmentName = new Map((departments.data ?? []).map((d) => [d.id, d.name]));

  const set = (patch: Partial<FormsSearch>) => void navigate({ search: (prev) => ({ ...prev, ...patch }) });

  return (
    <Page>
      <PageHeader
        title="Feedback forms and QR codes"
        subtitle="Questionnaires for out-patients, in-patients, the portal and QR posters. Print a form's QR code for posters and kiosks."
        icon={<QrCodeRegular />}
        breadcrumbs={[{ label: 'Feedback analytics', onClick: () => void navigate({ to: '/feedback' }) }]}
        actions={
          canManage && (
            <Button
              appearance="primary"
              icon={<AddRegular />}
              onClick={() => void navigate({ to: '/feedback/forms/new' })}
            >
              New form
            </Button>
          )
        }
      />
      <Section flush>
        <FilterPad>
          <FilterBar>
            <Field label="Used for">
              <Select
                value={search.type ?? ''}
                onChange={(_, d) => set({ type: (d.value || undefined) as FeedbackFormType | undefined })}
              >
                <option value="">All</option>
                {FORM_TYPES.map((t) => (
                  <option key={t} value={t}>
                    {formTypeLabel[t]}
                  </option>
                ))}
              </Select>
            </Field>
            <Checkbox
              style={{ alignSelf: 'flex-end' }}
              checked={Boolean(search.all)}
              onChange={(_, d) => set({ all: d.checked ? true : undefined })}
              label="Show inactive forms"
            />
          </FilterBar>
        </FilterPad>
        <DataTable
          ariaLabel="Feedback forms"
          rows={forms.data}
          loading={forms.isLoading}
          error={forms.error}
          onRetry={() => void forms.refetch()}
          getRowId={(f) => f.id}
          onRowClick={(f) => void navigate({ to: '/feedback/forms/$formId', params: { formId: f.id } })}
          columns={[
            { key: 'code', header: 'Code', width: 130, render: (f) => <Text font="monospace">{f.code}</Text> },
            {
              key: 'title',
              header: 'Form',
              render: (f) => (
                <span>
                  <Text weight="semibold">{f.title}</Text>
                  <Caption1 block style={{ color: tokens.colorNeutralForeground3 }}>
                    {questionSummary(f)}
                  </Caption1>
                </span>
              ),
            },
            { key: 'type', header: 'Used for', render: (f) => formTypeLabel[f.type] },
            {
              key: 'department',
              header: 'Department',
              priority: 'low',
              render: (f) => (f.departmentId ? (departmentName.get(f.departmentId) ?? '—') : 'Whole hospital'),
            },
            {
              key: 'status',
              header: 'Status',
              render: (f) =>
                f.isActive ? (
                  <Badge appearance="tint" color="success" shape="rounded">
                    Active
                  </Badge>
                ) : (
                  <Badge appearance="tint" color="subtle" shape="rounded">
                    Inactive
                  </Badge>
                ),
            },
          ]}
          emptyTitle="No feedback forms yet"
          emptyDescription={
            canManage
              ? 'Create a form, then print its QR code for posters and kiosks.'
              : 'A feedback manager can create the forms.'
          }
          emptyAction={
            canManage ? (
              <Button
                appearance="primary"
                icon={<AddRegular />}
                onClick={() => void navigate({ to: '/feedback/forms/new' })}
              >
                New form
              </Button>
            ) : undefined
          }
        />
      </Section>
    </Page>
  );
}
