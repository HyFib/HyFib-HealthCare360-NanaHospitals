import {
  Badge,
  Button,
  Caption1,
  Checkbox,
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
  Select,
  Spinner,
  Switch,
  Tab,
  TabList,
  Text,
} from '@fluentui/react-components';
import { AddRegular, EditRegular } from '@fluentui/react-icons';
import { formatDateTime, todayIso } from '@hc360/core';
import { DataTable, FilterBar, Page, PageHeader, Section, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { RequirePermission } from '@/modules/hr/shared/NoAccess';
import { errorMessage } from '@/modules/lab/common/errors';
import {
  CONDEMN_LABELS,
  countedLines,
  disinfects,
  KIND_LABELS,
  LINEN_PERMISSIONS,
  LINEN_READERS,
  linenApi,
  linenKeys,
  linenQueries,
  type CondemnReason,
  type LinenItem,
  type LinenItemStock,
  type LinenMovement,
  type LinenMovementKind,
  type WashProcess,
} from '@/modules/linen/api';

type LinenTab = 'stock' | 'movements' | 'items';
const TABS: LinenTab[] = ['stock', 'movements', 'items'];

export const Route = createFileRoute('/_app/linen/')({
  validateSearch: (s: Record<string, unknown>): { tab?: LinenTab } => ({
    tab: TABS.includes(s.tab as LinenTab) ? (s.tab as LinenTab) : undefined,
  }),
  component: () => (
    <RequirePermission permission={LINEN_READERS} what="linen and laundry">
      <LinenPage />
    </RequirePermission>
  ),
});

const WARD_KINDS: LinenMovementKind[] = ['SoiledReturn', 'Lost'];
const STORE_KINDS: LinenMovementKind[] = ['Issued', 'Received', 'Washed', 'Condemned'];

/** Linen and laundry: what is where, the wards against their par levels, and each movement counted in pieces. */
function LinenPage() {
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const tab = search.tab ?? 'stock';

  return (
    <Page>
      <PageHeader
        title="Linen and laundry"
        subtitle="Count linen from the store to the wards, back to the laundry as soiled, and to the store clean. Infected linen is bagged apart and washed only by thermal disinfection."
      />
      <TabList selectedValue={tab} onTabSelect={(_, d) => void navigate({ search: { tab: d.value as LinenTab } })}>
        <Tab value="stock">Stock</Tab>
        <Tab value="movements">Movements</Tab>
        <Tab value="items">Linen list</Tab>
      </TabList>
      {tab === 'stock' && <StockTab />}
      {tab === 'movements' && <MovementsTab />}
      {tab === 'items' && <ItemsTab />}
    </Page>
  );
}

function StockTab() {
  const canManage = usePermission(LINEN_PERMISSIONS.manage);
  const stock = useQuery(linenQueries.stock());
  const [recording, setRecording] = useState<LinenMovementKind | null>(null);
  const [settingPar, setSettingPar] = useState(false);
  const kinds = canManage ? [...STORE_KINDS, ...WARD_KINDS] : WARD_KINDS;

  return (
    <Section
      flush
      actions={
        <span style={{ display: 'flex', gap: 8, flexWrap: 'wrap' }}>
          {kinds.map((k) => (
            <Button key={k} icon={<AddRegular />} onClick={() => setRecording(k)}>
              {KIND_LABELS[k]}
            </Button>
          ))}
          {canManage && <Button onClick={() => setSettingPar(true)}>Ward par levels</Button>}
        </span>
      }
    >
      <DataTable<LinenItemStock>
        ariaLabel="Linen stock"
        rows={stock.data?.items}
        loading={stock.isLoading}
        error={stock.error}
        onRetry={() => void stock.refetch()}
        getRowId={(i) => i.linenItemId}
        size="small"
        columns={[
          { key: 'name', header: 'Item', render: (i) => <Text weight="semibold">{i.name}</Text> },
          { key: 'store', header: 'Store (clean)', width: 120, render: (i) => i.store },
          { key: 'wards', header: 'On wards', width: 110, render: (i) => i.wards },
          { key: 'soiled', header: 'At the laundry', width: 130, render: (i) => i.soiled },
          {
            key: 'infected',
            header: 'Infected',
            width: 100,
            render: (i) =>
              i.infected > 0 ? (
                <Badge appearance="tint" color="danger">
                  {i.infected}
                </Badge>
              ) : (
                0
              ),
          },
          { key: 'total', header: 'In circulation', width: 130, render: (i) => i.inCirculation },
        ]}
        emptyTitle="No linen yet"
        emptyDescription="Add the linen list, then receive new linen into the store."
      />
      {stock.data?.wards.map((w) => (
        <div key={w.wardId} style={{ padding: '12px 16px 0' }}>
          <Text weight="semibold">{w.wardName}</Text>
          <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap', marginTop: 4 }}>
            {w.lines.map((l) => (
              <Badge key={l.linenItemId} appearance="tint" color={l.short > 0 ? 'warning' : 'informative'}>
                {l.itemName}: {l.held}
                {l.par > 0 ? ` of ${l.par}` : ''}
                {l.short > 0 ? ` (${l.short} short)` : ''}
              </Badge>
            ))}
          </div>
        </div>
      ))}
      {recording && <RecordDialog kind={recording} onClose={() => setRecording(null)} />}
      {settingPar && <ParDialog onClose={() => setSettingPar(false)} />}
    </Section>
  );
}

function RecordDialog({ kind, onClose }: { kind: LinenMovementKind; onClose: () => void }) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const items = useQuery(linenQueries.items());
  const wards = useQuery(linenQueries.wards());
  const [wardId, setWardId] = useState('');
  const [infected, setInfected] = useState(false);
  const [process, setProcess] = useState<WashProcess>('Normal');
  const [temperature, setTemperature] = useState('');
  const [hold, setHold] = useState('');
  const [reason, setReason] = useState<CondemnReason>('Torn');
  const [from, setFrom] = useState<'Store' | 'Laundry'>('Laundry');
  const [counts, setCounts] = useState<Record<string, string>>({});
  const needsWard = kind === 'Issued' || kind === 'SoiledReturn' || kind === 'Lost';
  const canBeInfected = kind === 'SoiledReturn' || kind === 'Washed' || (kind === 'Condemned' && from === 'Laundry');
  const lines = countedLines(counts);
  const thermalNeeded = kind === 'Washed' && infected && process !== 'ThermalDisinfection';
  const temp = temperature ? Number(temperature) : null;
  const minutes = hold ? Number(hold) : null;
  const notDisinfected = kind === 'Washed' && process === 'ThermalDisinfection' && !disinfects(temp, minutes);
  const save = useMutation({
    mutationFn: () =>
      linenApi.record({
        kind,
        wardId: needsWard ? wardId : null,
        infected: canBeInfected && infected,
        process: kind === 'Washed' ? process : null,
        temperatureC: kind === 'Washed' && process === 'ThermalDisinfection' ? temp : null,
        holdMinutes: kind === 'Washed' && process === 'ThermalDisinfection' ? minutes : null,
        condemnReason: kind === 'Condemned' ? reason : null,
        from: kind === 'Condemned' ? from : null,
        notes: null,
        lines,
      }),
    onSuccess: (m) => {
      void queryClient.invalidateQueries({ queryKey: linenKeys.all });
      notify.success(KIND_LABELS[kind], `${m.number}: ${m.pieces} pieces.`);
      onClose();
    },
  });
  const ready = lines.length > 0 && (!needsWard || wardId) && !thermalNeeded && !notDisinfected;

  return (
    <Dialog open onOpenChange={(_, d) => !d.open && !save.isPending && onClose()}>
      <DialogSurface style={{ maxWidth: 560 }}>
        <form
          onSubmit={(event) => {
            event.preventDefault();
            if (ready && !save.isPending) {
              save.mutate();
            }
          }}
        >
          <DialogBody>
            <DialogTitle>{KIND_LABELS[kind]}</DialogTitle>
            <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
              {save.error && (
                <MessageBar intent="error">
                  <MessageBarBody>{errorMessage(save.error, 'The count could not be recorded.')}</MessageBarBody>
                </MessageBar>
              )}
              {needsWard && (
                <Field label="Ward" required>
                  <Select value={wardId} onChange={(_, d) => setWardId(d.value)}>
                    <option value="">Choose…</option>
                    {wards.data?.map((w) => (
                      <option key={w.id} value={w.id}>
                        {w.name}
                      </option>
                    ))}
                  </Select>
                </Field>
              )}
              {kind === 'Condemned' && (
                <div style={{ display: 'flex', gap: 12 }}>
                  <Field label="From" style={{ flex: 1 }}>
                    <Select value={from} onChange={(_, d) => setFrom(d.value as 'Store' | 'Laundry')}>
                      <option value="Laundry">The laundry (soiled)</option>
                      <option value="Store">The store (clean)</option>
                    </Select>
                  </Field>
                  <Field label="Reason" style={{ flex: 1 }}>
                    <Select value={reason} onChange={(_, d) => setReason(d.value as CondemnReason)}>
                      {(Object.keys(CONDEMN_LABELS) as CondemnReason[]).map((r) => (
                        <option key={r} value={r}>
                          {CONDEMN_LABELS[r]}
                        </option>
                      ))}
                    </Select>
                  </Field>
                </div>
              )}
              {canBeInfected && (
                <Checkbox
                  checked={infected}
                  onChange={(_, d) => setInfected(!!d.checked)}
                  label="Infected (isolation room, or soaked in blood or body fluids)"
                />
              )}
              {kind === 'Washed' && (
                <>
                  <Field label="Process">
                    <Select value={process} onChange={(_, d) => setProcess(d.value as WashProcess)}>
                      <option value="Normal">Normal wash</option>
                      <option value="ThermalDisinfection">Thermal disinfection</option>
                    </Select>
                  </Field>
                  {thermalNeeded && (
                    <MessageBar intent="warning">
                      <MessageBarBody>Infected linen is washed only by thermal disinfection.</MessageBarBody>
                    </MessageBar>
                  )}
                  {process === 'ThermalDisinfection' && (
                    <div style={{ display: 'flex', gap: 12 }}>
                      <Field
                        label="Temperature reached (°C)"
                        style={{ flex: 1 }}
                        validationMessage={
                          temperature && hold && notDisinfected
                            ? 'Hold 71 °C for 3 minutes, or 65 °C for 10: wash the batch again.'
                            : undefined
                        }
                      >
                        <Input type="number" value={temperature} onChange={(_, d) => setTemperature(d.value)} />
                      </Field>
                      <Field label="Held for (minutes)" style={{ flex: 1 }}>
                        <Input type="number" value={hold} onChange={(_, d) => setHold(d.value)} />
                      </Field>
                    </div>
                  )}
                </>
              )}
              <Caption1>Count each item in pieces; leave the rest blank.</Caption1>
              {items.data
                ?.filter((i) => i.isActive)
                .map((i) => (
                  <Field key={i.id} label={i.name} orientation="horizontal">
                    <Input
                      type="number"
                      min={0}
                      value={counts[i.id] ?? ''}
                      onChange={(_, d) => setCounts((c) => ({ ...c, [i.id]: d.value }))}
                    />
                  </Field>
                ))}
            </DialogContent>
            <DialogActions>
              <Button onClick={onClose} disabled={save.isPending}>
                Cancel
              </Button>
              <Button
                type="submit"
                appearance="primary"
                disabled={!ready || save.isPending}
                icon={save.isPending ? <Spinner size="tiny" aria-hidden /> : undefined}
              >
                Record
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
      </DialogSurface>
    </Dialog>
  );
}

