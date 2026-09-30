import { Button, Caption1, Field, SearchBox, Select, Tab, TabList, Text } from '@fluentui/react-components';
import { AddRegular } from '@fluentui/react-icons';
import { formatNumber, useDebouncedValue } from '@hc360/core';
import { DataTable, FilterBar, Page, PageHeader, Section, StatusBadge } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import {
  prescriptionQueries,
  type DoseLimitDto,
  type DrugInteractionDto,
  type InteractionSeverity,
} from '@/modules/emr/prescriptionsApi';
import { DoseLimitDrawer, InteractionDrawer } from '@/modules/emr/SafetyTableEditors';

type SafetyTab = 'interactions' | 'dose-limits';
const SEVERITIES: InteractionSeverity[] = ['Major', 'Moderate', 'Minor'];

export const Route = createFileRoute('/_app/emr/drug-safety')({
  validateSearch: (
    s: Record<string, unknown>,
  ): { tab?: SafetyTab; severity?: InteractionSeverity; search?: string; page?: number } => ({
    tab: s.tab === 'dose-limits' ? 'dose-limits' : undefined,
    severity: SEVERITIES.includes(s.severity as InteractionSeverity) ? (s.severity as InteractionSeverity) : undefined,
    search: typeof s.search === 'string' && s.search ? s.search : undefined,
    page: typeof s.page === 'number' && s.page > 1 ? s.page : undefined,
  }),
  component: DrugSafetyPage,
});

function severityTone(severity: InteractionSeverity): 'danger' | 'warning' | 'informative' {
  return severity === 'Major' ? 'danger' : severity === 'Moderate' ? 'warning' : 'informative';
}

/** The prescribing-safety tables (ERX-02): drug–drug interactions and maximum adult daily doses. */
function DrugSafetyPage() {
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const canManage = usePermission('clinical.templates.manage');
  const tab: SafetyTab = search.tab ?? 'interactions';
  const [editingInteraction, setEditingInteraction] = useState<DrugInteractionDto | 'new' | null>(null);
  const [editingLimit, setEditingLimit] = useState<DoseLimitDto | 'new' | null>(null);

  return (
    <Page>
      <PageHeader
        title="Drug safety tables"
        subtitle="Checked on every prescription before signing. Major interactions and doses above the limit block signing unless the doctor gives a reason."
        actions={
          canManage && (
            <Button
              appearance="primary"
              icon={<AddRegular />}
              onClick={() => (tab === 'interactions' ? setEditingInteraction('new') : setEditingLimit('new'))}
            >
              {tab === 'interactions' ? 'Add interaction' : 'Add dose limit'}
            </Button>
          )
        }
      />
      <Section flush>
        <div style={{ padding: '0 8px' }}>
          <TabList
            selectedValue={tab}
            onTabSelect={(_, d) =>
              void navigate({ search: { tab: d.value === 'dose-limits' ? 'dose-limits' : undefined }, replace: true })
            }
          >
            <Tab value="interactions">Interactions</Tab>
            <Tab value="dose-limits">Maximum daily doses</Tab>
          </TabList>
        </div>
        {tab === 'interactions' ? (
          <InteractionsTable canManage={canManage} onEdit={setEditingInteraction} />
        ) : (
          <DoseLimitsTable canManage={canManage} onEdit={setEditingLimit} />
        )}
      </Section>
      <InteractionDrawer interaction={editingInteraction} onClose={() => setEditingInteraction(null)} />
      <DoseLimitDrawer limit={editingLimit} onClose={() => setEditingLimit(null)} />
    </Page>
  );
}

