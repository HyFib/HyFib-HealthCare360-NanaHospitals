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
  Select,
  Spinner,
  Tab,
  TabList,
  Text,
  Textarea,
} from '@fluentui/react-components';
import { AddRegular, ArrowDownloadRegular, DeleteRegular, VehicleTruckProfileRegular } from '@fluentui/react-icons';
import { formatDateOnly, formatDateTime, todayIso } from '@hc360/core';
import { DataTable, FilterBar, Page, PageHeader, Section, StatusBadge, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { RequirePermission } from '@/modules/hr/shared/NoAccess';
import {
  bagProblems,
  BMW_PERMISSIONS,
  BMW_READERS,
  bmwApi,
  bmwKeys,
  bmwQueries,
  categoryHint,
  parseBarcodes,
  WASTE_CATEGORIES,
  type CategoryTotal,
  type WasteBag,
  type WasteCategory,
  type WasteHandover,
} from '@/modules/bmw/api';
import { errorMessage } from '@/modules/lab/common/errors';

type BmwTab = 'storage' | 'handovers' | 'register' | 'annual';
const TABS: BmwTab[] = ['storage', 'handovers', 'register', 'annual'];
const MONTH = /^\d{4}-\d{2}$/;

export const Route = createFileRoute('/_app/bmw/')({
  validateSearch: (s: Record<string, unknown>): { tab?: BmwTab; month?: string } => ({
    tab: TABS.includes(s.tab as BmwTab) ? (s.tab as BmwTab) : undefined,
    month: typeof s.month === 'string' && MONTH.test(s.month) ? s.month : undefined,
  }),
  component: () => (
    <RequirePermission permission={BMW_READERS} what="bio-medical waste">
      <BmwPage />
    </RequirePermission>
  ),
});

const categoryColor: Record<WasteCategory, 'warning' | 'danger' | 'informative' | 'brand'> = {
  Yellow: 'warning',
  Red: 'danger',
  White: 'informative',
  Blue: 'brand',
};

const kg = (value: number) => `${value.toLocaleString('en-IN', { maximumFractionDigits: 3 })} kg`;

/**
 * Bio-medical waste (BMW Management Rules 2016): bags bar-coded and weighed where the waste is generated, the storage
 * area emptied by the treatment facility within 48 hours, the month's register to publish, and the year's totals for
 * the annual report (Form IV) due to the State Pollution Control Board by 30 June.
 */
function BmwPage() {
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const canManage = usePermission(BMW_PERMISSIONS.manage);
  const tabs = canManage ? TABS : (['storage'] as BmwTab[]);
  const tab = search.tab && tabs.includes(search.tab) ? search.tab : 'storage';

  return (
    <Page>
      <PageHeader
        title="Bio-medical waste"
        subtitle="Weigh and bar-code each bag where the waste is generated. The treatment facility collects it within 48 hours; the month's register is published, and the year's totals go in the annual report (Form IV)."
      />
      {canManage && (
        <TabList selectedValue={tab} onTabSelect={(_, d) => void navigate({ search: { tab: d.value as BmwTab } })}>
          <Tab value="storage">Storage area</Tab>
          <Tab value="handovers">Handovers</Tab>
          <Tab value="register">Monthly register</Tab>
          <Tab value="annual">Annual report</Tab>
        </TabList>
      )}
      {tab === 'storage' && <StorageTab />}
      {tab === 'handovers' && <HandoversTab />}
      {tab === 'register' && <RegisterTab />}
      {tab === 'annual' && <AnnualTab />}
    </Page>
  );
}

function Totals({ totals }: { totals: CategoryTotal[] }) {
  return (
    <span style={{ display: 'flex', gap: 8, flexWrap: 'wrap' }}>
      {totals.map((t) => (
        <Badge key={t.category} appearance="tint" color={categoryColor[t.category]}>
          {t.category}: {t.bags} · {kg(t.kg)}
        </Badge>
      ))}
    </span>
  );
}

function StorageTab() {
  const canRecord = usePermission(BMW_PERMISSIONS.record);
  const pending = useQuery(bmwQueries.pending());
  const [recording, setRecording] = useState(false);
  const [voiding, setVoiding] = useState<WasteBag | null>(null);
  const data = pending.data;

  return (
    <Section
      flush
      actions={
        canRecord && (
          <Button appearance="primary" icon={<AddRegular />} onClick={() => setRecording(true)}>
            Weigh a bag
          </Button>
        )
      }
    >
      <div style={{ padding: '8px 16px 12px', display: 'flex', flexDirection: 'column', gap: 8 }}>
        {data && <Totals totals={data.byCategory} />}
        {data && data.overdue > 0 && (
          <MessageBar intent="error">
            <MessageBarBody>
              {data.overdue === 1 ? 'One bag has' : `${data.overdue} bags have`} been kept beyond 48 hours. Call the
              treatment facility for a collection today.
            </MessageBarBody>
          </MessageBar>
        )}
      </div>
      <DataTable<WasteBag>
        ariaLabel="Bags in the storage area"
        rows={data?.bags}
        loading={pending.isLoading}
        error={pending.error}
        onRetry={() => void pending.refetch()}
        getRowId={(b) => b.id}
        size="small"
        columns={[
          { key: 'barcode', header: 'Bar code', width: 170, render: (b) => <Text font="monospace">{b.barcode}</Text> },
          {
            key: 'category',
            header: 'Colour',
            width: 110,
            render: (b) => (
              <Badge appearance="tint" color={categoryColor[b.category]}>
                {b.category}
              </Badge>
            ),
          },
          { key: 'source', header: 'From', render: (b) => b.source },
          { key: 'weight', header: 'Weight', width: 100, render: (b) => kg(b.weightKg) },
          {
            key: 'collected',
            header: 'Collected',
            width: 190,
            render: (b) => (
              <span>
                {formatDateTime(b.collectedAt)}
                {b.overdue && (
                  <Badge size="small" appearance="filled" color="danger" style={{ marginLeft: 6 }}>
                    Over 48 h
                  </Badge>
                )}
                <Caption1 block>{b.collectedByName}</Caption1>
              </span>
            ),
          },
        ]}
        rowActions={(b) => (
          <Button size="small" icon={<DeleteRegular />} onClick={() => setVoiding(b)}>
            Void
          </Button>
        )}
        emptyTitle="The storage area is empty"
        emptyDescription="Bags appear here once weighed, until the treatment facility collects them."
      />
      {recording && <RecordBagDialog onClose={() => setRecording(false)} />}
      {voiding && <VoidBagDialog bag={voiding} onClose={() => setVoiding(null)} />}
    </Section>
  );
}

function RecordBagDialog({ onClose }: { onClose: () => void }) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [category, setCategory] = useState<WasteCategory | ''>('');
  const [source, setSource] = useState('');
  const [weight, setWeight] = useState('');
  const [tried, setTried] = useState(false);
  const problems = bagProblems({ category, source, weight });
  const valid = Object.values(problems).every((p) => p === null);
  const show = (p: string | null) => (tried ? (p ?? undefined) : undefined);
  const save = useMutation({
    mutationFn: () =>
      bmwApi.record({
        category: category as WasteCategory,
        departmentId: null,
        source: source.trim(),
        weightKg: Number(weight),
        collectedAt: null,
      }),
    onSuccess: (bag) => {
      void queryClient.invalidateQueries({ queryKey: bmwKeys.all });
      notify.success('Bag recorded', `Label it ${bag.barcode}.`);
      onClose();
    },
  });

  return (
    <Dialog open onOpenChange={(_, d) => !d.open && !save.isPending && onClose()}>
      <DialogSurface style={{ maxWidth: 520 }}>
        <form
          onSubmit={(event) => {
            event.preventDefault();
            setTried(true);
            if (valid && !save.isPending) {
              save.mutate();
            }
          }}
        >
          <DialogBody>
            <DialogTitle>Weigh a bag</DialogTitle>
            <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
              {save.error && (
                <MessageBar intent="error">
                  <MessageBarBody>{errorMessage(save.error, 'The bag could not be recorded.')}</MessageBarBody>
                </MessageBar>
              )}
              <Field
                label="Colour"
                required
                validationMessage={show(problems.category)}
                hint={category ? categoryHint[category] : undefined}
              >
                <Select value={category} onChange={(_, d) => setCategory(d.value as WasteCategory | '')}>
                  <option value="">Choose…</option>
                  {WASTE_CATEGORIES.map((c) => (
                    <option key={c} value={c}>
                      {c}
                    </option>
                  ))}
                </Select>
              </Field>
              <Field
                label="From"
                required
                validationMessage={show(problems.source)}
                hint="The ward, theatre or department."
              >
                <Input value={source} maxLength={120} onChange={(_, d) => setSource(d.value)} autoFocus />
              </Field>
              <Field label="Weight (kg)" required validationMessage={show(problems.weight)}>
                <Input
                  type="number"
                  inputMode="decimal"
                  step="0.01"
                  min={0}
                  value={weight}
                  onChange={(_, d) => setWeight(d.value)}
                />
              </Field>
            </DialogContent>
            <DialogActions>
              <Button onClick={onClose} disabled={save.isPending}>
                Cancel
              </Button>
              <Button
                type="submit"
                appearance="primary"
                disabled={save.isPending}
                icon={save.isPending ? <Spinner size="tiny" aria-hidden /> : undefined}
              >
                Record bag
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
      </DialogSurface>
    </Dialog>
  );
}

