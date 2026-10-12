import {
  Badge,
  Button,
  Caption1,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  DrawerBody,
  DrawerFooter,
  DrawerHeader,
  DrawerHeaderTitle,
  Field,
  Input,
  MessageBar,
  MessageBarBody,
  OverlayDrawer,
  SearchBox,
  Select,
  Spinner,
  Switch,
  Text,
  Textarea,
  tokens,
} from '@fluentui/react-components';
import { AlertUrgentRegular, DismissRegular, OpenRegular, WrenchRegular } from '@fluentui/react-icons';
import { formatDateTime, formatInr, formatRelative, isApiError, useDebouncedValue } from '@hc360/core';
import {
  ConfirmDialog,
  DataTable,
  ErrorState,
  FilterBar,
  LoadingState,
  Page,
  PageHeader,
  Section,
  useNotify,
} from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useEffect, useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { assetApi, assetKeys, assetQueries, type TicketDetail, type TicketListItem } from '@/modules/assets/api';
import { BreakdownDialog } from '@/modules/assets/BreakdownDialog';
import { TICKET_PRIORITIES, TICKET_STATUSES, type TicketPriority, type TicketStatus } from '@/modules/assets/labels';
import {
  formatDowntime,
  isTicketOpen,
  primaryTicketAction,
  ticketActionLabel,
  ticketActions,
  type TicketAction,
} from '@/modules/assets/ticketFlow';
import { parseNumber } from '@/modules/inventory/format';
import { SUPPLY_PERMISSIONS as S } from '@/modules/inventory/permissions';
import { Detail, DetailGrid, DocStatus } from '@/modules/inventory/StockBits';
import { VendorPicker } from '@/modules/purchase/VendorPicker';

interface TicketSearch {
  status?: TicketStatus;
  priority?: TicketPriority;
  all?: boolean;
  q?: string;
  page?: number;
  id?: string;
}

export const Route = createFileRoute('/_app/assets/tickets')({
  validateSearch: (s: Record<string, unknown>): TicketSearch => ({
    status: TICKET_STATUSES.includes(s.status as TicketStatus) ? (s.status as TicketStatus) : undefined,
    priority: TICKET_PRIORITIES.includes(s.priority as TicketPriority) ? (s.priority as TicketPriority) : undefined,
    all: s.all === true || s.all === 'true' ? true : undefined,
    q: typeof s.q === 'string' && s.q ? s.q : undefined,
    page: typeof s.page === 'number' && s.page > 1 ? s.page : undefined,
    id: typeof s.id === 'string' && s.id ? s.id : undefined,
  }),
  component: TicketsPage,
});

const STATUS_LABEL: Record<TicketStatus, string> = {
  Open: 'Open',
  Assigned: 'Assigned',
  InProgress: 'Being repaired',
  Resolved: 'Resolved',
  Closed: 'Closed',
};

function PriorityBadge({ priority }: { priority: TicketPriority }) {
  return priority === 'Critical' ? (
    <Badge color="danger" icon={<AlertUrgentRegular />}>
      Critical
    </Badge>
  ) : (
    <Badge appearance="tint" color={priority === 'High' ? 'warning' : priority === 'Medium' ? 'brand' : 'informative'}>
      {priority}
    </Badge>
  );
}