function ParDialog({ onClose }: { onClose: () => void }) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const items = useQuery(linenQueries.items());
  const wards = useQuery(linenQueries.wards());
  const stock = useQuery(linenQueries.stock());
  const [wardId, setWardId] = useState('');
  const [pars, setPars] = useState<Record<string, string>>({});
  const current = stock.data?.wards.find((w) => w.wardId === wardId);
  const parOf = (itemId: string) =>
    pars[itemId] ?? String(current?.lines.find((l) => l.linenItemId === itemId)?.par ?? '');
  const save = useMutation({
    mutationFn: () =>
      linenApi.setPar(
        wardId,
        (items.data ?? [])
          .filter((i) => i.isActive && parOf(i.id) !== '')
          .map((i) => ({ linenItemId: i.id, quantity: Number(parOf(i.id)) })),
      ),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: linenKeys.all });
      notify.success('Par levels saved');
      onClose();
    },
  });

  return (
    <Dialog open onOpenChange={(_, d) => !d.open && !save.isPending && onClose()}>
      <DialogSurface style={{ maxWidth: 480 }}>
        <form
          onSubmit={(event) => {
            event.preventDefault();
            if (wardId && !save.isPending) {
              save.mutate();
            }
          }}
        >
          <DialogBody>
            <DialogTitle>Ward par levels</DialogTitle>
            <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
              {save.error && (
                <MessageBar intent="error">
                  <MessageBarBody>{errorMessage(save.error, 'The par levels could not be saved.')}</MessageBarBody>
                </MessageBar>
              )}
              <Field label="Ward" required>
                <Select
                  value={wardId}
                  onChange={(_, d) => {
                    setWardId(d.value);
                    setPars({});
                  }}
                >
                  <option value="">Choose…</option>
                  {wards.data?.map((w) => (
                    <option key={w.id} value={w.id}>
                      {w.name}
                    </option>
                  ))}
                </Select>
              </Field>
              {wardId && <Caption1>The clean pieces the ward should hold of each item.</Caption1>}
              {wardId &&
                items.data
                  ?.filter((i) => i.isActive)
                  .map((i) => (
                    <Field key={i.id} label={i.name} orientation="horizontal">
                      <Input
                        type="number"
                        min={0}
                        value={parOf(i.id)}
                        onChange={(_, d) => setPars((p) => ({ ...p, [i.id]: d.value }))}
                      />
                    </Field>
                  ))}
            </DialogContent>
            <DialogActions>
              <Button onClick={onClose} disabled={save.isPending}>
                Cancel
              </Button>
              <Button type="submit" appearance="primary" disabled={!wardId || save.isPending}>
                Save
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
      </DialogSurface>
    </Dialog>
  );
}

