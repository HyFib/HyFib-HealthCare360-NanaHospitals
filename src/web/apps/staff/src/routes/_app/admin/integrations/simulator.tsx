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
  makeStyles,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  SearchBox,
  Select,
  Tab,
  TabList,
  Text,
  tokens,
} from '@fluentui/react-components';
import { formatDateTime, formatInr, useDebouncedValue } from '@hc360/core';
import { DataTable, FilterBar, Page, PageHeader, Section, StatusBadge } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useEffect, useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { IntegrationsTabs } from '@/modules/admin/IntegrationsTabs';
import {
  INBOX_PAGE_SIZE,
  integrationQueries,
  pageFromUrl,
  SIMULATOR_CHANNEL_LABELS,
  SIMULATOR_CHANNELS,
  SIMULATOR_TRANSACTION_KINDS,
  TRANSACTION_KIND_LABELS,
  transactionStatusDisplay,
  type SimulatorChannel,
  type SimulatorMessageDto,
  type SimulatorTransactionKind,
} from '@/modules/admin/integrationsApi';
import { NoAccess } from '@/modules/admin/NoAccess';

interface SimulatorSearch {
  view?: 'payments';
  channel?: SimulatorChannel;
  kind?: SimulatorTransactionKind;
  q?: string;
  page?: number;
}

export const Route = createFileRoute('/_app/admin/integrations/simulator')({
  validateSearch: (s: Record<string, unknown>): SimulatorSearch => ({
    view: s.view === 'payments' ? 'payments' : undefined,
    channel: SIMULATOR_CHANNELS.includes(s.channel as SimulatorChannel) ? (s.channel as SimulatorChannel) : undefined,
    kind: SIMULATOR_TRANSACTION_KINDS.includes(s.kind as SimulatorTransactionKind)
      ? (s.kind as SimulatorTransactionKind)
      : undefined,
    q: typeof s.q === 'string' && s.q.trim() ? s.q.trim().slice(0, 100) : undefined,
    page: pageFromUrl(s.page),
  }),
  component: SimulatorPage,
});