/** Equipment breakdown tickets (AST-04): report → assign → repair → resolve → close, with downtime for NABH uptime. */
function TicketsPage() {
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const [term, setTerm] = useState(search.q ?? '');
  const [reporting, setReporting] = useState(false);
  const debounced = useDebouncedValue(term.trim(), 300);

  useEffect(() => {
    if ((debounced || undefined) !== search.q) {
      void navigate({ search: (prev) => ({ ...prev, q: debounced || undefined, page: undefined }), replace: true });
    }
  }, [debounced, search.q, navigate]);

  const tickets = useQuery(
    assetQueries.tickets({
      status: search.status,
      priority: search.priority,
      openOnly: search.status || search.all ? undefined : true,
      search: search.q,
      page: search.page,
    }),
  );
  const set = (patch: Partial<TicketSearch>) =>
    void navigate({ search: (prev) => ({ ...prev, ...patch, page: undefined }) });
  /** Opens or closes the ticket drawer without losing the list page. */
  const show = (id: string | undefined) => void navigate({ search: (prev) => ({ ...prev, id }) });

  return (
    <Page>
      <PageHeader
        title="Breakdown tickets"
        subtitle="Anyone can report equipment that is not working. Biomedical engineering assigns, repairs and closes each ticket; downtime counts towards equipment uptime."
        actions={
          <Button appearance="primary" icon={<WrenchRegular />} onClick={() => setReporting(true)}>
            Report breakdown
          </Button>
        }
      />
      <Section flush>
        <div style={{ padding: '0 16px 12px' }}>
          <FilterBar>
            <Field label="Ticket or asset tag" style={{ flex: '0 1 240px' }}>
              <SearchBox
                value={term}
                onChange={(_, d) => setTerm(d.value)}
                placeholder="Full ticket number or scan the tag"
              />
            </Field>
            <Field label="Status">
              <Select
                value={search.status ?? ''}
                onChange={(_, d) => set({ status: (d.value || undefined) as TicketStatus | undefined })}
              >
                <option value="">{search.all ? 'All' : 'Still open'}</option>
                {TICKET_STATUSES.map((s) => (
                  <option key={s} value={s}>
                    {STATUS_LABEL[s]}
                  </option>
                ))}
              </Select>
            </Field>
            <Field label="Priority">
              <Select
                value={search.priority ?? ''}
                onChange={(_, d) => set({ priority: (d.value || undefined) as TicketPriority | undefined })}
              >
                <option value="">All</option>
                {TICKET_PRIORITIES.map((p) => (
                  <option key={p} value={p}>
                    {p}
                  </option>
                ))}
              </Select>
            </Field>
            <Switch
              checked={search.all === true}
              onChange={(_, d) => set({ all: d.checked || undefined })}
              label="Include resolved and closed"
              disabled={Boolean(search.status)}
            />
          </FilterBar>
        </div>
        <DataTable<TicketListItem>
          ariaLabel="Breakdown tickets"
          rows={tickets.data?.items}
          loading={tickets.isLoading}
          error={tickets.error}
          onRetry={() => void tickets.refetch()}
          getRowId={(t) => t.id}
          onRowClick={(t) => show(t.id)}
          size="small"
          columns={[
            { key: 'priority', header: 'Priority', width: 110, render: (t) => <PriorityBadge priority={t.priority} /> },
            { key: 'no', header: 'Ticket', render: (t) => <Text font="monospace">{t.number}</Text> },
            {
              key: 'asset',
              header: 'Equipment',
              render: (t) => (
                <span>
                  <Text weight="semibold">{t.assetName}</Text>{' '}
                  <Caption1 style={{ color: tokens.colorNeutralForeground3 }}>{t.assetTag}</Caption1>
                </span>
              ),
            },
            { key: 'issue', header: 'Problem', render: (t) => t.issue },
            {
              key: 'reported',
              header: 'Reported',
              priority: 'low',
              render: (t) => (
                <span>
                  {formatRelative(t.reportedAt)}
                  {t.reportedBy && <Caption1 block>{t.reportedBy}</Caption1>}
                </span>
              ),
            },
            { key: 'with', header: 'With', priority: 'low', render: (t) => t.assignedTo ?? 'Not assigned' },
            { key: 'down', header: 'Downtime', priority: 'low', render: (t) => formatDowntime(t.downtimeMinutes) },
            {
              key: 'status',
              header: 'Status',
              render: (t) => <DocStatus status={t.status} label={STATUS_LABEL[t.status]} />,
            },
          ]}
          emptyTitle={search.all || search.status ? 'No tickets match' : 'No open breakdowns'}
          emptyDescription="Report equipment that is not working; biomedical engineering is alerted."
          page={search.page ?? 1}
          pageSize={25}
          totalCount={tickets.data?.totalCount}
          onPageChange={(p) => void navigate({ search: (prev) => ({ ...prev, page: p > 1 ? p : undefined }) })}
        />
      </Section>
      {search.id && <TicketDrawer key={search.id} ticketId={search.id} onClose={() => show(undefined)} />}
      {reporting && <BreakdownDialog onClose={() => setReporting(false)} onReported={show} />}
    </Page>
  );
}

