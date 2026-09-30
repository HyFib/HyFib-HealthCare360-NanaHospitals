import {
  Badge,
  Button,
  Caption1,
  Field,
  makeStyles,
  SearchBox,
  Select,
  Switch,
  Tab,
  TabList,
  Text,
  tokens,
  Tooltip,
} from '@fluentui/react-components';
import { ArrowForwardRegular, ArrowRepeatAllRegular, CartRegular, ShieldKeyholeRegular } from '@fluentui/react-icons';
import { formatTime, humanize, useDebouncedValue, useHotkey } from '@hc360/core';
import { DataTable, FilterBar, Page, PageHeader, Section, StatusBadge, useNotify } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useEffect, useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { workflowTone } from '@/modules/inventory/labels';
import { pharmacyQueries, QUEUE_STATUSES, type QueueEntryStatus, type QueueListItem } from '@/modules/pharmacy/api';
import { usePharmacyLive } from '@/modules/pharmacy/counter';
import { PHARMACY_PERMISSIONS as P } from '@/modules/pharmacy/permissions';

interface QueueSearch {
  ip?: boolean;
  status?: QueueEntryStatus;
  closed?: boolean;
  q?: string;
  page?: number;
}

export const Route = createFileRoute('/_app/pharmacy/queue/')({
  validateSearch: (s: Record<string, unknown>): QueueSearch => ({
    ip: s.ip === true || s.ip === 'true' ? true : undefined,
    status: QUEUE_STATUSES.includes(s.status as QueueEntryStatus) ? (s.status as QueueEntryStatus) : undefined,
    closed: s.closed === true || s.closed === 'true' ? true : undefined,
    q: typeof s.q === 'string' && s.q ? s.q : undefined,
    page: typeof s.page === 'number' && s.page > 1 ? s.page : undefined,
  }),
  component: QueuePage,
});

const useStyles = makeStyles({
  late: { color: tokens.colorPaletteRedForeground1, fontWeight: tokens.fontWeightSemibold },
  waiting: { fontWeight: tokens.fontWeightSemibold },
  muted: { color: tokens.colorNeutralForeground3 },
  flags: { display: 'inline-flex', gap: '4px', flexWrap: 'wrap' },
});

const STATUS_LABEL: Record<QueueEntryStatus, string> = {
  Pending: 'Waiting',
  PartiallyDispensed: 'Partly dispensed',
  Dispensed: 'Dispensed',
  Cancelled: 'Closed or cancelled',
};