function VoidBagDialog({ bag, onClose }: { bag: WasteBag; onClose: () => void }) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [reason, setReason] = useState('');
  const [tried, setTried] = useState(false);
  const save = useMutation({
    mutationFn: () => bmwApi.void(bag.id, reason.trim(), bag.version),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: bmwKeys.all });
      notify.success('Bag voided', bag.barcode);
      onClose();
    },
  });

  return (
    <Dialog open onOpenChange={(_, d) => !d.open && !save.isPending && onClose()}>
      <DialogSurface style={{ maxWidth: 480 }}>
        <form
          onSubmit={(event) => {
            event.preventDefault();
            setTried(true);
            if (reason.trim() && !save.isPending) {
              save.mutate();
            }
          }}
        >
          <DialogBody>
            <DialogTitle>Void bag {bag.barcode}</DialogTitle>
            <DialogContent>
              {save.error && (
                <MessageBar intent="error">
                  <MessageBarBody>{errorMessage(save.error, 'The bag could not be voided.')}</MessageBarBody>
                </MessageBar>
              )}
              <Field
                label="Reason"
                required
                validationMessage={tried && !reason.trim() ? 'Say why the bag is voided.' : undefined}
              >
                <Textarea value={reason} maxLength={300} onChange={(_, d) => setReason(d.value)} />
              </Field>
            </DialogContent>
            <DialogActions>
              <Button onClick={onClose} disabled={save.isPending}>
                Cancel
              </Button>
              <Button type="submit" appearance="primary" disabled={save.isPending}>
                Void
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
      </DialogSurface>
    </Dialog>
  );
}