function TicketDrawer({ ticketId, onClose }: { ticketId: string; onClose: () => void }) {
  const navigate = useNavigate();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const canMaintain = usePermission(S.assetsMaintenance);
  const ticket = useQuery(assetQueries.ticket(ticketId));
  const [action, setAction] = useState<TicketAction | null>(null);
  const simple = useMutation({
    mutationFn: (kind: 'start' | 'close' | 'reopen') =>
      kind === 'start'
        ? assetApi.startTicket(ticketId)
        : kind === 'close'
          ? assetApi.closeTicket(ticketId)
          : assetApi.reopenTicket(ticketId),
    onSuccess: (_, kind) => {
      setAction(null);
      notify.success(kind === 'start' ? 'Work started' : kind === 'close' ? 'Ticket closed' : 'Ticket reopened');
      void queryClient.invalidateQueries({ queryKey: assetKeys.all });
    },
    onError: (error) => {
      setAction(null);
      notify.error(error, 'The ticket was not updated');
    },
  });
  const t = ticket.data;
  const actions = t && canMaintain ? ticketActions(t.status) : [];
  const primary = t ? primaryTicketAction(t.status) : null;

  return (
    <OverlayDrawer open position="end" size="medium" onOpenChange={(_, d) => !d.open && onClose()}>
      <DrawerHeader>
        <DrawerHeaderTitle
          action={<Button appearance="subtle" icon={<DismissRegular />} aria-label="Close" onClick={onClose} />}
        >
          {t ? `Ticket ${t.number}` : 'Ticket'}
        </DrawerHeaderTitle>
      </DrawerHeader>
      <DrawerBody>
        {ticket.isLoading && <LoadingState />}
        {ticket.error && <ErrorState error={ticket.error} onRetry={() => void ticket.refetch()} />}
        {t && (
          <div style={{ display: 'flex', flexDirection: 'column', gap: 16 }}>
            <div style={{ display: 'flex', gap: 8, alignItems: 'center', flexWrap: 'wrap' }}>
              <PriorityBadge priority={t.priority} />
              <DocStatus status={t.status} label={STATUS_LABEL[t.status]} />
            </div>
            <div>
              <Text weight="semibold">{t.assetName}</Text> <Caption1>{t.assetTag}</Caption1>
              <Text block style={{ marginTop: 6 }}>
                {t.issue}
              </Text>
            </div>
            <DetailGrid>
              <Detail label="Reported">
                {formatDateTime(t.reportedAt)}
                {t.reportedBy ? ` by ${t.reportedBy}` : ''}
              </Detail>
              <Detail label="Assigned to">
                {t.assignedTo ? `${t.assignedTo}${t.assignedAt ? ` · ${formatRelative(t.assignedAt)}` : ''}` : null}
              </Detail>
              <Detail label="Work started">{t.startedAt ? formatDateTime(t.startedAt) : null}</Detail>
              <Detail label="Resolved">{t.resolvedAt ? formatDateTime(t.resolvedAt) : null}</Detail>
              <Detail label="Closed">{t.closedAt ? formatDateTime(t.closedAt) : null}</Detail>
              <Detail label="Downtime">
                {t.downtimeMinutes !== null
                  ? formatDowntime(t.downtimeMinutes)
                  : isTicketOpen(t.status)
                    ? `Running since ${formatRelative(t.reportedAt)}`
                    : null}
              </Detail>
              {t.resolution && <Detail label="Resolution">{t.resolution}</Detail>}
              {t.rootCause && <Detail label="Root cause">{t.rootCause}</Detail>}
              {t.cost > 0 && <Detail label="Repair cost">{formatInr(t.cost)}</Detail>}
            </DetailGrid>
          </div>
        )}
      </DrawerBody>
      <DrawerFooter>
        {t && (
          <>
            <Button
              icon={<OpenRegular />}
              onClick={() =>
                void navigate({ to: '/assets/$assetId', params: { assetId: t.assetId }, search: { tab: 'tickets' } })
              }
            >
              Equipment
            </Button>
            {actions.map((a) => (
              <Button key={a} appearance={a === primary ? 'primary' : 'secondary'} onClick={() => setAction(a)}>
                {ticketActionLabel[a]}
              </Button>
            ))}
          </>
        )}
      </DrawerFooter>
      {t && action === 'assign' && <AssignDialog ticket={t} onClose={() => setAction(null)} />}
      {t && action === 'resolve' && <ResolveDialog ticket={t} onClose={() => setAction(null)} />}
      <ConfirmDialog
        open={action === 'start' || action === 'close' || action === 'reopen'}
        title={
          action === 'start'
            ? 'Start work on this breakdown?'
            : action === 'close'
              ? 'Close this ticket?'
              : 'Reopen this ticket?'
        }
        confirmLabel={action ? ticketActionLabel[action] : 'OK'}
        busy={simple.isPending}
        onConfirm={() => (action === 'start' || action === 'close' || action === 'reopen') && simple.mutate(action)}
        onCancel={() => setAction(null)}
      >
        {action === 'close'
          ? 'Close it once the ward confirms the equipment works.'
          : action === 'reopen'
            ? 'The fix did not hold: the ticket goes back to being repaired and downtime is worked out again when it is resolved.'
            : 'The ticket shows as being repaired.'}
      </ConfirmDialog>
    </OverlayDrawer>
  );
}