/** The e-prescription queue (PH-01): signed prescriptions arrive live, oldest first. F2 opens the next one. */
function QueuePage() {
  const styles = useStyles();
  const notify = useNotify();
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const canSell = usePermission(P.sales);
  const [term, setTerm] = useState(search.q ?? '');
  const debounced = useDebouncedValue(term.trim(), 300);
  usePharmacyLive();

  useEffect(() => {
    if ((debounced || undefined) !== search.q) {
      void navigate({ search: (prev) => ({ ...prev, q: debounced || undefined, page: undefined }), replace: true });
    }
  }, [debounced, search.q, navigate]);

  const inpatient = search.ip === true;
  const page = search.page ?? 1;
  const queue = useQuery(
    pharmacyQueries.queue({ inpatient, status: search.status, includeClosed: search.closed, search: search.q, page }),
  );
  const dashboard = useQuery(pharmacyQueries.dashboard(null));
  const rows = queue.data?.items;
  const next = rows?.find((e) => e.status === 'Pending' || e.status === 'PartiallyDispensed');

  const open = (entry: QueueListItem) =>
    void navigate({
      to: '/pharmacy/queue/$entryId',
      params: { entryId: entry.id },
      search: { ip: entry.isInpatient || undefined },
    });
  const openNext = () => {
    if (next) {
      open(next);
    } else {
      notify.info('No one is waiting', 'New e-prescriptions appear here as soon as doctors sign them.');
    }
  };
  useHotkey('f2', openNext, { allowInInputs: true });

  const set = (patch: Partial<QueueSearch>) =>
    void navigate({ search: (prev) => ({ ...prev, ...patch, page: undefined }) });

  return (
    <Page>
      <PageHeader
        title="Dispensing queue"
        subtitle="E-prescriptions signed by doctors, oldest first. The list updates by itself; press F2 to open the next one."
        actions={
          <>
            {canSell && (
              <Button icon={<CartRegular />} onClick={() => void navigate({ to: '/pharmacy/sale' })}>
                Counter sale
              </Button>
            )}
            <Button
              appearance="primary"
              icon={<ArrowForwardRegular />}
              onClick={openNext}
              disabled={!next}
              aria-keyshortcuts="F2"
            >
              Open next (F2)
            </Button>
          </>
        }
      />
      <Section flush>
        <div style={{ padding: '0 8px' }}>
          <TabList
            selectedValue={inpatient ? 'ip' : 'op'}
            onTabSelect={(_, d) => set({ ip: d.value === 'ip' ? true : undefined })}
          >
            <Tab value="op">Out-patients{dashboard.data ? ` (${dashboard.data.pendingOutpatient})` : ''}</Tab>
            <Tab value="ip">In-patients{dashboard.data ? ` (${dashboard.data.pendingInpatient})` : ''}</Tab>
          </TabList>
        </div>
        <div style={{ padding: '8px 16px 12px' }}>
          <FilterBar>
            <Field label="Search" style={{ flex: '1 1 280px' }}>
              <SearchBox
                value={term}
                onChange={(_, d) => setTerm(d.value)}
                placeholder="Name, UHID, mobile or prescription number"
                aria-label="Search the queue"
              />
            </Field>
            <Field label="Status">
              <Select
                value={search.status ?? ''}
                onChange={(_, d) => set({ status: (d.value || undefined) as QueueEntryStatus | undefined })}
              >
                <option value="">Still to dispense</option>
                {QUEUE_STATUSES.map((s) => (
                  <option key={s} value={s}>
                    {STATUS_LABEL[s]}
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
        <DataTable<QueueListItem>
          ariaLabel={inpatient ? 'In-patient prescriptions' : 'Out-patient prescriptions'}
          rows={rows}
          loading={queue.isLoading}
          error={queue.error}
          onRetry={() => void queue.refetch()}
          getRowId={(e) => e.id}
          onRowClick={open}
          size="small"
          columns={[
            {
              key: 'wait',
              header: 'Waiting',
              width: 90,
              render: (e) =>
                e.status === 'Pending' || e.status === 'PartiallyDispensed' ? (
                  <span className={e.waitingMinutes >= 20 ? styles.late : styles.waiting}>{e.waitingMinutes} min</span>
                ) : (
                  <span className={styles.muted}>—</span>
                ),
            },
            {
              key: 'patient',
              header: 'Patient',
              render: (e) => (
                <span>
                  <Text weight="semibold">{e.patientName}</Text>{' '}
                  <Caption1 className={styles.muted}>
                    {e.age ?? '—'} / {e.gender.charAt(0)} · {e.uhid}
                  </Caption1>
                </span>
              ),
            },
            {
              key: 'rx',
              header: 'Prescription',
              render: (e) => (
                <span>
                  <Text font="monospace">{e.prescriptionNumber}</Text>
                  <Caption1 block className={styles.muted}>
                    Signed {formatTime(e.signedAt)}
                  </Caption1>
                </span>
              ),
            },
            { key: 'doctor', header: 'Prescriber', priority: 'low', render: (e) => e.prescriberName ?? '—' },
            {
              key: 'items',
              header: 'Items',
              width: 90,
              render: (e) =>
                e.openLines > 0 && e.openLines !== e.lineCount
                  ? `${e.openLines} of ${e.lineCount} left`
                  : `${e.lineCount}`,
            },
            {
              key: 'flags',
              header: 'Notes',
              priority: 'low',
              render: (e) => (
                <span className={styles.flags}>
                  {e.hasControlledDrugs && (
                    <Tooltip
                      content="Contains Schedule H1/X or narcotic medicines: confirm and record in the register"
                      relationship="description"
                    >
                      <Badge size="small" color="danger" icon={<ShieldKeyholeRegular />}>
                        Controlled
                      </Badge>
                    </Tooltip>
                  )}
                  {e.isRefill && (
                    <Badge size="small" appearance="tint" color="brand" icon={<ArrowRepeatAllRegular />}>
                      Refill
                    </Badge>
                  )}
                </span>
              ),
            },
            {
              key: 'status',
              header: 'Status',
              width: 150,
              render: (e) => (
                <StatusBadge
                  status={e.status}
                  tone={workflowTone(e.status)}
                  label={e.status === 'Pending' ? 'Waiting' : humanize(e.status)}
                />
              ),
            },
          ]}
          emptyTitle={
            search.q
              ? 'No prescription matches'
              : inpatient
                ? 'No in-patient prescriptions waiting'
                : 'No one is waiting'
          }
          emptyDescription={
            search.q
              ? 'Check the spelling, or search by UHID or prescription number.'
              : 'E-prescriptions appear here the moment a doctor signs them.'
          }
          page={page}
          pageSize={50}
          totalCount={queue.data?.totalCount}
          onPageChange={(p) => void navigate({ search: (prev) => ({ ...prev, page: p > 1 ? p : undefined }) })}
        />
      </Section>
    </Page>
  );
}
