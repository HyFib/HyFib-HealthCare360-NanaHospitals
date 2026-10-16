import { Badge, Button, Caption1, Field, SearchBox, Select, Switch, Text, tokens } from '@fluentui/react-components';
import { AddRegular, AlertUrgentRegular } from '@fluentui/react-icons';
import { formatRelative, humanize, useDebouncedValue } from '@hc360/core';
import { DataTable, FilterBar, Page, PageHeader, Section, StatusBadge } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useEffect, useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { workflowTone } from '@/modules/inventory/labels';
import { useWorkingStore } from '@/modules/inventory/stores';
import {
  pharmacyQueries,
  WARD_INDENT_STATUSES,
  type WardIndentListItem,
  type WardIndentStatus,
} from '@/modules/pharmacy/api';
import { usePharmacyLive } from '@/modules/pharmacy/counter';
import { PHARMACY_PERMISSIONS as P } from '@/modules/pharmacy/permissions';
import { WardIndentDialog } from '@/modules/pharmacy/WardIndentDialog';

interface IndentSearch {
  status?: WardIndentStatus;
  closed?: boolean;
  q?: string;
  page?: number;
  raise?: boolean;
}

export const Route = createFileRoute('/_app/pharmacy/ward-indents/')({
  validateSearch: (s: Record<string, unknown>): IndentSearch => ({
    status: WARD_INDENT_STATUSES.includes(s.status as WardIndentStatus) ? (s.status as WardIndentStatus) : undefined,
    closed: s.closed === true || s.closed === 'true' ? true : undefined,
    q: typeof s.q === 'string' && s.q ? s.q : undefined,
    page: typeof s.page === 'number' && s.page > 1 ? s.page : undefined,
    raise: s.raise === true || s.raise === 'true' || s.raise === 1 || s.raise === '1' ? true : undefined,
  }),
  component: WardIndentsPage,
});

/** Ward indents (PH-03): STAT first, then oldest. Nurses raise them; the IP pharmacy issues against them. */
function WardIndentsPage() {
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const canRaise = usePermission(P.ipIndent);
  const canIssue = usePermission(P.ipIssue);
  const ipStore = useWorkingStore('pharmacy-ip', { type: 'Pharmacy' });
  const [term, setTerm] = useState(search.q ?? '');
  const [raising, setRaising] = useState(false);
  const debounced = useDebouncedValue(term.trim(), 300);
  usePharmacyLive();

  useEffect(() => {
    if ((debounced || undefined) !== search.q) {
      void navigate({ search: (prev) => ({ ...prev, q: debounced || undefined, page: undefined }), replace: true });
    }
  }, [debounced, search.q, navigate]);

  const indents = useQuery(
    pharmacyQueries.wardIndents({
      status: search.status,
      includeClosed: search.closed,
      search: search.q,
      page: search.page,
    }),
  );
  const set = (patch: Partial<IndentSearch>) =>
    void navigate({ search: (prev) => ({ ...prev, ...patch, page: undefined }) });
  const showRaise = canRaise && (raising || search.raise === true);

  return (
    <Page>
      <PageHeader
        title="Ward indents"
        subtitle={
          canIssue
            ? 'Requests from the wards, STAT first. Open one to issue the medicines.'
            : 'Your requests to the IP pharmacy and what has been issued.'
        }
        actions={
          canRaise && (
            <Button appearance="primary" icon={<AddRegular />} onClick={() => setRaising(true)}>
              Raise ward indent
            </Button>
          )
        }
      />
      <Section flush>
        <div style={{ padding: '0 16px 12px' }}>
          <FilterBar>
            <Field label="Search" style={{ flex: '1 1 260px' }}>
              <SearchBox
                value={term}
                onChange={(_, d) => setTerm(d.value)}
                placeholder="Patient name, UHID or indent number"
              />
            </Field>
            <Field label="Status">
              <Select
                value={search.status ?? ''}
                onChange={(_, d) => set({ status: (d.value || undefined) as WardIndentStatus | undefined })}
              >
                <option value="">Still to issue</option>
                {WARD_INDENT_STATUSES.map((s) => (
                  <option key={s} value={s}>
                    {humanize(s)}
                  </option>
                ))}
              </Select>
            </Field>
            <Switch
              checked={search.closed === true}
              onChange={(_, d) => set({ closed: d.checked || undefined })}
              label="Include finished"
              disabled={Boolean(search.status)}
            />
          </FilterBar>
        </div>
        <DataTable<WardIndentListItem>
          ariaLabel="Ward indents"
          rows={indents.data?.items}
          loading={indents.isLoading}
          error={indents.error}
          onRetry={() => void indents.refetch()}
          getRowId={(i) => i.id}
          onRowClick={(i) => void navigate({ to: '/pharmacy/ward-indents/$indentId', params: { indentId: i.id } })}
          size="small"
          columns={[
            {
              key: 'urgency',
              header: 'Urgency',
              width: 90,
              render: (i) =>
                i.urgency === 'Stat' ? (
                  <Badge color="danger" icon={<AlertUrgentRegular />}>
                    STAT
                  </Badge>
                ) : (
                  <Caption1>Routine</Caption1>
                ),
            },
            { key: 'number', header: 'Indent', render: (i) => <Text font="monospace">{i.number}</Text> },
            {
              key: 'patient',
              header: 'Patient',
              render: (i) => (
                <span>
                  <Text weight="semibold">{i.patientName}</Text>{' '}
                  <Caption1 style={{ color: tokens.colorNeutralForeground3 }}>{i.uhid}</Caption1>
                </span>
              ),
            },
            {
              key: 'ward',
              header: 'Ward / bed',
              render: (i) => [i.wardName, i.bedLabel].filter(Boolean).join(' / ') || '—',
            },
            { key: 'items', header: 'Items', width: 70, align: 'end', render: (i) => i.lineCount },
            {
              key: 'raised',
              header: 'Raised',
              priority: 'low',
              render: (i) => (
                <span>
                  {formatRelative(i.raisedAt)}
                  {i.raisedBy && <Caption1 block>{i.raisedBy}</Caption1>}
                </span>
              ),
            },
            {
              key: 'status',
              header: 'Status',
              render: (i) => <StatusBadge status={i.status} tone={workflowTone(i.status)} />,
            },
          ]}
          emptyTitle={search.q ? 'No indent matches' : 'No ward indents waiting'}
          emptyDescription={
            canRaise
              ? 'Raise an indent for an admitted patient when the ward needs medicines.'
              : 'New indents appear here as the wards raise them.'
          }
          emptyAction={
            canRaise ? (
              <Button appearance="primary" icon={<AddRegular />} onClick={() => setRaising(true)}>
                Raise ward indent
              </Button>
            ) : undefined
          }
          page={search.page ?? 1}
          pageSize={25}
          totalCount={indents.data?.totalCount}
          onPageChange={(p) => void navigate({ search: (prev) => ({ ...prev, page: p > 1 ? p : undefined }) })}
        />
      </Section>
      {showRaise && (
        <WardIndentDialog
          ipStoreId={ipStore.storeId}
          onClose={() => {
            setRaising(false);
            if (search.raise) {
              void navigate({ search: (prev) => ({ ...prev, raise: undefined }), replace: true });
            }
          }}
          onRaised={(id) =>
            canIssue && void navigate({ to: '/pharmacy/ward-indents/$indentId', params: { indentId: id } })
          }
        />
      )}
    </Page>
  );
}