function AssignDialog({ ticket, onClose }: { ticket: TicketDetail; onClose: () => void }) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [assignedTo, setAssignedTo] = useState(ticket.assignedTo ?? '');
  const [vendor, setVendor] = useState<{ id: string; name: string } | null>(null);
  const assign = useMutation({
    mutationFn: () =>
      assetApi.assignTicket(ticket.id, {
        assignedTo: assignedTo.trim(),
        assignedToUserId: null,
        vendorId: vendor?.id ?? ticket.vendorId ?? null,
      }),
    onSuccess: () => {
      notify.success(`Assigned to ${assignedTo.trim()}`);
      void queryClient.invalidateQueries({ queryKey: assetKeys.all });
      onClose();
    },
  });
  return (
    <Dialog open onOpenChange={(_, d) => !d.open && !assign.isPending && onClose()}>
      <DialogSurface style={{ maxWidth: 520 }}>
        <DialogBody>
          <DialogTitle>Assign {ticket.number}</DialogTitle>
          <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
            <Field label="Engineer or service team" required>
              <Input
                value={assignedTo}
                maxLength={120}
                onChange={(_, d) => setAssignedTo(d.value)}
                autoFocus
                placeholder="e.g. Suresh (biomedical), or the vendor's engineer"
              />
            </Field>
            <VendorPicker
              label="Service vendor (if outside)"
              value={vendor}
              onPick={(v) => setVendor({ id: v.id, name: v.name })}
            />
            {assign.error && (
              <MessageBar intent="error">
                <MessageBarBody>{isApiError(assign.error) ? assign.error.userMessage : 'Not assigned.'}</MessageBarBody>
              </MessageBar>
            )}
          </DialogContent>
          <DialogActions>
            <Button onClick={onClose} disabled={assign.isPending}>
              Cancel
            </Button>
            <Button
              appearance="primary"
              disabled={!assignedTo.trim() || assign.isPending}
              icon={assign.isPending ? <Spinner size="tiny" aria-hidden /> : undefined}
              onClick={() => assign.mutate()}
            >
              Assign
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}

function ResolveDialog({ ticket, onClose }: { ticket: TicketDetail; onClose: () => void }) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [resolution, setResolution] = useState('');
  const [rootCause, setRootCause] = useState('');
  const [cost, setCost] = useState('');
  const [downtime, setDowntime] = useState('');
  const costValue = parseNumber(cost) ?? 0;
  const downtimeValue = downtime.trim() === '' ? null : parseNumber(downtime);
  const valid =
    resolution.trim().length > 0 &&
    costValue >= 0 &&
    (downtimeValue === null || (Number.isInteger(downtimeValue) && downtimeValue >= 0));
  const resolve = useMutation({
    mutationFn: () =>
      assetApi.resolveTicket(ticket.id, {
        resolution: resolution.trim(),
        rootCause: rootCause.trim() || null,
        cost: costValue,
        downtimeMinutes: downtimeValue,
      }),
    onSuccess: () => {
      notify.success('Marked resolved', 'Close the ticket once the ward confirms it works.');
      void queryClient.invalidateQueries({ queryKey: assetKeys.all });
      onClose();
    },
  });
  return (
    <Dialog open onOpenChange={(_, d) => !d.open && !resolve.isPending && onClose()}>
      <DialogSurface style={{ maxWidth: 560 }}>
        <DialogBody>
          <DialogTitle>Resolve {ticket.number}</DialogTitle>
          <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
            <Field label="What was done" required>
              <Textarea
                value={resolution}
                rows={3}
                maxLength={2000}
                onChange={(_, d) => setResolution(d.value)}
                autoFocus
              />
            </Field>
            <Field label="Root cause">
              <Input
                value={rootCause}
                maxLength={500}
                onChange={(_, d) => setRootCause(d.value)}
                placeholder="e.g. Worn power cable, user error, power surge"
              />
            </Field>
            <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 12 }}>
              <Field label="Repair cost (₹)">
                <Input type="number" min={0} value={cost} onChange={(_, d) => setCost(d.value)} placeholder="0" />
              </Field>
              <Field
                label="Downtime (minutes)"
                hint={`Blank = since it was reported (${formatRelative(ticket.reportedAt)})`}
              >
                <Input type="number" min={0} value={downtime} onChange={(_, d) => setDowntime(d.value)} />
              </Field>
            </div>
            <Caption1>Enter the downtime only if the equipment stayed usable for part of the time.</Caption1>
            {resolve.error && (
              <MessageBar intent="error">
                <MessageBarBody>{isApiError(resolve.error) ? resolve.error.userMessage : 'Not saved.'}</MessageBarBody>
              </MessageBar>
            )}
          </DialogContent>
          <DialogActions>
            <Button onClick={onClose} disabled={resolve.isPending}>
              Cancel
            </Button>
            <Button
              appearance="primary"
              disabled={!valid || resolve.isPending}
              icon={resolve.isPending ? <Spinner size="tiny" aria-hidden /> : undefined}
              onClick={() => resolve.mutate()}
            >
              Mark resolved
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}
