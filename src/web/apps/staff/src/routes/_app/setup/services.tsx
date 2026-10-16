import {
  Button,
  Caption1,
  Field,
  makeStyles,
  SearchBox,
  Select,
  Switch,
  Text,
  tokens,
} from '@fluentui/react-components';
import { AddRegular } from '@fluentui/react-icons';
import { formatInr, useDebouncedValue } from '@hc360/core';
import { DataTable, FilterBar, Page, PageHeader, Section } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useEffect, useState } from 'react';
import { SERVICE_CATEGORIES, SERVICE_CATEGORY_LABEL, type ServiceCategory } from '@/modules/billing/labels';
import { RequirePermission } from '@/modules/hr/shared/NoAccess';
import { searchFlag, searchId, searchOneOf, searchPage, searchText } from '@/modules/lab/common/search';
import { MASTERS_MANAGE, MASTERS_PAGE_SIZE, mastersQueries } from '@/modules/masters/api';
import { InUseBadge, ListToolbar } from '@/modules/masters/parts';
import { ServiceEditorDrawer } from '@/modules/masters/ServiceEditor';

interface ServicesSearch {
  q?: string;
  category?: ServiceCategory;
  inactive?: true;
  page?: number;
  edit?: string;
}

export const Route = createFileRoute('/_app/setup/services')({
  validateSearch: (s: Record<string, unknown>): ServicesSearch => ({
    q: searchText(s.q),
    category: searchOneOf(s.category, SERVICE_CATEGORIES),
    inactive: searchFlag(s.inactive),
    page: searchPage(s.page),
    edit: s.edit === 'new' ? 'new' : searchId(s.edit),
  }),
  component: () => (
    <RequirePermission permission={MASTERS_MANAGE} what="the services">
      <ServicesPage />
    </RequirePermission>
  ),
});

const useStyles = makeStyles({
  stack: { display: 'flex', flexDirection: 'column', minWidth: 0 },
  muted: { color: tokens.colorNeutralForeground3 },
});

/** The service catalogue: everything the hospital charges for, with its base price and GST (HMS-03). */
function ServicesPage() {
  const styles = useStyles();
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const [term, setTerm] = useState(search.q ?? '');
  const debounced = useDebouncedValue(term.trim(), 300);
  const services = useQuery(
    mastersQueries.services({
      search: search.q,
      category: search.category,
      includeInactive: search.inactive,
      page: search.page,
    }),
  );
  const set = (patch: Partial<ServicesSearch>) =>
    void navigate({ search: (prev) => ({ ...prev, ...patch, page: undefined }) });

  useEffect(() => {
    if ((debounced || undefined) !== search.q) {
      void navigate({ search: (prev) => ({ ...prev, q: debounced || undefined, page: undefined }), replace: true });
    }
  }, [debounced, search.q, navigate]);

  const filtered = Boolean(search.q || search.category);

  return (
    <Page>
      <PageHeader
        title="Services and prices"
        subtitle="Everything the hospital charges for, with its base price and GST. Tariff plans set other prices for corporate, insurance and scheme patients."
        actions={
          <Button
            appearance="primary"
            icon={<AddRegular />}
            onClick={() => void navigate({ search: (prev) => ({ ...prev, edit: 'new' }) })}
          >
            Add service
          </Button>
        }
      />
      <Section flush>
        <ListToolbar>
          <FilterBar>
            <Field label="Search" style={{ flex: '1 1 240px' }}>
              <SearchBox value={term} onChange={(_, data) => setTerm(data.value)} placeholder="Name or code" />
            </Field>
            <Field label="Category">
              <Select
                value={search.category ?? ''}
                onChange={(_, d) => set({ category: (d.value || undefined) as ServiceCategory | undefined })}
              >
                <option value="">All categories</option>
                {SERVICE_CATEGORIES.map((category) => (
                  <option key={category} value={category}>
                    {SERVICE_CATEGORY_LABEL[category]}
                  </option>
                ))}
              </Select>
            </Field>
            <Switch
              checked={search.inactive === true}
              onChange={(_, d) => set({ inactive: d.checked || undefined })}
              label="Show those not in use"
            />
          </FilterBar>
        </ListToolbar>
        <DataTable
          ariaLabel="Services"
          rows={services.data?.items}
          loading={services.isLoading}
          error={services.error}
          onRetry={() => void services.refetch()}
          getRowId={(s) => s.id}
          onRowClick={(s) => void navigate({ search: (prev) => ({ ...prev, edit: s.id }) })}
          columns={[
            {
              key: 'name',
              header: 'Service',
              render: (s) => (
                <span className={styles.stack}>
                  <Text weight="semibold">{s.name}</Text>
                  {s.shortName && <Caption1 className={styles.muted}>{s.shortName}</Caption1>}
                </span>
              ),
            },
            { key: 'code', header: 'Code', width: 140, render: (s) => <Text font="monospace">{s.code}</Text> },
            {
              key: 'category',
              header: 'Category',
              width: 180,
              priority: 'low',
              render: (s) => SERVICE_CATEGORY_LABEL[s.category],
            },
            { key: 'price', header: 'Base price', width: 120, align: 'end', render: (s) => formatInr(s.basePrice) },
            { key: 'gst', header: 'GST', width: 70, align: 'end', priority: 'low', render: (s) => `${s.taxRate}%` },
            { key: 'status', header: 'Status', width: 120, render: (s) => <InUseBadge isActive={s.isActive} /> },
          ]}
          emptyTitle={filtered ? 'No service matches' : 'No services yet'}
          emptyDescription={
            filtered ? 'Clear the search or choose another category.' : 'Add the services the hospital charges for.'
          }
          emptyAction={
            <Button
              appearance="primary"
              icon={<AddRegular />}
              onClick={() => void navigate({ search: (prev) => ({ ...prev, edit: 'new' }) })}
            >
              Add service
            </Button>
          }
          page={search.page ?? 1}
          pageSize={MASTERS_PAGE_SIZE}
          totalCount={services.data?.totalCount}
          onPageChange={(page) => void navigate({ search: (prev) => ({ ...prev, page: page > 1 ? page : undefined }) })}
        />
      </Section>
      {search.edit && (
        <ServiceEditorDrawer
          key={search.edit}
          serviceId={search.edit === 'new' ? null : search.edit}
          onClose={() => void navigate({ search: (prev) => ({ ...prev, edit: undefined }) })}
        />
      )}
    </Page>
  );
}
