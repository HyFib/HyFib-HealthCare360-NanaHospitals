import { Badge, Button, Caption1, Field, Input, Switch, Text, tokens } from '@fluentui/react-components';
import { ArrowDownloadRegular, WarningRegular } from '@fluentui/react-icons';
import { formatDateOnly, formatDateTime, todayIso } from '@hc360/core';
import { DataTable, FilterBar, Page, PageHeader, Section } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { cssdQueries } from '@/modules/cssd/api';
import { SoiledReceiptDialog } from '@/modules/cssd/SoiledReceiptDialog';
import { addDays } from '@/modules/inpatient/time';

const DATE = /^\d{4}-\d{2}-\d{2}$/;

interface ReceiptsSearch {
  from?: string;
  to?: string;
  discrepancies?: boolean;
}

export const Route = createFileRoute('/_app/cssd/receipts')({
  validateSearch: (s: Record<string, unknown>): ReceiptsSearch => ({
    from: typeof s.from === 'string' && DATE.test(s.from) ? s.from : undefined,
    to: typeof s.to === 'string' && DATE.test(s.to) ? s.to : undefined,
    discrepancies: s.discrepancies === true ? true : undefined,
  }),
  component: SoiledReceiptsPage,
});

/** Used trays received from departments with their instrument counts (CSSD-03). */
function SoiledReceiptsPage() {
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const canReceive = usePermission('cssd.process');
  const [receiving, setReceiving] = useState(false);
  const today = todayIso();
  const to = search.to ?? today;
  const from = search.from ?? addDays(to, -6);
  const receipts = useQuery(cssdQueries.receipts({ from, to, discrepanciesOnly: search.discrepancies }));
  const set = (patch: Partial<ReceiptsSearch>) => void navigate({ search: (prev) => ({ ...prev, ...patch }), replace: true });

  return (
    <Page>
      <PageHeader
        title="Soiled receipt"
        subtitle={`Used trays received · ${formatDateOnly(from)} to ${formatDateOnly(to)}`}
        actions={
          canReceive && (
            <Button appearance="primary" icon={<ArrowDownloadRegular />} onClick={() => setReceiving(true)}>
              Receive trays
            </Button>
          )
        }
      />
      <Section flush>
        <div style={{ padding: '0 16px 12px' }}>
          <FilterBar>
            <Field label="From">
              <Input type="date" value={from} max={to} onChange={(_, d) => d.value && set({ from: d.value })} />
            </Field>
            <Field label="To">
              <Input type="date" value={to} min={from} max={today} onChange={(_, d) => d.value && set({ to: d.value })} />
            </Field>
            <Switch checked={Boolean(search.discrepancies)} onChange={(_, d) => set({ discrepancies: d.checked || undefined })} label="Only with count differences" />
          </FilterBar>
        </div>
        <DataTable
          ariaLabel="Soiled receipts"
          rows={receipts.data}
          loading={receipts.isLoading}
          error={receipts.error}
          onRetry={() => void receipts.refetch()}
          getRowId={(r) => r.id}
          columns={[
            { key: 'at', header: 'Received', width: 170, render: (r) => formatDateTime(r.receivedAt) },
            {
              key: 'dept',
              header: 'From',
              render: (r) => (
                <span>
                  <Text weight="semibold">{r.departmentName ?? '—'}</Text>
                  <Caption1 block>
                    {r.handedOverByName ? `${r.handedOverByName} → ` : ''}
                    {r.receivedByName ?? ''}
                  </Caption1>
                </span>
              ),
            },
            {
              key: 'trays',
              header: 'Trays',
              render: (r) => (
                <span>
                  {r.lines.map((l, index) => (
                    <Caption1 key={`${l.instrumentSetId}-${index}`} block style={l.discrepancies ? { color: tokens.colorPaletteRedForeground1 } : undefined}>
                      {l.setName ?? 'Tray'}: {l.receivedCount}/{l.expectedCount}
                      {l.discrepancies ? ` · ${l.discrepancies}` : ''}
                    </Caption1>
                  ))}
                </span>
              ),
            },
            {
              key: 'status',
              header: 'Count',
              width: 150,
              render: (r) =>
                r.hasDiscrepancy ? (
                  <Badge appearance="tint" color="danger" icon={<WarningRegular />}>
                    Differences
                  </Badge>
                ) : (
                  <Badge appearance="tint" color="success">
                    Complete
                  </Badge>
                ),
            },
          ]}
          emptyTitle={search.discrepancies ? 'No count differences in this period' : 'No trays received in this period'}
          emptyDescription={canReceive ? 'Receive used trays from OT and wards and count them against the count sheet.' : undefined}
        />
      </Section>
      {receiving && <SoiledReceiptDialog onClose={() => setReceiving(false)} />}
    </Page>
  );
}
