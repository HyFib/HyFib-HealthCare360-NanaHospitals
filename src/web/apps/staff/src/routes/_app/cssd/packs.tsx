import { Badge, Button, Field, Select, Switch, Text } from '@fluentui/react-components';
import { BoxArrowUpRegular, BoxMultipleRegular } from '@fluentui/react-icons';
import { formatDateOnly, todayIso } from '@hc360/core';
import { DataTable, FilterBar, Page, PageHeader, Section, StatusBadge } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { usePermission } from '@/lib/permissions';
import { cssdQueries, PACK_STATUS_LABEL, type PackStatus } from '@/modules/cssd/api';
import { daysToExpiry } from '@/modules/cssd/rules';

const STATUSES: PackStatus[] = ['Available', 'InProcess', 'Issued', 'Returned', 'Recalled', 'Reprocess'];

interface PacksSearch {
  setId?: string;
  status?: PackStatus;
  expiring?: boolean;
}

export const Route = createFileRoute('/_app/cssd/packs')({
  validateSearch: (s: Record<string, unknown>): PacksSearch => ({
    setId: typeof s.setId === 'string' && s.setId ? s.setId : undefined,
    status: STATUSES.includes(s.status as PackStatus) ? (s.status as PackStatus) : undefined,
    expiring: s.expiring === true ? true : undefined,
  }),
  component: SterileStockPage,
});

/** Sterile packs by status, soonest expiry first (first-expiry, first-out). */
function SterileStockPage() {
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const canIssue = usePermission('cssd.process');
  const sets = useQuery(cssdQueries.sets());
  const packs = useQuery(cssdQueries.packs({ setId: search.setId, status: search.status, expiringWithinDays: search.expiring ? 2 : undefined }));
  const today = todayIso();
  const set = (patch: Partial<PacksSearch>) => void navigate({ search: (prev) => ({ ...prev, ...patch }), replace: true });
  const status = search.status ?? 'Available';

  return (
    <Page>
      <PageHeader
        title="Sterile stock"
        icon={<BoxMultipleRegular />}
        subtitle={`${PACK_STATUS_LABEL[status]} packs, soonest expiry first`}
        actions={
          canIssue && (
            <Button appearance="primary" icon={<BoxArrowUpRegular />} onClick={() => void navigate({ to: '/cssd/issue' })}>
              Issue packs
            </Button>
          )
        }
      />
      <Section flush>
        <div style={{ padding: '0 16px 12px' }}>
          <FilterBar>
            <Field label="Set">
              <Select value={search.setId ?? ''} onChange={(_, d) => set({ setId: d.value || undefined })}>
                <option value="">All sets</option>
                {sets.data?.map((s) => (
                  <option key={s.id} value={s.id}>
                    {s.name}
                  </option>
                ))}
              </Select>
            </Field>
            <Field label="Status">
              <Select value={status} onChange={(_, d) => set({ status: d.value === 'Available' ? undefined : (d.value as PackStatus) })}>
                {STATUSES.map((s) => (
                  <option key={s} value={s}>
                    {PACK_STATUS_LABEL[s]}
                  </option>
                ))}
              </Select>
            </Field>
            <Switch checked={Boolean(search.expiring)} onChange={(_, d) => set({ expiring: d.checked || undefined })} label="Expiring within 2 days" />
          </FilterBar>
        </div>
        <DataTable
          ariaLabel="Sterile packs"
          rows={packs.data}
          loading={packs.isLoading}
          error={packs.error}
          onRetry={() => void packs.refetch()}
          getRowId={(p) => p.id}
          onRowClick={(p) => void navigate({ to: '/cssd/loads/$loadId', params: { loadId: p.loadId } })}
          size="small"
          columns={[
            { key: 'number', header: 'Pack', width: 200, render: (p) => <Text font="monospace">{p.packNumber}</Text> },
            { key: 'set', header: 'Set', render: (p) => p.setName ?? '—' },
            { key: 'cycle', header: 'Cycle', width: 170, priority: 'low', render: (p) => <Text font="monospace">{p.cycleNumber ?? '—'}</Text> },
            {
              key: 'expiry',
              header: 'Use by',
              width: 170,
              render: (p) => {
                if (!p.sterileUntil) {
                  return '—';
                }
                const days = daysToExpiry(p.sterileUntil, today);
                return (
                  <span>
                    {formatDateOnly(p.sterileUntil)}{' '}
                    {p.isExpired ? (
                      <Badge size="small" appearance="tint" color="danger">
                        Expired: reprocess
                      </Badge>
                    ) : days !== null && days <= 2 ? (
                      <Badge size="small" appearance="tint" color="warning">
                        {days === 0 ? 'Last day' : `${days} days left`}
                      </Badge>
                    ) : null}
                  </span>
                );
              },
            },
            { key: 'status', header: 'Status', width: 140, render: (p) => <StatusBadge status={p.status} label={PACK_STATUS_LABEL[p.status]} tone={p.status === 'Available' ? 'success' : p.status === 'Recalled' || p.status === 'Reprocess' ? 'danger' : undefined} /> },
          ]}
          emptyTitle={search.expiring ? 'No packs expiring soon' : 'No packs'}
          emptyDescription="Packs appear here when a sterilizer load is released."
        />
      </Section>
    </Page>
  );
}
