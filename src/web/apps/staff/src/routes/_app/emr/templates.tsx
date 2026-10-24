import { Badge, Button, Caption1, Field, SearchBox, Select, Switch, Text } from '@fluentui/react-components';
import { AddRegular } from '@fluentui/react-icons';
import { useDebouncedValue } from '@hc360/core';
import { DataTable, FilterBar, Page, PageHeader, Section, StatusBadge } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { emrQueries } from '@/modules/emr/api';
import { useSpecialtyOptions } from '@/modules/emr/catalogue';
import { TemplateDrawer } from '@/modules/emr/TemplateDrawer';

interface TemplatesSearch {
  specialty?: string;
  search?: string;
  inactive?: boolean;
}

export const Route = createFileRoute('/_app/emr/templates')({
  validateSearch: (s: Record<string, unknown>): TemplatesSearch => ({
    specialty: typeof s.specialty === 'string' && s.specialty ? s.specialty : undefined,
    search: typeof s.search === 'string' && s.search ? s.search : undefined,
    inactive: s.inactive === true ? true : undefined,
  }),
  component: TemplatesPage,
});

/**
 * The clinical note templates (EMR-02): shared and the doctor's own, by speciality. A template fills an encounter's note
 * and proposes its complaints, orders and medicines; staff who maintain them add and edit them here.
 */
function TemplatesPage() {
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const canManage = usePermission('clinical.templates.manage');
  const [term, setTerm] = useState(search.search ?? '');
  const debounced = useDebouncedValue(term.trim(), 300);
  const [editing, setEditing] = useState<string | 'new' | null>(null);
  const specialties = useSpecialtyOptions(search.specialty);
  const templates = useQuery(
    emrQueries.templateCatalogue({
      specialtyCode: search.specialty ?? null,
      search: debounced,
      includeInactive: search.inactive ?? false,
    }),
  );
  const set = (patch: TemplatesSearch) => void navigate({ search: (prev) => ({ ...prev, ...patch }), replace: true });

  return (
    <Page>
      <PageHeader
        title="Clinical note templates"
        subtitle="Starting points for an encounter's note, by speciality: the note text, common complaints, default orders and medicines."
        actions={
          canManage && (
            <Button appearance="primary" icon={<AddRegular />} onClick={() => setEditing('new')}>
              New template
            </Button>
          )
        }
      />
      <Section flush>
        <div style={{ padding: '12px 16px' }}>
          <FilterBar>
            <Field label="Template" style={{ flex: '1 1 260px' }}>
              <SearchBox
                value={term}
                onChange={(_, d) => {
                  setTerm(d.value);
                  set({ search: d.value.trim() || undefined });
                }}
                placeholder="Name or code"
              />
            </Field>
            <Field label="Speciality">
              <Select value={search.specialty ?? ''} onChange={(_, d) => set({ specialty: d.value || undefined })}>
                <option value="">All specialities</option>
                {specialties.options.map((o) => (
                  <option key={o.value} value={o.value}>
                    {o.label}
                  </option>
                ))}
              </Select>
            </Field>
            <Switch
              label="Show inactive"
              checked={search.inactive ?? false}
              onChange={(_, d) => set({ inactive: d.checked || undefined })}
            />
          </FilterBar>
        </div>
        <DataTable
          ariaLabel="Clinical note templates"
          rows={templates.data}
          loading={templates.isLoading}
          error={templates.error}
          onRetry={() => void templates.refetch()}
          getRowId={(t) => t.id}
          onRowClick={canManage ? (t) => setEditing(t.id) : undefined}
          size="small"
          columns={[
            {
              key: 'name',
              header: 'Template',
              render: (t) => (
                <span style={{ display: 'flex', flexDirection: 'column' }}>
                  <Text weight="semibold">{t.name}</Text>
                  <Caption1>{t.code}</Caption1>
                </span>
              ),
            },
            {
              key: 'specialty',
              header: 'Speciality',
              render: (t) => (t.specialtyCode ? specialties.nameOf(t.specialtyCode) : 'General'),
            },
            {
              key: 'kind',
              header: 'Shared with',
              width: 130,
              render: (t) =>
                t.isPersonal ? (
                  <Badge appearance="tint" color="brand">
                    Only me
                  </Badge>
                ) : (
                  'Every doctor'
                ),
            },
            { key: 'order', header: 'Order', width: 80, priority: 'low', render: (t) => t.displayOrder },
            {
              key: 'active',
              header: 'Active',
              width: 90,
              render: (t) => (
                <StatusBadge
                  status={t.isActive ? 'Active' : 'Inactive'}
                  tone={t.isActive ? 'success' : 'informative'}
                />
              ),
            },
          ]}
          emptyTitle="No templates match"
          emptyDescription={canManage ? 'Add a template to give doctors a starting point for their notes.' : undefined}
          emptyAction={
            canManage ? (
              <Button appearance="primary" icon={<AddRegular />} onClick={() => setEditing('new')}>
                New template
              </Button>
            ) : undefined
          }
        />
      </Section>
      <TemplateDrawer templateId={editing} onClose={() => setEditing(null)} />
    </Page>
  );
}