function HandoversTab() {
  const handovers = useQuery(bmwQueries.handovers());
  const [handing, setHanding] = useState(false);

  return (
    <Section
      flush
      actions={
        <Button appearance="primary" icon={<VehicleTruckProfileRegular />} onClick={() => setHanding(true)}>
          Hand over bags
        </Button>
      }
    >
      <DataTable<WasteHandover>
        ariaLabel="Handovers to the treatment facility"
        rows={handovers.data}
        loading={handovers.isLoading}
        error={handovers.error}
        onRetry={() => void handovers.refetch()}
        getRowId={(h) => h.id}
        size="small"
        columns={[
          {
            key: 'number',
            header: 'Handover',
            width: 190,
            render: (h) => (
              <span>
                {h.number}
                <Caption1 block>{formatDateTime(h.handedOverAt)}</Caption1>
              </span>
            ),
          },
          {
            key: 'cbwtf',
            header: 'To',
            render: (h) => (
              <span>
                {h.cbwtf}
                <Caption1 block>
                  {h.vehicleNumber} · {h.receivedBy}
                  {h.receiptNumber ? ` · receipt ${h.receiptNumber}` : ''}
                </Caption1>
              </span>
            ),
          },
          { key: 'bags', header: 'Bags', render: (h) => <Totals totals={h.byCategory} /> },
          {
            key: 'late',
            header: 'Kept over 48 h',
            width: 130,
            render: (h) =>
              h.lateBags > 0 ? (
                <StatusBadge status="late" tone="danger" label={`${h.lateBags} ${h.lateBags === 1 ? 'bag' : 'bags'}`} />
              ) : (
                'None'
              ),
          },
        ]}
        emptyTitle="No handovers in the last week"
        emptyDescription="Hand the bags over when the treatment facility's vehicle collects them."
      />
      {handing && <HandOverDialog onClose={() => setHanding(false)} />}
    </Section>
  );
}