function MovementsTab() {
  const [from, setFrom] = useState(todayIso());
  const [to, setTo] = useState(todayIso());
  const movements = useQuery(linenQueries.movements(from, to));

  return (
    <Section flush>
      <FilterBar>
        <Field label="From">
          <Input type="date" value={from} onChange={(_, d) => setFrom(d.value)} />
        </Field>
        <Field label="To">
          <Input type="date" value={to} onChange={(_, d) => setTo(d.value)} />
        </Field>
      </FilterBar>
      <DataTable<LinenMovement>
        ariaLabel="Linen movements"
        rows={movements.data}
        loading={movements.isLoading}
        error={movements.error}
        onRetry={() => void movements.refetch()}
        getRowId={(m) => m.id}
        size="small"
        columns={[
          { key: 'number', header: 'No.', width: 150, render: (m) => <Text font="monospace">{m.number}</Text> },
          {
            key: 'kind',
            header: 'Movement',
            width: 220,
            render: (m) => (
              <span>
                {KIND_LABELS[m.kind]}
                {m.infected && (
                  <Badge size="small" appearance="tint" color="danger" style={{ marginLeft: 6 }}>
                    Infected
                  </Badge>
                )}
                <Caption1 block>
                  {[
                    m.wardName,
                    m.condemnReason && CONDEMN_LABELS[m.condemnReason],
                    m.process === 'ThermalDisinfection' ? `${m.temperatureC} °C, ${m.holdMinutes} min` : null,
                  ]
                    .filter(Boolean)
                    .join(' · ')}
                </Caption1>
              </span>
            ),
          },
          {
            key: 'lines',
            header: 'Pieces',
            render: (m) => m.lines.map((l) => `${l.itemName} ${l.quantity}`).join(', '),
          },
          {
            key: 'at',
            header: 'When',
            width: 190,
            render: (m) => (
              <span>
                {formatDateTime(m.at)}
                <Caption1 block>{m.byName}</Caption1>
              </span>
            ),
          },
        ]}
        emptyTitle="No movements in this period"
      />
    </Section>
  );
}

