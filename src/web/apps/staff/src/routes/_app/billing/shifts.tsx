import {
  Button,
  Caption1,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Field,
  Input,
  Select,
  Text,
  tokens,
} from '@fluentui/react-components';
import { DocumentTableRegular, DoorArrowLeftRegular, LockClosedRegular } from '@fluentui/react-icons';
import { formatDateTime, formatInr, todayIso, useSession } from '@hc360/core';
import { DataTable, ErrorState, FilterBar, LoadingState, Page, PageHeader, Section, StatusBadge } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { billingQueries, type ShiftDto } from '@/modules/billing/api';
import { useBillingLive } from '@/modules/billing/hooks';
import { PAYMENT_MODE_LABEL, SHIFT_STATUSES, type ShiftStatus } from '@/modules/billing/labels';
import { dateParam, enumParam, pageParam } from '@/modules/billing/searchParams';
import { CloseShiftDialog, ShiftBar } from '@/modules/billing/ShiftControls';
import { Amount, NoAccess, SummaryList, SummaryRow } from '@/modules/billing/ui';

interface ShiftsSearch {
  date?: string;
  status?: ShiftStatus;
  page?: number;
}

export const Route = createFileRoute('/_app/billing/shifts')({
  validateSearch: (s: Record<string, unknown>): ShiftsSearch => ({ date: dateParam(s.date), status: enumParam(s.status, SHIFT_STATUSES), page: pageParam(s.page) }),
  component: ShiftsPage,
});

function Variance({ value }: { value: number | null }) {
  if (value === null) {
    return <Text style={{ color: tokens.colorNeutralForeground3 }}>—</Text>;
  }
  if (value === 0) {
    return <Text style={{ color: tokens.colorPaletteGreenForeground1 }}>Tallied</Text>;
  }
  return (
    <Text weight="semibold" style={{ color: value < 0 ? tokens.colorPaletteRedForeground1 : tokens.colorPaletteMarigoldForeground1 }}>
      {value < 0 ? `Short ${formatInr(-value)}` : `Excess ${formatInr(value)}`}
    </Text>
  );
}

