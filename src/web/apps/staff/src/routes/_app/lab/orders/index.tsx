import { Button, Caption1, Field, makeStyles, SearchBox, Select, Text, tokens } from '@fluentui/react-components';
import { AddRegular } from '@fluentui/react-icons';
import { formatDate, formatTime, todayIso, useDebouncedValue } from '@hc360/core';
import { DataTable, FilterBar, Page, PageHeader, Section, StatusBadge } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useEffect, useState } from 'react';
import { Can } from '@/lib/permissions';
import { labQueries, ORDER_STATUSES, type LabOrderFilters, type LabOrderListItem, type LabOrderStatus } from '@/modules/lab/api';
import { PatientClassBadge, PriorityBadge, ReportStatusBadge } from '@/modules/lab/badges';
import { DateRangeFields } from '@/modules/lab/common/DateRangeFields';
import { searchDate, searchFlag, searchOneOf, searchPage, searchText } from '@/modules/lab/common/search';
import { ORDER_PRIORITIES, PATIENT_CLASSES, type OrderPriority, type PatientClass } from '@/modules/lab/common/types';
import { orderStatusLabel, patientClassLabel, priorityLabel, sourceLabel } from '@/modules/lab/labels';
import { useLabLive } from '@/modules/lab/useLabLive';

const SORT_KEYS = ['orderedAt', 'accessionNumber', 'priority', 'status'] as const;

interface OrdersSearch {
  search?: string;
  from?: string;
  to?: string;
  status?: LabOrderStatus;
  priority?: OrderPriority;
  patientClass?: PatientClass;
  page?: number;
  sort?: (typeof SORT_KEYS)[number];
  asc?: true;
}

export const Route = createFileRoute('/_app/lab/orders/')({
  validateSearch: (s: Record<string, unknown>): OrdersSearch => ({
    search: searchText(s.search, 60),
    from: searchDate(s.from),
    to: searchDate(s.to),
    status: searchOneOf(s.status, ORDER_STATUSES),
    priority: searchOneOf(s.priority, ORDER_PRIORITIES),
    patientClass: searchOneOf(s.patientClass, PATIENT_CLASSES),
    page: searchPage(s.page),
    sort: searchOneOf(s.sort, SORT_KEYS),
    asc: searchFlag(s.asc),
  }),
  component: LabOrdersPage,
});

const useStyles = makeStyles({
  muted: { color: tokens.colorNeutralForeground3 },
  mono: { fontFamily: tokens.fontFamilyMonospace },
  cell: { display: 'flex', flexDirection: 'column', gap: '2px', minWidth: 0 },
});

