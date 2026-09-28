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
  Field,
  Input,
  MessageBar,
  MessageBarBody,
  Radio,
  RadioGroup,
  SearchBox,
  Select,
  Spinner,
  Switch,
  Text,
  Textarea,
  tokens,
} from '@fluentui/react-components';
import { AddRegular, AlertUrgentRegular, DeleteRegular } from '@fluentui/react-icons';
import { formatRelative, humanize, isApiError, useDebouncedValue } from '@hc360/core';
import { DataTable, EmptyState, FilterBar, Page, PageHeader, Section, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useEffect, useState } from 'react';
import { toOptions, useDepartments } from '@/lib/lookups';
import { usePermission } from '@/lib/permissions';
import { inventoryApi, inventoryKeys, inventoryQueries, type IndentListItem, type ItemLookup } from '@/modules/inventory/api';
import { formatQty, parseNumber } from '@/modules/inventory/format';
import { ItemPicker } from '@/modules/inventory/ItemPicker';
import { INDENT_STATUSES, type IndentPriority, type IndentStatus } from '@/modules/inventory/labels';
import { LineTable } from '@/modules/inventory/LineTable';
import { SUPPLY_PERMISSIONS as S } from '@/modules/inventory/permissions';
import { StorePicker } from '@/modules/inventory/StorePicker';
import { DocStatus, ItemFlags } from '@/modules/inventory/StockBits';
import { useWorkingStore } from '@/modules/inventory/stores';

interface IndentSearch {
  status?: IndentStatus;
  all?: boolean;
  fromStoreId?: string;
  toStoreId?: string;
  q?: string;
  page?: number;
  raise?: boolean;
}

export const Route = createFileRoute('/_app/inventory/indents/')({
  validateSearch: (s: Record<string, unknown>): IndentSearch => ({
    status: INDENT_STATUSES.includes(s.status as IndentStatus) ? (s.status as IndentStatus) : undefined,
    all: s.all === true || s.all === 'true' ? true : undefined,
    fromStoreId: typeof s.fromStoreId === 'string' && s.fromStoreId ? s.fromStoreId : undefined,
    toStoreId: typeof s.toStoreId === 'string' && s.toStoreId ? s.toStoreId : undefined,
    q: typeof s.q === 'string' && s.q ? s.q : undefined,
    page: typeof s.page === 'number' && s.page > 1 ? s.page : undefined,
    raise: s.raise === true || s.raise === 'true' ? true : undefined,
  }),
  component: IndentsPage,
});

