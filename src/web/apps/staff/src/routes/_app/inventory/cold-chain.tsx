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
  DrawerHeader,
  DrawerHeaderTitle,
  Field,
  Input,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  OverlayDrawer,
  Select,
  Spinner,
  Text,
  Textarea,
} from '@fluentui/react-components';
import { AddRegular, DismissRegular } from '@fluentui/react-icons';
import { formatDateTime, formatTime, isApiError, todayIso } from '@hc360/core';
import { DataTable, Page, PageHeader, Section, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute } from '@tanstack/react-router';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { inventoryQueries } from '@/modules/inventory/api';
import {
  COLD_UNIT_KINDS,
  celsius,
  coldChainApi,
  coldChainKeys,
  coldChainQueries,
  coldUnitKindLabel,
  rangeWords,
  type ColdChainCheckStatus,
  type ColdChainExcursionDto,
  type ColdChainLoggerKey,
  type ColdStorageUnitDto,
  type ColdUnitKind,
} from '@/modules/inventory/coldChain';
import { SUPPLY_PERMISSIONS as S } from '@/modules/inventory/permissions';
import { StorePicker } from '@/modules/inventory/StorePicker';

export const Route = createFileRoute('/_app/inventory/cold-chain')({
  component: ColdChainPage,
});

const checkTone: Record<ColdChainCheckStatus, 'success' | 'warning' | 'danger' | 'informative'> = {
  Taken: 'success',
  Due: 'warning',
  Missed: 'danger',
  Upcoming: 'informative',
};

/**
 * The cold-chain temperature log: each refrigerator and freezer with its last reading and today's two checks (06:00–11:00
 * and 16:00–21:00), readings entered by hand, the daily register, and excursions with what was done about the stock.
 */