function ItemsTab() {
  const canManage = usePermission(LINEN_PERMISSIONS.manage);
  const items = useQuery(linenQueries.items());
  const [editing, setEditing] = useState<LinenItem | 'new' | null>(null);

  return (
    <Section
      flush
      actions={
        canManage && (
          <Button appearance="primary" icon={<AddRegular />} onClick={() => setEditing('new')}>
            Add an item
          </Button>
        )
      }
    >
      <DataTable<LinenItem>
        ariaLabel="Linen list"
        rows={items.data}
        loading={items.isLoading}
        error={items.error}
        onRetry={() => void items.refetch()}
        getRowId={(i) => i.id}
        size="small"
        columns={[
          { key: 'code', header: 'Code', width: 120, render: (i) => i.code },
          { key: 'name', header: 'Item', render: (i) => i.name },
          {
            key: 'washes',
            header: 'Washes a piece (life)',
            width: 190,
            render: (i) => (
              <span>
                {i.averageWashes ?? '—'} of {i.lifeWashes}
                {i.averageWashes !== null && i.averageWashes >= i.lifeWashes * 0.9 && (
                  <Badge size="small" appearance="tint" color="warning" style={{ marginLeft: 6 }}>
                    Near end of life
                  </Badge>
                )}
              </span>
            ),
          },
          { key: 'active', header: 'In use', width: 90, render: (i) => (i.isActive ? 'Yes' : 'No') },
        ]}
        rowActions={(i) =>
          canManage && (
            <Button size="small" icon={<EditRegular />} onClick={() => setEditing(i)}>
              Edit
            </Button>
          )
        }
        emptyTitle="No linen items yet"
      />
      {editing && <ItemDialog item={editing === 'new' ? null : editing} onClose={() => setEditing(null)} />}
    </Section>
  );
}