/** Cashier shifts of a day (OPB-07): who opened which counter, expected against declared cash, and the variance. */
function ShiftsPage() {
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const canShifts = usePermission('billing.shifts');
  const isSupervisor = usePermission('billing.refunds.approve');
  const userId = useSession((s) => s.user?.id);
  const [viewing, setViewing] = useState<string | null>(null);
  const [closing, setClosing] = useState<ShiftDto | null>(null);
  useBillingLive();
  const date = search.date ?? todayIso();
  const shifts = useQuery({ ...billingQueries.shifts({ date, status: search.status, page: search.page ?? 1 }), enabled: canShifts });
  const detail = useQuery({ ...billingQueries.shift(viewing ?? ''), enabled: viewing !== null });

  if (!canShifts) {
    return (
      <Page>
        <PageHeader title="Cash shifts" />
        <NoAccess what="cash shifts" />
      </Page>
    );
  }

  const d = detail.data;
  return (
    <Page>
      <PageHeader
        title="Cash shifts"
        subtitle="Each cashier opens a shift at a counter and declares the drawer when closing it"
        icon={<LockClosedRegular />}
        actions={
          <Button icon={<DocumentTableRegular />} onClick={() => void navigate({ to: '/billing/day-end', search: { date: search.date } })}>
            Day-end report
          </Button>
        }
      />
      <ShiftBar />
      <Section flush>
        <div style={{ padding: '0 16px 12px' }}>
          <FilterBar>
            <Field label="Date">
              <Input type="date" value={date} max={todayIso()} onChange={(_, e) => void navigate({ search: (prev) => ({ ...prev, date: e.value || undefined, page: undefined }) })} />
            </Field>
            <Field label="Status">
              <Select value={search.status ?? ''} onChange={(_, e) => void navigate({ search: (prev) => ({ ...prev, status: (e.value || undefined) as ShiftStatus | undefined, page: undefined }) })}>
                <option value="">Open and closed</option>
                <option value="Open">Open</option>
                <option value="Closed">Closed</option>
              </Select>
            </Field>
          </FilterBar>
        </div>
        <DataTable
          ariaLabel="Cash shifts"
          rows={shifts.data?.items}
          loading={shifts.isLoading}
          error={shifts.error}
          onRetry={() => void shifts.refetch()}
          getRowId={(s) => s.id}
          onRowClick={(s) => setViewing(s.id)}
          columns={[
            {
              key: 'cashier',
              header: 'Cashier',
              render: (s) => (
                <span>
                  <Text weight="semibold">{s.userName}</Text>
                  <Caption1 block>{s.counter}</Caption1>
                </span>
              ),
            },
            { key: 'opened', header: 'Opened', render: (s) => formatDateTime(s.openedAt) },
            { key: 'closed', header: 'Closed', priority: 'low', render: (s) => (s.closedAt ? formatDateTime(s.closedAt) : 'Still open') },
            { key: 'expected', header: 'Expected cash', align: 'end', priority: 'low', render: (s) => (s.status === 'Closed' ? <Amount value={s.expectedCash} /> : 'Open the shift to see') },
            { key: 'declared', header: 'Declared', align: 'end', render: (s) => <Amount value={s.declaredCash} /> },
            { key: 'variance', header: 'Short / excess', align: 'end', render: (s) => <Variance value={s.variance} /> },
            { key: 'status', header: 'Status', width: 100, render: (s) => <StatusBadge status={s.status} /> },
          ]}
          emptyTitle="No shifts on this day"
          page={search.page ?? 1}
          pageSize={25}
          totalCount={shifts.data?.totalCount}
          onPageChange={(page) => void navigate({ search: (prev) => ({ ...prev, page: page > 1 ? page : undefined }) })}
          size="small"
        />
      </Section>

      <Dialog open={viewing !== null} onOpenChange={(_, e) => !e.open && setViewing(null)}>
        <DialogSurface style={{ maxWidth: 560 }}>
          <DialogBody>
            <DialogTitle>{d ? `${d.userName} · ${d.counter}` : 'Shift'}</DialogTitle>
            <DialogContent>
              {detail.isLoading ? (
                <LoadingState />
              ) : detail.error || !d ? (
                <ErrorState error={detail.error} onRetry={() => void detail.refetch()} />
              ) : (
                <SummaryList>
                  <SummaryRow label="Opened">{formatDateTime(d.openedAt)}</SummaryRow>
                  <SummaryRow label="Closed">{d.closedAt ? formatDateTime(d.closedAt) : 'Still open'}</SummaryRow>
                  <SummaryRow label="Opening cash">
                    <Amount value={d.openingFloat} />
                  </SummaryRow>
                  {d.totals.map((t) => (
                    <SummaryRow key={t.mode} label={PAYMENT_MODE_LABEL[t.mode]} hint={t.refunded ? `Collected ${formatInr(t.collected)} · refunded ${formatInr(t.refunded)}` : undefined}>
                      <Amount value={t.net} />
                    </SummaryRow>
                  ))}
                  <SummaryRow label="Cash expected in the drawer" strong>
                    <Amount value={d.expectedCash} strong />
                  </SummaryRow>
                  <SummaryRow label="Cash declared">
                    <Amount value={d.declaredCash} />
                  </SummaryRow>
                  <SummaryRow label="Short / excess">
                    <Variance value={d.variance} />
                  </SummaryRow>
                  <SummaryRow label="Receipts">{d.receiptCount}</SummaryRow>
                  {d.notes && <SummaryRow label="Notes">{d.notes}</SummaryRow>}
                </SummaryList>
              )}
            </DialogContent>
            <DialogActions>
              {d && d.status === 'Open' && (d.userId === userId || isSupervisor) && (
                <Button
                  icon={<DoorArrowLeftRegular />}
                  onClick={() => {
                    setClosing(d);
                    setViewing(null);
                  }}
                >
                  Close this shift
                </Button>
              )}
              <Button appearance="primary" onClick={() => setViewing(null)}>
                Done
              </Button>
            </DialogActions>
          </DialogBody>
        </DialogSurface>
      </Dialog>
      {closing && <CloseShiftDialog shift={closing} open onClose={() => setClosing(null)} onClosed={() => setClosing(null)} />}
    </Page>
  );
}