function ColdChainPage() {
  const canRecord = usePermission(S.coldChainRecord);
  const canManage = usePermission(S.coldChainManage);
  const [storeId, setStoreId] = useState<string>('');
  const stores = useQuery(inventoryQueries.stores());
  const units = useQuery(coldChainQueries.units(storeId || undefined));
  const excursions = useQuery(coldChainQueries.excursions(storeId || undefined, true));
  const [recording, setRecording] = useState<ColdStorageUnitDto | null>(null);
  const [register, setRegister] = useState<ColdStorageUnitDto | null>(null);
  const [editing, setEditing] = useState<ColdStorageUnitDto | 'new' | null>(null);
  const [acting, setActing] = useState<ColdChainExcursionDto | null>(null);
  const unitName = (id: string) => units.data?.find((u) => u.id === id)?.name ?? '';

  return (
    <Page maxWidth={1200}>
      <PageHeader
        title="Cold chain"
        subtitle="Refrigerator and freezer temperatures: read twice a day, every excursion alerted to the store's in-charge"
        actions={
          canManage && (
            <Button appearance="primary" icon={<AddRegular />} onClick={() => setEditing('new')}>
              Add unit
            </Button>
          )
        }
      />
      <Section flush>
        <StorePicker
          stores={stores.data ?? []}
          value={storeId}
          onChange={(id) => setStoreId(id ?? '')}
          allowAll
          loading={stores.isLoading}
        />
        <DataTable<ColdStorageUnitDto>
          ariaLabel="Refrigerators and freezers"
          rows={units.data}
          loading={units.isLoading}
          error={units.error}
          onRetry={() => void units.refetch()}
          getRowId={(u) => u.id}
          size="small"
          columns={[
            {
              key: 'unit',
              header: 'Unit',
              render: (u) => (
                <span>
                  <Text weight="semibold">{u.name}</Text>
                  <Caption1 block>{`${u.storeName} · ${rangeWords(u.minCelsius, u.maxCelsius)}`}</Caption1>
                </span>
              ),
            },
            {
              key: 'last',
              header: 'Last reading',
              render: (u) =>
                u.lastCelsius === null ? (
                  '—'
                ) : (
                  <span>
                    <Text weight="semibold">{celsius(u.lastCelsius)}</Text>{' '}
                    {u.lastOutOfRange && (
                      <Badge size="small" color="danger">
                        Out of range
                      </Badge>
                    )}
                    <Caption1 block>{formatDateTime(u.lastReadingAt)}</Caption1>
                  </span>
                ),
            },
            {
              key: 'checks',
              header: "Today's checks",
              render: (u) => (
                <span>
                  <Badge size="small" appearance="tint" color={checkTone[u.morningCheck]}>
                    {`Morning: ${u.morningCheck}`}
                  </Badge>{' '}
                  <Badge size="small" appearance="tint" color={checkTone[u.eveningCheck]}>
                    {`Evening: ${u.eveningCheck}`}
                  </Badge>
                </span>
              ),
            },
            {
              key: 'logger',
              header: 'Data logger',
              priority: 'low',
              render: (u) =>
                u.hasLogger
                  ? u.lastLoggerReadingAt
                    ? `Last heard ${formatDateTime(u.lastLoggerReadingAt)}`
                    : 'Not heard yet'
                  : '—',
            },
          ]}
          rowActions={(u) => (
            <span>
              {canRecord && u.isActive && (
                <Button size="small" appearance="primary" onClick={() => setRecording(u)}>
                  Record
                </Button>
              )}{' '}
              <Button size="small" appearance="subtle" onClick={() => setRegister(u)}>
                Register
              </Button>
              {canManage && (
                <Button size="small" appearance="subtle" onClick={() => setEditing(u)}>
                  Edit
                </Button>
              )}
            </span>
          )}
        />
      </Section>
      <Section title="Excursions to act on" flush>
        <DataTable<ColdChainExcursionDto>
          ariaLabel="Excursions"
          rows={excursions.data}
          loading={excursions.isLoading}
          error={excursions.error}
          onRetry={() => void excursions.refetch()}
          getRowId={(e) => e.id}
          size="small"
          columns={[
            { key: 'unit', header: 'Unit', render: (e) => unitName(e.unitId) },
            {
              key: 'when',
              header: 'When',
              render: (e) =>
                `${formatDateTime(e.startedAt)}${e.endedAt ? ` – ${formatTime(e.endedAt)}` : ' (still out of range)'}`,
            },
            {
              key: 'extreme',
              header: 'Reading',
              render: (e) => `${e.extreme}, allowed ${rangeWords(e.minCelsius, e.maxCelsius)}`,
            },
            { key: 'items', header: 'Items stored there', priority: 'low', render: (e) => e.itemsAtRisk ?? '—' },
            { key: 'action', header: 'Action taken', render: (e) => e.actionTaken ?? 'Not recorded' },
          ]}
          rowActions={
            canRecord
              ? (e) => (
                  <Button size="small" appearance="subtle" onClick={() => setActing(e)}>
                    Record action
                  </Button>
                )
              : undefined
          }
        />
      </Section>
      {recording && <RecordDialog unit={recording} onClose={() => setRecording(null)} />}
      {register && <RegisterDrawer unit={register} onClose={() => setRegister(null)} />}
      {editing && (
        <UnitDialog
          unit={editing === 'new' ? null : editing}
          stores={(stores.data ?? []).map((s) => ({ id: s.id, name: s.name }))}
          onClose={() => setEditing(null)}
        />
      )}
      {acting && <ActionDialog excursion={acting} onClose={() => setActing(null)} />}
    </Page>
  );
}

function RecordDialog({ unit, onClose }: { unit: ColdStorageUnitDto; onClose: () => void }) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [value, setValue] = useState('');
  const [note, setNote] = useState('');
  const reading = Number(value.replace('−', '-'));
  const valid = value.trim() !== '' && Number.isFinite(reading) && reading >= -100 && reading <= 100;
  const record = useMutation({
    mutationFn: () => coldChainApi.record(unit.id, { celsius: reading, takenAt: null, note: note.trim() || null }),
    onSuccess: (saved) => {
      if (saved.outOfRange) {
        notify.info(
          `${celsius(saved.celsius)} is out of range`,
          `Allowed ${saved.allowedRange}. The store's in-charge has been alerted; record what is done about the stock.`,
        );
      } else {
        notify.success(`${celsius(saved.celsius)} recorded`);
      }
      void queryClient.invalidateQueries({ queryKey: coldChainKeys.all });
      onClose();
    },
  });
  return (
    <Dialog open onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface>
        <DialogBody>
          <DialogTitle>{`Temperature of ${unit.name}`}</DialogTitle>
          <DialogContent>
            <Field label="Reading (°C)" required hint={`Allowed ${rangeWords(unit.minCelsius, unit.maxCelsius)}`}>
              <Input type="number" step={0.1} value={value} onChange={(_, d) => setValue(d.value)} />
            </Field>
            <Field label="Note (optional)">
              <Textarea value={note} maxLength={300} onChange={(_, d) => setNote(d.value)} />
            </Field>
            {record.error && (
              <MessageBar intent="error">
                <MessageBarBody>{isApiError(record.error) ? record.error.userMessage : 'Not saved.'}</MessageBarBody>
              </MessageBar>
            )}
          </DialogContent>
          <DialogActions>
            <Button appearance="primary" disabled={!valid || record.isPending} onClick={() => record.mutate()}>
              Record
            </Button>
            <Button onClick={onClose}>Cancel</Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}