/** Internal indents (INV-05): sub-stores and departments ask the central store; urgent first, then oldest. */
function IndentsPage() {
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const canRaise = usePermission(S.indentsRaise);
  const working = useWorkingStore('inventory');
  const [term, setTerm] = useState(search.q ?? '');
  const debounced = useDebouncedValue(term.trim(), 300);

  useEffect(() => {
    if ((debounced || undefined) !== search.q) {
      void navigate({ search: (prev) => ({ ...prev, q: debounced || undefined, page: undefined }), replace: true });
    }
  }, [debounced, search.q, navigate]);

  const indents = useQuery(
    inventoryQueries.indents({
      status: search.status,
      openOnly: search.status || search.all ? undefined : true,
      fromStoreId: search.fromStoreId,
      toStoreId: search.toStoreId,
      search: search.q,
      page: search.page,
    }),
  );
  const set = (patch: Partial<IndentSearch>) => void navigate({ search: (prev) => ({ ...prev, ...patch, page: undefined }) });

  return (
    <Page>
      <PageHeader
        title="Indents"
        subtitle="Requests from sub-stores and departments to an issuing store. Urgent requests come first."
        actions={
          canRaise && (
            <Button appearance="primary" icon={<AddRegular />} onClick={() => set({ raise: true })}>
              Raise indent
            </Button>
          )
        }
      />
      <Section flush>
        <div style={{ padding: '0 16px 12px' }}>
          <FilterBar>
            <Field label="Indent number" style={{ flex: '0 1 220px' }}>
              <SearchBox value={term} onChange={(_, d) => setTerm(d.value)} placeholder="Exact number" />
            </Field>
            <StorePicker stores={working.stores} value={search.fromStoreId ?? null} onChange={(id) => set({ fromStoreId: id ?? undefined })} allowAll label="Issuing store" />
            <StorePicker stores={working.stores} value={search.toStoreId ?? null} onChange={(id) => set({ toStoreId: id ?? undefined })} allowAll label="Requesting store" />
            <Field label="Status">
              <Select value={search.status ?? ''} onChange={(_, d) => set({ status: (d.value || undefined) as IndentStatus | undefined })}>
                <option value="">{search.all ? 'All' : 'Open (pending, approved, part issued)'}</option>
                {INDENT_STATUSES.map((s) => (
                  <option key={s} value={s}>
                    {humanize(s)}
                  </option>
                ))}
              </Select>
            </Field>
            <Switch checked={search.all === true} onChange={(_, d) => set({ all: d.checked || undefined })} label="Include closed" disabled={Boolean(search.status)} />
          </FilterBar>
        </div>
        <DataTable<IndentListItem>
          ariaLabel="Indents"
          rows={indents.data?.items}
          loading={indents.isLoading}
          error={indents.error}
          onRetry={() => void indents.refetch()}
          getRowId={(i) => i.id}
          onRowClick={(i) => void navigate({ to: '/inventory/indents/$indentId', params: { indentId: i.id } })}
          size="small"
          columns={[
            {
              key: 'priority',
              header: 'Priority',
              width: 90,
              render: (i) =>
                i.priority === 'Urgent' ? (
                  <Badge color="danger" icon={<AlertUrgentRegular />}>
                    Urgent
                  </Badge>
                ) : (
                  <Caption1>Routine</Caption1>
                ),
            },
            { key: 'number', header: 'Indent', render: (i) => <Text font="monospace">{i.number}</Text> },
            { key: 'from', header: 'Issuing store', render: (i) => i.fromStoreName },
            { key: 'to', header: 'For', render: (i) => i.toStoreName ?? i.departmentName ?? '—' },
            { key: 'lines', header: 'Items', align: 'end', width: 70, render: (i) => i.lineCount },
            {
              key: 'when',
              header: 'Requested',
              priority: 'low',
              render: (i) => (
                <span>
                  {formatRelative(i.requestedAt)}
                  {i.requestedBy && <Caption1 block>{i.requestedBy}</Caption1>}
                </span>
              ),
            },
            { key: 'status', header: 'Status', render: (i) => <DocStatus status={i.status} /> },
          ]}
          emptyTitle={search.q ? 'No indent with that number' : 'No open indents'}
          emptyDescription={canRaise ? 'Raise an indent to ask the central store for items.' : undefined}
          page={search.page ?? 1}
          pageSize={25}
          totalCount={indents.data?.totalCount}
          onPageChange={(p) => void navigate({ search: (prev) => ({ ...prev, page: p > 1 ? p : undefined }) })}
        />
      </Section>
      {canRaise && search.raise && (
        <RaiseIndentDialog
          onClose={() => set({ raise: undefined })}
          onRaised={(id) => void navigate({ to: '/inventory/indents/$indentId', params: { indentId: id } })}
        />
      )}
    </Page>
  );
}

interface IndentLineDraft {
  item: ItemLookup;
  quantity: string;
  remarks: string;
}