function ItemDialog({ item, onClose }: { item: LinenItem | null; onClose: () => void }) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [code, setCode] = useState(item?.code ?? '');
  const [name, setName] = useState(item?.name ?? '');
  const [life, setLife] = useState(String(item?.lifeWashes ?? 150));
  const [active, setActive] = useState(item?.isActive ?? true);
  const save = useMutation({
    mutationFn: () =>
      linenApi.saveItem({
        id: item?.id ?? null,
        code: code.trim(),
        name: name.trim(),
        lifeWashes: Number(life),
        isActive: active,
        version: item?.version ?? null,
      }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: linenKeys.all });
      notify.success('Item saved', name.trim());
      onClose();
    },
  });

  return (
    <Dialog open onOpenChange={(_, d) => !d.open && !save.isPending && onClose()}>
      <DialogSurface style={{ maxWidth: 460 }}>
        <form
          onSubmit={(event) => {
            event.preventDefault();
            if (code.trim() && name.trim() && Number(life) > 0 && !save.isPending) {
              save.mutate();
            }
          }}
        >
          <DialogBody>
            <DialogTitle>{item ? `Edit ${item.name}` : 'Add a linen item'}</DialogTitle>
            <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
              {save.error && (
                <MessageBar intent="error">
                  <MessageBarBody>{errorMessage(save.error, 'The item could not be saved.')}</MessageBarBody>
                </MessageBar>
              )}
              <Field label="Code" required>
                <Input value={code} maxLength={24} onChange={(_, d) => setCode(d.value)} />
              </Field>
              <Field label="Name" required>
                <Input value={name} maxLength={100} onChange={(_, d) => setName(d.value)} />
              </Field>
              <Field label="Expected life (washes)" hint="A cotton bed sheet lasts about 150 washes.">
                <Input type="number" min={1} max={1000} value={life} onChange={(_, d) => setLife(d.value)} />
              </Field>
              <Switch checked={active} onChange={(_, d) => setActive(d.checked)} label="In use" />
            </DialogContent>
            <DialogActions>
              <Button onClick={onClose} disabled={save.isPending}>
                Cancel
              </Button>
              <Button type="submit" appearance="primary" disabled={save.isPending}>
                Save
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
      </DialogSurface>
    </Dialog>
  );
}