function RegisterDrawer({ unit, onClose }: { unit: ColdStorageUnitDto; onClose: () => void }) {
  const [date, setDate] = useState(todayIso());
  const register = useQuery(coldChainQueries.register(unit.id, date));
  const r = register.data;
  return (
    <OverlayDrawer open position="end" size="medium" onOpenChange={(_, d) => !d.open && onClose()}>
      <DrawerHeader>
        <DrawerHeaderTitle
          action={<Button appearance="subtle" aria-label="Close" icon={<DismissRegular />} onClick={onClose} />}
        >
          {`Cold-chain register: ${unit.name}`}
        </DrawerHeaderTitle>
      </DrawerHeader>
      <DrawerBody>
        <Field label="Date">
          <Input type="date" value={date} max={todayIso()} onChange={(_, d) => d.value && setDate(d.value)} />
        </Field>
        {!r ? (
          <Spinner label="Loading…" />
        ) : (
          <>
            <Text block>
              {`${r.storeName} · allowed ${rangeWords(r.minCelsius, r.maxCelsius)} · low ${r.lowestCelsius === null ? '—' : celsius(r.lowestCelsius)}, high ${r.highestCelsius === null ? '—' : celsius(r.highestCelsius)}`}
            </Text>
            {r.checks.map((c) => (
              <Text block key={c.slot}>
                {`${c.slot} check: ${c.status}${c.takenAt ? `, ${celsius(c.celsius ?? 0)} at ${formatTime(c.takenAt)} by ${c.recordedBy ?? '—'}` : ''}`}
              </Text>
            ))}
            {r.excursions.map((e) => (
              <MessageBar key={e.id} intent="error" layout="multiline">
                <MessageBarBody>
                  <MessageBarTitle>{`Excursion ${e.extreme}`}</MessageBarTitle>
                  {`${formatDateTime(e.startedAt)}${e.endedAt ? ` to ${formatTime(e.endedAt)}` : ', still out of range'}. Action: ${e.actionTaken ?? 'not recorded'}.`}
                </MessageBarBody>
              </MessageBar>
            ))}
            <DataTable
              ariaLabel="Readings"
              rows={r.readings}
              getRowId={(x) => x.id}
              size="small"
              columns={[
                { key: 'time', header: 'Time', render: (x) => formatTime(x.takenAt) },
                {
                  key: 'temp',
                  header: 'Reading',
                  render: (x) => (
                    <span>
                      {celsius(x.celsius)}{' '}
                      {x.outOfRange && (
                        <Badge size="small" color="danger">
                          Out of range
                        </Badge>
                      )}
                    </span>
                  ),
                },
                {
                  key: 'source',
                  header: 'From',
                  render: (x) => (x.source === 'Logger' ? 'Data logger' : (x.recordedBy ?? 'By hand')),
                },
                { key: 'note', header: 'Note', priority: 'low', render: (x) => x.note ?? '' },
              ]}
            />
          </>
        )}
      </DrawerBody>
    </OverlayDrawer>
  );
}