function HandOverDialog({ onClose }: { onClose: () => void }) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const pending = useQuery(bmwQueries.pending());
  const [barcodes, setBarcodes] = useState('');
  const [cbwtf, setCbwtf] = useState('');
  const [vehicle, setVehicle] = useState('');
  const [receivedBy, setReceivedBy] = useState('');
  const [receipt, setReceipt] = useState('');
  const [tried, setTried] = useState(false);
  const scanned = parseBarcodes(barcodes);
  const problem =
    scanned.length === 0
      ? 'Scan the bags the vehicle takes.'
      : !vehicle.trim() || !receivedBy.trim()
        ? 'Enter the vehicle number and who received the bags.'
        : null;
  const save = useMutation({
    mutationFn: () =>
      bmwApi.handOver({
        barcodes: scanned,
        cbwtf: cbwtf.trim() || null,
        vehicleNumber: vehicle.trim(),
        receivedBy: receivedBy.trim(),
        receiptNumber: receipt.trim() || null,
      }),
    onSuccess: (h) => {
      void queryClient.invalidateQueries({ queryKey: bmwKeys.all });
      notify.success('Handed over', `${h.number}: ${h.bagCount} bags, ${kg(h.totalKg)}.`);
      onClose();
    },
  });

  return (
    <Dialog open onOpenChange={(_, d) => !d.open && !save.isPending && onClose()}>
      <DialogSurface style={{ maxWidth: 600 }}>
        <form
          onSubmit={(event) => {
            event.preventDefault();
            setTried(true);
            if (!problem && !save.isPending) {
              save.mutate();
            }
          }}
        >
          <DialogBody>
            <DialogTitle>Hand over to the treatment facility</DialogTitle>
            <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
              {save.error && (
                <MessageBar intent="error">
                  <MessageBarBody>{errorMessage(save.error, 'The handover could not be recorded.')}</MessageBarBody>
                </MessageBar>
              )}
              <Field
                label="Bags"
                required
                hint={`Scan each bag's bar code as it is loaded. ${pending.data?.bags.length ?? 0} bags are waiting.`}
              >
                <Textarea value={barcodes} rows={5} onChange={(_, d) => setBarcodes(d.value)} autoFocus />
              </Field>
              {pending.data && pending.data.bags.length > 0 && (
                <Button size="small" onClick={() => setBarcodes(pending.data!.bags.map((b) => b.barcode).join('\n'))}>
                  All waiting bags
                </Button>
              )}
              <Field
                label="Treatment facility"
                hint="Leave empty for the facility set under Administration › Settings."
              >
                <Input value={cbwtf} maxLength={200} onChange={(_, d) => setCbwtf(d.value)} />
              </Field>
              <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(160px, 1fr))', gap: 12 }}>
                <Field label="Vehicle number" required>
                  <Input value={vehicle} maxLength={20} onChange={(_, d) => setVehicle(d.value)} />
                </Field>
                <Field label="Received by" required>
                  <Input value={receivedBy} maxLength={120} onChange={(_, d) => setReceivedBy(d.value)} />
                </Field>
                <Field label="Receipt no.">
                  <Input value={receipt} maxLength={40} onChange={(_, d) => setReceipt(d.value)} />
                </Field>
              </div>
              {tried && problem && (
                <MessageBar intent="warning">
                  <MessageBarBody>{problem}</MessageBarBody>
                </MessageBar>
              )}
            </DialogContent>
            <DialogActions>
              <Button onClick={onClose} disabled={save.isPending}>
                Cancel
              </Button>
              <Button
                type="submit"
                appearance="primary"
                disabled={save.isPending}
                icon={save.isPending ? <Spinner size="tiny" aria-hidden /> : undefined}
              >
                Record handover
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
      </DialogSurface>
    </Dialog>
  );
}