const useStyles = makeStyles({
  toolbar: { padding: '0 16px 12px', display: 'flex', flexDirection: 'column', gap: '12px' },
  muted: { color: tokens.colorNeutralForeground3 },
  preview: { display: 'block', maxWidth: '420px', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' },
  detail: { display: 'grid', gridTemplateColumns: 'auto 1fr', gap: '6px 16px', alignItems: 'baseline' },
  block: {
    gridColumn: '1 / -1',
    whiteSpace: 'pre-wrap',
    wordBreak: 'break-word',
    backgroundColor: tokens.colorNeutralBackground3,
    padding: '8px 12px',
    borderRadius: tokens.borderRadiusMedium,
    margin: 0,
    maxHeight: '260px',
    overflow: 'auto',
  },
  mono: { fontFamily: tokens.fontFamilyMonospace, wordBreak: 'break-all' },
});

/**
 * The simulator inbox: what the simulator providers "delivered" when an integration's provider is Simulator, so staff
 * can train and test end to end without credentials. Nothing here was really sent, charged or posted.
 */
function SimulatorPage() {
  const styles = useStyles();
  const allowed = usePermission('admin.integrations.manage');
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const payments = search.view === 'payments';

  if (!allowed) {
    return (
      <Page>
        <PageHeader title="Integrations" />
        <NoAccess what="the simulator inbox" />
      </Page>
    );
  }

  return (
    <Page>
      <PageHeader title="Integrations" subtitle="Simulator inbox for training and testing" />
      <IntegrationsTabs selected="simulator" />
      <MessageBar intent="warning" layout="multiline">
        <MessageBarBody>
          <MessageBarTitle>Training and simulation only</MessageBarTitle>
          Nothing on this page was really sent, charged, printed or posted to Tally. When an integration’s provider is
          Simulator, what it would have done is recorded here so staff can practise without real messages or money.
        </MessageBarBody>
      </MessageBar>
      <TabList
        selectedValue={payments ? 'payments' : 'messages'}
        onTabSelect={(_, data) =>
          void navigate({ search: { view: data.value === 'payments' ? 'payments' : undefined } })
        }
        aria-label="Simulator records"
        size="small"
      >
        <Tab value="messages">Messages (SMS, WhatsApp, e-mail, Tally, labels)</Tab>
        <Tab value="payments">Payments, POS and punches</Tab>
      </TabList>
      {payments ? <SimulatedPayments /> : <SimulatedMessages />}
      <Caption1 className={styles.muted}>Recipients are stored masked, like the notification log.</Caption1>
    </Page>
  );
}

function SimulatedMessages() {
  const styles = useStyles();
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const [term, setTerm] = useState(search.q ?? '');
  const debounced = useDebouncedValue(term.trim(), 300);
  const [open, setOpen] = useState<SimulatorMessageDto | null>(null);
  const page = search.page ?? 1;
  const messages = useQuery(integrationQueries.simulatorMessages({ channel: search.channel, search: search.q, page }));

  useEffect(() => {
    if ((debounced || undefined) !== search.q) {
      void navigate({ search: (prev) => ({ ...prev, q: debounced || undefined, page: undefined }), replace: true });
    }
  }, [debounced, search.q, navigate]);

  return (
    <Section flush>
      <div className={styles.toolbar}>
        <FilterBar>
          <Field label="Search" style={{ flex: '1 1 220px' }}>
            <SearchBox
              value={term}
              onChange={(_, data) => setTerm(data.value)}
              placeholder="Text, subject or reference"
            />
          </Field>
          <Field label="Channel">
            <Select
              value={search.channel ?? ''}
              onChange={(_, data) =>
                void navigate({
                  search: (prev) => ({
                    ...prev,
                    channel: SIMULATOR_CHANNELS.includes(data.value as SimulatorChannel)
                      ? (data.value as SimulatorChannel)
                      : undefined,
                    page: undefined,
                  }),
                })
              }
            >
              <option value="">All</option>
              {SIMULATOR_CHANNELS.map((channel) => (
                <option key={channel} value={channel}>
                  {SIMULATOR_CHANNEL_LABELS[channel]}
                </option>
              ))}
            </Select>
          </Field>
        </FilterBar>
      </div>
      <DataTable
        ariaLabel="Simulated messages"
        rows={messages.data?.items}
        loading={messages.isLoading}
        error={messages.error}
        onRetry={() => void messages.refetch()}
        getRowId={(row) => row.id}
        onRowClick={setOpen}
        size="small"
        columns={[
          { key: 'when', header: 'When', width: 180, render: (row) => formatDateTime(row.createdAt) },
          {
            key: 'channel',
            header: 'Channel',
            width: 110,
            render: (row) => SIMULATOR_CHANNEL_LABELS[row.channel] ?? row.channel,
          },
          { key: 'to', header: 'To', width: 160, render: (row) => row.recipient ?? '—' },
          {
            key: 'content',
            header: 'Message',
            render: (row) => (
              <span className={styles.preview} title={row.subject ?? undefined}>
                {row.subject ? `${row.subject}: ` : ''}
                {row.body}
              </span>
            ),
          },
          { key: 'reference', header: 'Reference', priority: 'low', render: (row) => row.reference ?? '—' },
        ]}
        emptyTitle="No simulated messages"
        emptyDescription="Set an integration’s provider to Simulator, then send a test or book an appointment to see what would have been sent."
        page={page}
        pageSize={INBOX_PAGE_SIZE}
        totalCount={messages.data?.totalCount}
        onPageChange={(next) => void navigate({ search: (prev) => ({ ...prev, page: next > 1 ? next : undefined }) })}
      />

      <Dialog open={open !== null} onOpenChange={(_, data) => !data.open && setOpen(null)}>
        <DialogSurface style={{ maxWidth: 720 }}>
          <DialogBody>
            <DialogTitle>
              Simulated {open ? (SIMULATOR_CHANNEL_LABELS[open.channel] ?? open.channel) : ''} message
            </DialogTitle>
            <DialogContent className={styles.detail}>
              {open && (
                <>
                  <Caption1 className={styles.muted}>When</Caption1>
                  <Text>{formatDateTime(open.createdAt)}</Text>
                  <Caption1 className={styles.muted}>To</Caption1>
                  <Text>{open.recipient ?? '—'}</Text>
                  {open.subject && (
                    <>
                      <Caption1 className={styles.muted}>Subject</Caption1>
                      <Text>{open.subject}</Text>
                    </>
                  )}
                  <Caption1 className={styles.muted}>Reference</Caption1>
                  <Text className={styles.mono}>{open.reference ?? '—'}</Text>
                  <Caption1 className={styles.muted}>Message id</Caption1>
                  <Text className={styles.mono}>{open.providerMessageId}</Text>
                  <pre className={styles.block}>{open.body}</pre>
                  {open.details && <pre className={styles.block}>{open.details}</pre>}
                </>
              )}
            </DialogContent>
            <DialogActions>
              <Button onClick={() => setOpen(null)}>Close</Button>
            </DialogActions>
          </DialogBody>
        </DialogSurface>
      </Dialog>
    </Section>
  );
}

function SimulatedPayments() {
  const styles = useStyles();
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const page = search.page ?? 1;
  const transactions = useQuery(integrationQueries.simulatorTransactions({ kind: search.kind, page }));

  return (
    <Section flush>
      <div className={styles.toolbar}>
        <FilterBar>
          <Field label="Kind">
            <Select
              value={search.kind ?? ''}
              onChange={(_, data) =>
                void navigate({
                  search: (prev) => ({
                    ...prev,
                    kind: SIMULATOR_TRANSACTION_KINDS.includes(data.value as SimulatorTransactionKind)
                      ? (data.value as SimulatorTransactionKind)
                      : undefined,
                    page: undefined,
                  }),
                })
              }
            >
              <option value="">All</option>
              {SIMULATOR_TRANSACTION_KINDS.map((kind) => (
                <option key={kind} value={kind}>
                  {TRANSACTION_KIND_LABELS[kind]}
                </option>
              ))}
            </Select>
          </Field>
        </FilterBar>
      </div>
      <DataTable
        ariaLabel="Simulated payments, POS sales and attendance punches"
        rows={transactions.data?.items}
        loading={transactions.isLoading}
        error={transactions.error}
        onRetry={() => void transactions.refetch()}
        getRowId={(row) => row.id}
        size="small"
        columns={[
          { key: 'when', header: 'When', width: 180, render: (row) => formatDateTime(row.createdAt) },
          { key: 'kind', header: 'Kind', width: 130, render: (row) => TRANSACTION_KIND_LABELS[row.kind] ?? row.kind },
          {
            key: 'amount',
            header: 'Amount',
            width: 130,
            align: 'end',
            render: (row) =>
              row.kind === 'TerminalPunch'
                ? '—'
                : row.currency === 'INR'
                  ? formatInr(row.amount)
                  : `${row.amount} ${row.currency}`,
          },
          {
            key: 'status',
            header: 'Status',
            width: 170,
            render: (row) => {
              const status = transactionStatusDisplay(row.status, row.kind);
              return <StatusBadge status={row.status} tone={status.tone} label={status.label} />;
            },
          },
          {
            key: 'reference',
            header: 'Reference',
            render: (row) =>
              row.kind === 'TerminalPunch' ? `Employee number ${row.reference ?? '—'}` : (row.reference ?? '—'),
          },
          {
            key: 'external',
            header: 'Simulator id',
            priority: 'low',
            render: (row) => <Text font="monospace">{row.externalId}</Text>,
          },
          { key: 'terminal', header: 'Terminal', priority: 'low', render: (row) => row.terminalId ?? '—' },
          {
            key: 'refunded',
            header: 'Refunded',
            priority: 'low',
            align: 'end',
            render: (row) => (row.refundedAmount > 0 ? formatInr(row.refundedAmount) : '—'),
          },
        ]}
        emptyTitle="No simulated payments or punches"
        emptyDescription="Set the payment gateway, POS or biometric attendance provider to Simulator to practise payment links, card/UPI sales and attendance punches. No money moves."
        page={page}
        pageSize={INBOX_PAGE_SIZE}
        totalCount={transactions.data?.totalCount}
        onPageChange={(next) => void navigate({ search: (prev) => ({ ...prev, page: next > 1 ? next : undefined }) })}
      />
    </Section>
  );
}