/** Lab orders by day: doctors' orders and counter registrations, with progress and report status (SRS LIS-02). */
function LabOrdersPage() {
  const styles = useStyles();
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const [term, setTerm] = useState(search.search ?? '');
  const debounced = useDebouncedValue(term.trim(), 300);

  useEffect(() => {
    if ((debounced || undefined) !== search.search) {
      void navigate({ search: (prev) => ({ ...prev, search: debounced || undefined, page: undefined }), replace: true });
    }
  }, [debounced, search.search, navigate]);

  useLabLive();
  const filters: LabOrderFilters = {
    search: search.search,
    from: search.from,
    to: search.to,
    status: search.status,
    priority: search.priority,
    patientClass: search.patientClass,
    page: search.page,
    sort: search.sort,
    desc: search.sort ? !search.asc : undefined,
  };
  const orders = useQuery(labQueries.orders(filters));
  const set = (patch: Partial<OrdersSearch>) => void navigate({ search: (prev) => ({ ...prev, ...patch, page: undefined }) });
  const today = todayIso();
  const range = search.from && search.from !== today ? `${formatDate(search.from)}${search.to && search.to !== search.from ? ` to ${formatDate(search.to)}` : ''}` : 'Today';

  return (
    <Page>
      <PageHeader
        title="Lab orders"
        subtitle={`${range}: doctors' orders and walk-in registrations`}
        actions={
          <Can permission="lab.orders.create">
            <Button appearance="primary" icon={<AddRegular />} onClick={() => void navigate({ to: '/lab/orders/new' })}>
              New lab order
            </Button>
          </Can>
        }
      />
      <Section flush>
        <div style={{ padding: '0 16px 12px' }}>
          <FilterBar>
            <Field label="Search" style={{ flex: '1 1 260px' }}>
              <SearchBox value={term} onChange={(_, data) => setTerm(data.value)} placeholder="UHID, name, mobile or accession number" autoFocus />
            </Field>
            <DateRangeFields value={{ from: search.from, to: search.to }} onChange={(range) => set(range)} />
            <Field label="Status">
              <Select value={search.status ?? ''} onChange={(_, data) => set({ status: (data.value || undefined) as LabOrderStatus | undefined })}>
                <option value="">All</option>
                {ORDER_STATUSES.map((value) => (
                  <option key={value} value={value}>
                    {orderStatusLabel[value]}
                  </option>
                ))}
              </Select>
            </Field>
            <Field label="Priority">
              <Select value={search.priority ?? ''} onChange={(_, data) => set({ priority: (data.value || undefined) as OrderPriority | undefined })}>
                <option value="">All</option>
                {ORDER_PRIORITIES.map((value) => (
                  <option key={value} value={value}>
                    {priorityLabel[value]}
                  </option>
                ))}
              </Select>
            </Field>
            <Field label="Patient type">
              <Select value={search.patientClass ?? ''} onChange={(_, data) => set({ patientClass: (data.value || undefined) as PatientClass | undefined })}>
                <option value="">All</option>
                {PATIENT_CLASSES.map((value) => (
                  <option key={value} value={value}>
                    {patientClassLabel[value]}
                  </option>
                ))}
              </Select>
            </Field>
          </FilterBar>
        </div>
        <DataTable
          ariaLabel="Lab orders"
          rows={orders.data?.items}
          loading={orders.isLoading}
          error={orders.error}
          onRetry={() => void orders.refetch()}
          getRowId={(order) => order.id}
          onRowClick={(order) => void navigate({ to: '/lab/orders/$orderId', params: { orderId: order.id } })}
          sort={{ key: search.sort ?? 'orderedAt', desc: search.sort ? !search.asc : true }}
          onSortChange={(sort) =>
            set({ sort: sort.key as OrdersSearch['sort'], asc: sort.desc ? undefined : true })
          }
          columns={[
            {
              key: 'orderedAt',
              header: 'Ordered',
              width: 110,
              sortable: true,
              render: (order: LabOrderListItem) => (
                <span className={styles.cell}>
                  <Text>{formatTime(order.orderedAt)}</Text>
                  <Caption1 className={styles.muted}>{formatDate(order.orderedAt)}</Caption1>
                </span>
              ),
            },
            {
              key: 'accessionNumber',
              header: 'Accession',
              sortable: true,
              priority: 'low',
              render: (order: LabOrderListItem) => (
                <span className={styles.cell}>
                  <Text className={styles.mono}>{order.accessionNumber}</Text>
                  <Caption1 className={styles.muted}>{sourceLabel[order.source]}</Caption1>
                </span>
              ),
            },
            {
              key: 'patient',
              header: 'Patient',
              render: (order: LabOrderListItem) => (
                <span className={styles.cell}>
                  <Text weight="semibold">{order.patientName}</Text>
                  <Caption1 className={styles.muted}>
                    {order.age ?? '—'} / {order.gender.charAt(0)} · {order.uhid}
                  </Caption1>
                </span>
              ),
            },
            {
              key: 'tests',
              header: 'Tests',
              render: (order: LabOrderListItem) => (
                <span className={styles.cell}>
                  <Text>{order.tests.join(', ')}</Text>
                  <Caption1 className={styles.muted}>
                    {order.releasedCount} of {order.testCount} released
                  </Caption1>
                </span>
              ),
            },
            {
              key: 'priority',
              header: 'Priority',
              width: 90,
              sortable: true,
              render: (order: LabOrderListItem) => <PriorityBadge priority={order.priority} showRoutine />,
            },
            { key: 'class', header: 'Type', width: 90, priority: 'low', render: (order: LabOrderListItem) => <PatientClassBadge patientClass={order.patientClass} /> },
            {
              key: 'status',
              header: 'Status',
              width: 150,
              sortable: true,
              render: (order: LabOrderListItem) =>
                order.reportStatus ? (
                  <ReportStatusBadge status={order.reportStatus} partial={order.releasedCount > 0 && order.releasedCount < order.testCount} />
                ) : (
                  <StatusBadge status={order.status} label={orderStatusLabel[order.status]} />
                ),
            },
          ]}
          emptyTitle={search.search ? 'No order matches your search' : 'No lab orders for these dates'}
          emptyDescription={search.search ? 'Check the UHID or accession number, or change the dates.' : 'Doctors’ orders appear here automatically. Register walk-in patients with New lab order.'}
          page={search.page ?? 1}
          pageSize={25}
          totalCount={orders.data?.totalCount}
          onPageChange={(page) => void navigate({ search: (prev) => ({ ...prev, page: page > 1 ? page : undefined }) })}
          size="small"
        />
      </Section>
    </Page>
  );
}