function RegisterTab() {
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const notify = useNotify();
  const month = search.month ?? todayIso().slice(0, 7);
  const [y, m] = [Number(month.slice(0, 4)), Number(month.slice(5, 7))];
  const register = useQuery(bmwQueries.month(y, m));
  const exportCsv = useMutation({
    mutationFn: () => bmwApi.exportMonth(y, m),
    onSuccess: (blob) => {
      const url = URL.createObjectURL(blob);
      const link = document.createElement('a');
      link.href = url;
      link.download = `bmw-register-${month}.csv`;
      document.body.appendChild(link);
      link.click();
      link.remove();
      setTimeout(() => URL.revokeObjectURL(url), 10_000);
    },
    onError: (error) => notify.error(error, 'Could not download the register'),
  });
  type Day = { date: string; byCategory: CategoryTotal[]; totalKg: number };

  return (
    <Section
      flush
      actions={
        <Button
          icon={<ArrowDownloadRegular />}
          disabled={!register.data || exportCsv.isPending}
          onClick={() => exportCsv.mutate()}
        >
          Download CSV
        </Button>
      }
    >
      <div style={{ padding: '8px 16px 12px', display: 'flex', flexDirection: 'column', gap: 8 }}>
        <FilterBar>
          <Field label="Month">
            <Input
              type="month"
              value={month}
              max={todayIso().slice(0, 7)}
              onChange={(_, d) =>
                void navigate({ search: { tab: 'register', month: MONTH.test(d.value) ? d.value : undefined } })
              }
            />
          </Field>
          {register.data && <Totals totals={register.data.totals} />}
        </FilterBar>
        <Caption1>
          The month&apos;s register is to be published on the hospital&apos;s website (BMW Rules 2016, rule 4).
        </Caption1>
      </div>
      <DataTable<Day>
        ariaLabel="Monthly register of bio-medical waste"
        rows={register.data?.days}
        loading={register.isLoading}
        error={register.error}
        onRetry={() => void register.refetch()}
        getRowId={(d) => d.date}
        size="small"
        columns={[
          { key: 'date', header: 'Date', width: 130, render: (d) => formatDateOnly(d.date) },
          ...WASTE_CATEGORIES.map((c) => ({
            key: c,
            header: c,
            render: (d: Day) => kg(d.byCategory.find((t) => t.category === c)?.kg ?? 0),
          })),
          { key: 'total', header: 'Total', render: (d) => kg(d.totalKg) },
        ]}
        emptyTitle="No waste recorded this month"
        emptyDescription="Days appear here as bags are weighed."
      />
    </Section>
  );
}

function AnnualTab() {
  const lastYear = Number(todayIso().slice(0, 4)) - 1;
  const [year, setYear] = useState(lastYear);
  const report = useQuery(bmwQueries.year(year));
  const data = report.data;

  return (
    <Section>
      <FilterBar>
        <Field label="Year">
          <Input
            type="number"
            min={2000}
            max={lastYear + 1}
            value={String(year)}
            onChange={(_, d) => Number.isInteger(Number(d.value)) && setYear(Number(d.value))}
          />
        </Field>
      </FilterBar>
      {report.error && (
        <MessageBar intent="error">
          <MessageBarBody>The year could not be loaded.</MessageBarBody>
        </MessageBar>
      )}
      {data && (
        <div style={{ display: 'flex', flexDirection: 'column', gap: 8, marginTop: 12 }}>
          {(!data.authorisationNumber || !data.cbwtf) && (
            <MessageBar intent="warning">
              <MessageBarBody>
                Set the authorisation number (bmw.authorisationNumber) and the treatment facility (bmw.cbwtf) under
                Administration › Settings; Form IV asks for both.
              </MessageBarBody>
            </MessageBar>
          )}
          <Text>Authorisation: {data.authorisationNumber ?? 'not set'}</Text>
          <Text>Treatment facility: {data.cbwtf ?? 'not set'}</Text>
          <Totals totals={data.totals} />
          <Text>
            {kg(data.totalKg)} in the year, {kg(data.averageKgPerDay)} a day on average; {data.handovers} handovers,{' '}
            {data.lateBags} bags kept beyond 48 hours, {data.voidedBags} voided.
          </Text>
          <Caption1>
            Form IV is due to the State Pollution Control Board by 30 June for the calendar year before.
          </Caption1>
        </div>
      )}
    </Section>
  );
}