function InteractionsTable({
  canManage,
  onEdit,
}: {
  canManage: boolean;
  onEdit: (interaction: DrugInteractionDto) => void;
}) {
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const [term, setTerm] = useState(search.search ?? '');
  const debounced = useDebouncedValue(term.trim(), 300);
  const page = search.page ?? 1;
  const interactions = useQuery(prescriptionQueries.interactions(debounced, search.severity ?? null, page));
  const set = (patch: { severity?: InteractionSeverity; search?: string; page?: number }) =>
    void navigate({ search: (prev) => ({ ...prev, ...patch }), replace: true });

  return (
    <>
      <div style={{ padding: '12px 16px' }}>
        <FilterBar>
          <Field label="Drug" style={{ flex: '1 1 260px' }}>
            <SearchBox
              value={term}
              onChange={(_, d) => {
                setTerm(d.value);
                set({ search: d.value.trim() || undefined, page: undefined });
              }}
              placeholder="Generic name, e.g. warfarin"
            />
          </Field>
          <Field label="Severity">
            <Select
              value={search.severity ?? ''}
              onChange={(_, d) =>
                set({ severity: (d.value || undefined) as InteractionSeverity | undefined, page: undefined })
              }
            >
              <option value="">All</option>
              {SEVERITIES.map((s) => (
                <option key={s} value={s}>
                  {s}
                </option>
              ))}
            </Select>
          </Field>
        </FilterBar>
      </div>
      <DataTable
        ariaLabel="Drug interactions"
        rows={interactions.data?.items}
        loading={interactions.isLoading}
        error={interactions.error}
        onRetry={() => void interactions.refetch()}
        getRowId={(i) => i.id}
        onRowClick={canManage ? onEdit : undefined}
        size="small"
        columns={[
          {
            key: 'pair',
            header: 'Drugs',
            render: (i) => (
              <Text weight="semibold">
                {i.drugA} + {i.drugB}
              </Text>
            ),
          },
          {
            key: 'severity',
            header: 'Severity',
            width: 110,
            render: (i) => (
              <StatusBadge
                status={i.severity}
                tone={severityTone(i.severity)}
                label={i.severity === 'Major' ? 'Major (blocks)' : i.severity}
              />
            ),
          },
          { key: 'effect', header: 'Effect', render: (i) => i.effect },
          { key: 'management', header: 'What to do', priority: 'low', render: (i) => i.management ?? '—' },
          {
            key: 'active',
            header: 'Active',
            width: 90,
            render: (i) => (
              <StatusBadge status={i.isActive ? 'Active' : 'Inactive'} tone={i.isActive ? 'success' : 'informative'} />
            ),
          },
        ]}
        emptyTitle="No interactions match"
        emptyDescription={canManage ? 'Add an interaction to have it checked on every prescription.' : undefined}
        page={page}
        pageSize={25}
        totalCount={interactions.data?.totalCount}
        onPageChange={(next) => set({ page: next > 1 ? next : undefined })}
      />
    </>
  );
}

function DoseLimitsTable({ canManage, onEdit }: { canManage: boolean; onEdit: (limit: DoseLimitDto) => void }) {
  const limits = useQuery(prescriptionQueries.doseLimits());
  const [term, setTerm] = useState('');
  const rows = (limits.data ?? []).filter(
    (l) => !term.trim() || l.genericName.toLowerCase().includes(term.trim().toLowerCase()),
  );
  return (
    <>
      <div style={{ padding: '12px 16px' }}>
        <FilterBar>
          <Field label="Drug" style={{ flex: '1 1 260px' }}>
            <SearchBox value={term} onChange={(_, d) => setTerm(d.value)} placeholder="Generic name" />
          </Field>
        </FilterBar>
        <Caption1>
          Limits are summed per ingredient across the new prescription and the patient's current medicines.
        </Caption1>
      </div>
      <DataTable
        ariaLabel="Maximum daily doses"
        rows={limits.data ? rows : undefined}
        loading={limits.isLoading}
        error={limits.error}
        onRetry={() => void limits.refetch()}
        getRowId={(l) => l.id}
        onRowClick={canManage ? onEdit : undefined}
        size="small"
        columns={[
          { key: 'name', header: 'Generic', render: (l) => <Text weight="semibold">{l.genericName}</Text> },
          {
            key: 'max',
            header: 'Maximum per day',
            render: (l) =>
              l.maxDailyDoseMg >= 1000
                ? `${formatNumber(l.maxDailyDoseMg / 1000)} g`
                : `${formatNumber(l.maxDailyDoseMg)} mg`,
          },
          { key: 'route', header: 'Route', render: (l) => l.route ?? 'Any' },
          { key: 'notes', header: 'Notes', priority: 'low', render: (l) => l.notes ?? '—' },
          {
            key: 'active',
            header: 'Active',
            width: 90,
            render: (l) => (
              <StatusBadge status={l.isActive ? 'Active' : 'Inactive'} tone={l.isActive ? 'success' : 'informative'} />
            ),
          },
        ]}
        emptyTitle="No dose limits"
      />
    </>
  );
}