function UnitDialog({
  unit,
  stores,
  onClose,
}: {
  unit: ColdStorageUnitDto | null;
  stores: { id: string; name: string }[];
  onClose: () => void;
}) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [storeId, setStoreId] = useState(unit?.storeId ?? stores[0]?.id ?? '');
  const [code, setCode] = useState(unit?.code ?? '');
  const [name, setName] = useState(unit?.name ?? '');
  const [kind, setKind] = useState<ColdUnitKind>(unit?.kind ?? 'Refrigerator');
  const [min, setMin] = useState(unit ? String(unit.minCelsius) : '');
  const [max, setMax] = useState(unit ? String(unit.maxCelsius) : '');
  const [key, setKey] = useState<ColdChainLoggerKey | null>(null);
  const save = useMutation({
    mutationFn: async () => {
      await coldChainApi.saveUnit(unit?.id ?? null, {
        storeId,
        code: code.trim(),
        name: name.trim(),
        kind,
        minCelsius: min.trim() === '' ? null : Number(min),
        maxCelsius: max.trim() === '' ? null : Number(max),
        isActive: true,
        version: unit?.version ?? null,
      });
    },
    onSuccess: () => {
      notify.success(unit ? 'Unit saved' : 'Unit added');
      void queryClient.invalidateQueries({ queryKey: coldChainKeys.all });
      onClose();
    },
  });
  const issue = useMutation({
    mutationFn: () => coldChainApi.issueLoggerKey(unit!.id),
    onSuccess: (issued) => {
      setKey(issued);
      void queryClient.invalidateQueries({ queryKey: coldChainKeys.all });
    },
  });
  const error = save.error ?? issue.error;
  return (
    <Dialog open onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface>
        <DialogBody>
          <DialogTitle>{unit ? `Edit ${unit.name}` : 'Add a refrigerator or freezer'}</DialogTitle>
          <DialogContent>
            <Field label="Store" required>
              <Select value={storeId} onChange={(_, d) => setStoreId(d.value)}>
                {stores.map((s) => (
                  <option key={s.id} value={s.id}>
                    {s.name}
                  </option>
                ))}
              </Select>
            </Field>
            <Field label="Code" required>
              <Input value={code} maxLength={24} onChange={(_, d) => setCode(d.value)} />
            </Field>
            <Field label="Name" required>
              <Input value={name} maxLength={100} onChange={(_, d) => setName(d.value)} />
            </Field>
            <Field label="Kind">
              <Select value={kind} onChange={(_, d) => setKind(d.value as ColdUnitKind)}>
                {COLD_UNIT_KINDS.map((k) => (
                  <option key={k} value={k}>
                    {coldUnitKindLabel[k]}
                  </option>
                ))}
              </Select>
            </Field>
            <Field label="Lowest allowed (°C)" hint="Empty for the kind's default">
              <Input type="number" step={0.1} value={min} onChange={(_, d) => setMin(d.value)} />
            </Field>
            <Field label="Highest allowed (°C)" hint="Empty for the kind's default">
              <Input type="number" step={0.1} value={max} onChange={(_, d) => setMax(d.value)} />
            </Field>
            {unit && (
              <Field label="Data logger" hint="A new key stops the old one. It is shown once: enter it in the logger.">
                <Button onClick={() => issue.mutate()} disabled={issue.isPending}>
                  {unit.hasLogger ? 'Issue a new logger key' : 'Issue a logger key'}
                </Button>
              </Field>
            )}
            {key && (
              <MessageBar intent="warning" layout="multiline">
                <MessageBarBody>
                  <MessageBarTitle>Logger key (shown once)</MessageBarTitle>
                  <Text font="monospace" block>
                    {key.key}
                  </Text>
                  {`POST readings to ${key.path} with the key in the ${key.header} header.`}
                </MessageBarBody>
              </MessageBar>
            )}
            {error && (
              <MessageBar intent="error">
                <MessageBarBody>{isApiError(error) ? error.userMessage : 'Not saved.'}</MessageBarBody>
              </MessageBar>
            )}
          </DialogContent>
          <DialogActions>
            <Button
              appearance="primary"
              disabled={!storeId || !code.trim() || !name.trim() || save.isPending}
              onClick={() => save.mutate()}
            >
              Save
            </Button>
            <Button onClick={onClose}>Close</Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}

function ActionDialog({ excursion, onClose }: { excursion: ColdChainExcursionDto; onClose: () => void }) {
  const queryClient = useQueryClient();
  const [action, setAction] = useState(excursion.actionTaken ?? '');
  const save = useMutation({
    mutationFn: () => coldChainApi.recordAction(excursion.id, action.trim()),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: coldChainKeys.all });
      onClose();
    },
  });
  return (
    <Dialog open onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface>
        <DialogBody>
          <DialogTitle>{`Excursion ${excursion.extreme}`}</DialogTitle>
          <DialogContent>
            <Text block>{`Items stored there: ${excursion.itemsAtRisk ?? '—'}`}</Text>
            <Field label="What was done about the stock" required>
              <Textarea
                value={action}
                maxLength={1000}
                onChange={(_, d) => setAction(d.value)}
                placeholder="e.g. Moved to the IP pharmacy fridge at 14:20; insulin quarantined pending the manufacturer's advice"
              />
            </Field>
            {save.error && (
              <MessageBar intent="error">
                <MessageBarBody>{isApiError(save.error) ? save.error.userMessage : 'Not saved.'}</MessageBarBody>
              </MessageBar>
            )}
          </DialogContent>
          <DialogActions>
            <Button
              appearance="primary"
              disabled={action.trim().length < 3 || save.isPending}
              onClick={() => save.mutate()}
            >
              Save
            </Button>
            <Button onClick={onClose}>Cancel</Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}