function RaiseIndentDialog({ onClose, onRaised }: { onClose: () => void; onRaised: (id: string) => void }) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const working = useWorkingStore('inventory');
  const departments = useDepartments();
  const central = working.stores.find((s) => s.type === 'Central') ?? working.stores[0] ?? null;
  const [fromStoreId, setFromStoreId] = useState<string | null>(null);
  const [target, setTarget] = useState<'store' | 'department'>('store');
  const [toStoreId, setToStoreId] = useState<string | null>(null);
  const [departmentId, setDepartmentId] = useState('');
  const [priority, setPriority] = useState<IndentPriority>('Routine');
  const [remarks, setRemarks] = useState('');
  const [lines, setLines] = useState<IndentLineDraft[]>([]);
  const issuingStoreId = fromStoreId ?? central?.id ?? null;

  const add = (item: ItemLookup) => {
    if (lines.some((l) => l.item.id === item.id)) {
      notify.info(`${item.name} is already on the indent`, 'Change its quantity instead.');
      return;
    }
    setLines([...lines, { item, quantity: '', remarks: '' }]);
  };
  const update = (itemId: string, patch: Partial<IndentLineDraft>) => setLines(lines.map((l) => (l.item.id === itemId ? { ...l, ...patch } : l)));
  const missingQuantity = lines.some((l) => !((parseNumber(l.quantity) ?? 0) > 0));
  const targetChosen = target === 'store' ? Boolean(toStoreId) && toStoreId !== issuingStoreId : Boolean(departmentId);
  const valid = Boolean(issuingStoreId) && targetChosen && lines.length > 0 && !missingQuantity;

  const raise = useMutation({
    mutationFn: () =>
      inventoryApi.createIndent({
        fromStoreId: issuingStoreId!,
        toStoreId: target === 'store' ? toStoreId : null,
        departmentId: target === 'department' ? departmentId || null : null,
        priority,
        remarks: remarks.trim() || null,
        lines: lines.map((l) => ({ itemId: l.item.id, quantity: parseNumber(l.quantity) ?? 0, remarks: l.remarks.trim() || null })),
      }),
    onSuccess: (result) => {
      notify.success(`Indent ${result.number} raised`);
      void queryClient.invalidateQueries({ queryKey: inventoryKeys.indents });
      onRaised(result.id);
    },
  });

  return (
    <Dialog open onOpenChange={(_, d) => !d.open && !raise.isPending && onClose()}>
      <DialogSurface style={{ maxWidth: 920, width: '96vw' }}>
        <DialogBody>
          <DialogTitle>Raise indent</DialogTitle>
          <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
            <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(220px, 1fr))', gap: 12 }}>
              <StorePicker stores={working.stores} value={issuingStoreId} onChange={setFromStoreId} label="Ask from (issuing store)" required loading={working.loading} />
              <Field label="For">
                <RadioGroup layout="horizontal" value={target} onChange={(_, d) => setTarget(d.value as typeof target)}>
                  <Radio value="store" label="A sub-store" />
                  <Radio value="department" label="A department (consumed)" />
                </RadioGroup>
              </Field>
              {target === 'store' ? (
                <StorePicker
                  stores={working.stores}
                  value={toStoreId}
                  onChange={setToStoreId}
                  excludeId={issuingStoreId}
                  label="Receiving store"
                  required
                  hint="Stock moves into this store when issued"
                />
              ) : (
                <Field label="Department" required hint="Issued stock is booked as consumed by the department">
                  <Select value={departmentId} onChange={(_, d) => setDepartmentId(d.value)}>
                    <option value="">Choose department</option>
                    {toOptions(departments.data).map((o) => (
                      <option key={o.value} value={o.value}>
                        {o.label}
                      </option>
                    ))}
                  </Select>
                </Field>
              )}
              <Field label="Priority">
                <RadioGroup layout="horizontal" value={priority} onChange={(_, d) => setPriority(d.value as IndentPriority)}>
                  <Radio value="Routine" label="Routine" />
                  <Radio value="Urgent" label="Urgent" />
                </RadioGroup>
              </Field>
            </div>
            <ItemPicker storeId={issuingStoreId} onPick={add} label="Add item" hint="Stock shown is what the issuing store has now" />
            <LineTable<IndentLineDraft>
              ariaLabel="Indent lines"
              rows={lines}
              getRowId={(l) => l.item.id}
              empty={<EmptyState title="No items yet" description="Search above to add what is needed." />}
              columns={[
                {
                  key: 'item',
                  header: 'Item',
                  render: (l) => (
                    <span>
                      <Text weight="semibold">{l.item.name}</Text> <ItemFlags schedule={l.item.schedule} isControlled={l.item.isControlled} isColdChain={l.item.isColdChain} />
                      <Caption1 block style={{ color: tokens.colorNeutralForeground3 }}>
                        {l.item.available !== null ? `${formatQty(l.item.available, l.item.baseUnit)} in the issuing store` : l.item.code}
                      </Caption1>
                    </span>
                  ),
                },
                {
                  key: 'qty',
                  header: 'Quantity',
                  width: 150,
                  render: (l) => (
                    <Input
                      type="number"
                      min={0}
                      aria-label={`Quantity of ${l.item.name}`}
                      value={l.quantity}
                      contentAfter={<Caption1>{l.item.baseUnit}</Caption1>}
                      onChange={(_, d) => update(l.item.id, { quantity: d.value })}
                    />
                  ),
                },
                {
                  key: 'remarks',
                  header: 'Note',
                  priority: 'low',
                  render: (l) => <Input value={l.remarks} maxLength={300} aria-label={`Note for ${l.item.name}`} onChange={(_, d) => update(l.item.id, { remarks: d.value })} />,
                },
                {
                  key: 'remove',
                  header: '',
                  width: 44,
                  render: (l) => <Button appearance="subtle" icon={<DeleteRegular />} aria-label={`Remove ${l.item.name}`} onClick={() => setLines(lines.filter((x) => x.item.id !== l.item.id))} />,
                },
              ]}
            />
            <Field label="Remarks">
              <Textarea value={remarks} rows={2} maxLength={1000} onChange={(_, d) => setRemarks(d.value)} />
            </Field>
            {target === 'store' && toStoreId && toStoreId === issuingStoreId && (
              <MessageBar intent="warning">
                <MessageBarBody>A store cannot indent from itself.</MessageBarBody>
              </MessageBar>
            )}
            {raise.error && (
              <MessageBar intent="error">
                <MessageBarBody>{isApiError(raise.error) ? raise.error.userMessage : 'Could not raise the indent.'}</MessageBarBody>
              </MessageBar>
            )}
          </DialogContent>
          <DialogActions>
            <Button onClick={onClose} disabled={raise.isPending}>
              Cancel
            </Button>
            <Button appearance="primary" disabled={!valid || raise.isPending} icon={raise.isPending ? <Spinner size="tiny" /> : undefined} onClick={() => raise.mutate()}>
              Raise indent
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}

