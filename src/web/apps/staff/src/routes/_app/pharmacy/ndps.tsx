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
import { AddRegular, ArrowDownloadRegular, CheckmarkRegular } from '@fluentui/react-icons';
import { formatDateTime, todayIso } from '@hc360/core';
import { ConfirmDialog, DataTable, FilterBar, Page, PageHeader, Section, StatusBadge, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { RequirePermission } from '@/modules/hr/shared/NoAccess';
import { formatQty } from '@/modules/inventory/format';
import { movementLabel } from '@/modules/inventory/labels';
import { errorMessage } from '@/modules/lab/common/errors';
import { PatientPicker, type PickedPatient } from '@/modules/patients/PatientPicker';
import {
  countProblem,
  NDPS_COUNT_PERMISSION,
  ndpsApi,
  ndpsKeys,
  ndpsQueries,
  signed,
  type NarcoticCount,
  type NdpsAnnualDrug,
  type NdpsDailyDrug,
  type NdpsPatientLine,
} from '@/modules/pharmacy/ndps';
import { PHARMACY_PERMISSIONS } from '@/modules/pharmacy/permissions';

type NdpsTab = 'daily' | 'annual' | 'patient' | 'counts';
const TABS: NdpsTab[] = ['daily', 'annual', 'patient', 'counts'];
const DATE = /^\d{4}-\d{2}-\d{2}$/;

interface NdpsSearch {
  tab?: NdpsTab;
  storeId?: string;
  date?: string;
  year?: number;
}

export const Route = createFileRoute('/_app/pharmacy/ndps')({
  validateSearch: (s: Record<string, unknown>): NdpsSearch => ({
    tab: TABS.includes(s.tab as NdpsTab) ? (s.tab as NdpsTab) : undefined,
    storeId: typeof s.storeId === 'string' && s.storeId ? s.storeId : undefined,
    date: typeof s.date === 'string' && DATE.test(s.date) ? s.date : undefined,
    year: typeof s.year === 'number' && Number.isInteger(s.year) && s.year >= 2000 ? s.year : undefined,
  }),
  component: () => (
    <RequirePermission
      permission={[PHARMACY_PERMISSIONS.registers, NDPS_COUNT_PERMISSION]}
      what="the narcotic accounts"
    >
      <NdpsPage />
    </RequirePermission>
  ),
});

/**
 * The NDPS accounts of the essential narcotic drugs (NDPS Rules 52-O to 52-T, as amended in 2015): each store's day
 * (Form 3H), each patient's record (Form 3E), the year's return and next year's estimate (Form 3J), and the blind count
 * by two people at a change of shift.
 */
function NdpsPage() {
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const canRead = usePermission(PHARMACY_PERMISSIONS.registers);
  const canCount = usePermission(NDPS_COUNT_PERMISSION);
  const tabs = TABS.filter((t) => (t === 'counts' ? canCount || canRead : canRead));
  const tab = search.tab && tabs.includes(search.tab) ? search.tab : tabs[0];

  return (
    <Page>
      <PageHeader
        title="Narcotic accounts (NDPS)"
        subtitle="Morphine, fentanyl, methadone, oxycodone, codeine and hydrocodone: the day's account of each store (Form 3H), each patient's record (Form 3E), the annual return and next year's estimate (Form 3J), and the count at each change of shift."
      />
      <TabList selectedValue={tab} onTabSelect={(_, d) => void navigate({ search: { tab: d.value as NdpsTab } })}>
        {tabs.includes('daily') && <Tab value="daily">Daily account (3H)</Tab>}
        {tabs.includes('annual') && <Tab value="annual">Annual return (3J)</Tab>}
        {tabs.includes('patient') && <Tab value="patient">Patient record (3E)</Tab>}
        {tabs.includes('counts') && <Tab value="counts">Shift counts</Tab>}
      </TabList>
      {tab === 'daily' && <DailyTab />}
      {tab === 'annual' && <AnnualTab />}
      {tab === 'patient' && <PatientTab />}
      {tab === 'counts' && <CountsTab canCount={canCount} />}
    </Page>
  );
}

function useDownload(name: string) {
  const notify = useNotify();
  return (blob: Blob) => {
    const url = URL.createObjectURL(blob);
    const link = document.createElement('a');
    link.href = url;
    link.download = name;
    document.body.appendChild(link);
    link.click();
    link.remove();
    setTimeout(() => URL.revokeObjectURL(url), 10_000);
    notify.success('Downloaded', 'The download was recorded in the audit log.');
  };
}

function DailyTab() {
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const notify = useNotify();
  const stores = useQuery(ndpsQueries.stores());
  const storeId = search.storeId ?? stores.data?.[0]?.id ?? null;
  const date = search.date ?? todayIso();
  const account = useQuery({ ...ndpsQueries.daily(storeId ?? '', date), enabled: !!storeId });
  const [confirm, setConfirm] = useState(false);
  const download = useDownload(`ndps-form-3h-${date}.csv`);
  const exportCsv = useMutation({
    mutationFn: () => ndpsApi.exportDaily(storeId!, date),
    onSuccess: (blob) => {
      setConfirm(false);
      download(blob);
    },
    onError: (error) => {
      setConfirm(false);
      notify.error(error, 'Could not download the account');
    },
  });

  return (
    <Section
      flush
      actions={
        <Button
          icon={<ArrowDownloadRegular />}
          disabled={!account.data || exportCsv.isPending}
          onClick={() => setConfirm(true)}
        >
          Download CSV
        </Button>
      }
    >
      <div style={{ padding: '8px 16px 12px' }}>
        <FilterBar>
          <Field label="Store">
            <Select
              value={storeId ?? ''}
              disabled={!stores.data?.length}
              onChange={(_, d) => void navigate({ search: { ...search, storeId: d.value || undefined } })}
            >
              {stores.data?.map((s) => (
                <option key={s.id} value={s.id}>
                  {s.name}
                </option>
              ))}
            </Select>
          </Field>
          <Field label="Date">
            <Input
              type="date"
              value={date}
              max={todayIso()}
              onChange={(_, d) =>
                void navigate({ search: { ...search, date: DATE.test(d.value) ? d.value : undefined } })
              }
            />
          </Field>
        </FilterBar>
        {stores.data && stores.data.length === 0 && (
          <MessageBar intent="info" style={{ marginTop: 8 }}>
            <MessageBarBody>No store has held an essential narcotic drug yet.</MessageBarBody>
          </MessageBar>
        )}
        {account.data?.drugs.some((d) => !d.balanced) && (
          <MessageBar intent="error" style={{ marginTop: 8 }}>
            <MessageBarBody>
              A drug&apos;s closing stock does not agree with the register&apos;s balance. Report it to the pharmacist
              in charge before signing the account.
            </MessageBarBody>
          </MessageBar>
        )}
      </div>
      <DataTable<NdpsDailyDrug>
        ariaLabel="Daily account of essential narcotic drugs"
        rows={account.data?.drugs}
        loading={account.isLoading}
        error={account.error}
        onRetry={() => void account.refetch()}
        getRowId={(d) => d.itemId}
        size="small"
        columns={[
          {
            key: 'drug',
            header: 'Drug',
            render: (d) => (
              <span>
                <Text weight="semibold">{d.itemName}</Text>
                {!d.balanced && (
                  <Badge size="small" appearance="filled" color="danger" style={{ marginLeft: 6 }}>
                    Does not balance
                  </Badge>
                )}
                {d.movements
                  .filter((m) => m.patientUhid)
                  .map((m, i) => (
                    <Caption1 block key={i}>
                      {formatDateTime(m.at)} · {m.patientUhid} · {formatQty(m.out)}
                    </Caption1>
                  ))}
              </span>
            ),
          },
          { key: 'opening', header: 'Opening', width: 100, render: (d) => formatQty(d.opening) },
          { key: 'received', header: 'Received', width: 100, render: (d) => formatQty(d.received + d.otherIn) },
          { key: 'dispensed', header: 'Dispensed', width: 100, render: (d) => formatQty(d.dispensed) },
          { key: 'other', header: 'Other out', width: 100, priority: 'low', render: (d) => formatQty(d.otherOut) },
          { key: 'closing', header: 'Closing', width: 100, render: (d) => formatQty(d.closing) },
        ]}
        emptyTitle="No narcotics in this store"
        emptyDescription="Drugs appear here once the store has received them."
      />
      <ConfirmDialog
        open={confirm}
        title="Download the day's account?"
        confirmLabel="Download"
        busy={exportCsv.isPending}
        onConfirm={() => exportCsv.mutate()}
        onCancel={() => setConfirm(false)}
      >
        The file names the patients the drugs were dispensed to. The download is recorded in the audit log with your
        name.
      </ConfirmDialog>
    </Section>
  );
}

function AnnualTab() {
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const notify = useNotify();
  const lastYear = Number(todayIso().slice(0, 4)) - 1;
  const year = search.year ?? lastYear;
  const report = useQuery(ndpsQueries.annual(year));
  const download = useDownload(`ndps-annual-${year}.csv`);
  const exportCsv = useMutation({
    mutationFn: () => ndpsApi.exportAnnual(year),
    onSuccess: download,
    onError: (error) => notify.error(error, 'Could not download the return'),
  });

  return (
    <Section
      flush
      actions={
        <Button
          icon={<ArrowDownloadRegular />}
          disabled={!report.data || exportCsv.isPending}
          onClick={() => exportCsv.mutate()}
        >
          Download CSV
        </Button>
      }
    >
      <div style={{ padding: '8px 16px 12px' }}>
        <FilterBar>
          <Field label="Year">
            <Input
              type="number"
              value={String(year)}
              min={2000}
              max={lastYear + 1}
              onChange={(_, d) => {
                const value = Number(d.value);
                void navigate({
                  search: { ...search, year: Number.isInteger(value) && value >= 2000 ? value : undefined },
                });
              }}
            />
          </Field>
        </FilterBar>
        <Caption1 block style={{ marginTop: 8 }}>
          January to December, all stores together. The estimate for the next year (Form 3J, due by 30 November) is the
          year&apos;s consumption with a 10% margin; change it on the form if the hospital expects more patients.
        </Caption1>
      </div>
      <DataTable<NdpsAnnualDrug>
        ariaLabel="Annual return of essential narcotic drugs"
        rows={report.data?.drugs}
        loading={report.isLoading}
        error={report.error}
        onRetry={() => void report.refetch()}
        getRowId={(d) => d.itemId}
        size="small"
        columns={[
          {
            key: 'drug',
            header: 'Drug',
            render: (d) => (
              <span>
                <Text weight="semibold">{d.itemName}</Text>
                {!d.balanced && (
                  <Badge size="small" appearance="filled" color="danger" style={{ marginLeft: 6 }}>
                    Does not balance
                  </Badge>
                )}
              </span>
            ),
          },
          { key: 'opening', header: '1 January', width: 100, render: (d) => formatQty(d.opening) },
          { key: 'received', header: 'Received', width: 100, render: (d) => formatQty(d.received) },
          { key: 'consumed', header: 'Consumed', width: 100, render: (d) => formatQty(d.consumed) },
          {
            key: 'written',
            header: 'Written off',
            width: 110,
            priority: 'low',
            render: (d) => formatQty(d.writtenOff),
          },
          { key: 'closing', header: '31 December', width: 110, render: (d) => formatQty(d.closing) },
          { key: 'estimate', header: `Estimate ${year + 1}`, width: 120, render: (d) => formatQty(d.estimateNextYear) },
        ]}
        emptyTitle="No narcotics this year"
        emptyDescription="Drugs appear here once a store has held them."
      />
    </Section>
  );
}

function PatientTab() {
  const [patient, setPatient] = useState<PickedPatient | null>(null);
  const record = useQuery({ ...ndpsQueries.patient(patient?.id ?? ''), enabled: !!patient });

  return (
    <Section>
      <div style={{ maxWidth: 480, marginBottom: 12 }}>
        <PatientPicker value={patient} onChange={setPatient} label="Patient" hint="Kept for two years (rule 52H)." />
      </div>
      {record.data && (
        <Text block style={{ marginBottom: 8 }}>
          {[
            record.data.patientName,
            record.data.uhid,
            record.data.sex,
            record.data.ageYears !== null ? `${record.data.ageYears} years` : null,
            record.data.address,
          ]
            .filter(Boolean)
            .join(' · ')}
        </Text>
      )}
      {patient && (
        <DataTable<NdpsPatientLine>
          ariaLabel="Narcotics dispensed to the patient"
          rows={record.data?.lines}
          loading={record.isLoading}
          error={record.error}
          onRetry={() => void record.refetch()}
          getRowId={(l) => `${l.at}-${l.itemName}-${l.batchNumber}`}
          size="small"
          columns={[
            { key: 'at', header: 'Date', width: 160, render: (l) => formatDateTime(l.at) },
            { key: 'drug', header: 'Drug', render: (l) => `${l.itemName} · ${l.batchNumber}` },
            { key: 'qty', header: 'Quantity', width: 100, render: (l) => formatQty(l.quantity) },
            {
              key: 'prescriber',
              header: 'Prescriber',
              render: (l) => [l.prescriberName, l.prescriberRegistrationNumber].filter(Boolean).join(' · '),
            },
            {
              key: 'rx',
              header: 'Prescription',
              priority: 'low',
              render: (l) => l.prescriptionNumber ?? movementLabel[l.type],
            },
          ]}
          emptyTitle="No narcotics dispensed"
          emptyDescription="Nothing from the essential narcotic drugs was dispensed to this patient."
        />
      )}
    </Section>
  );
}

function CountsTab({ canCount }: { canCount: boolean }) {
  const counts = useQuery(ndpsQueries.counts());
  const [counting, setCounting] = useState(false);
  const [witnessing, setWitnessing] = useState<NarcoticCount | null>(null);

  return (
    <Section
      flush
      actions={
        canCount && (
          <Button appearance="primary" icon={<AddRegular />} onClick={() => setCounting(true)}>
            Count now
          </Button>
        )
      }
    >
      <DataTable<NarcoticCount>
        ariaLabel="Shift counts"
        rows={counts.data}
        loading={counts.isLoading}
        error={counts.error}
        onRetry={() => void counts.refetch()}
        getRowId={(c) => c.id}
        size="small"
        columns={[
          {
            key: 'when',
            header: 'Counted',
            width: 200,
            render: (c) => (
              <span>
                {formatDateTime(c.countedAt)}
                <Caption1 block>
                  {c.storeName} · {c.countedByName}
                </Caption1>
              </span>
            ),
          },
          {
            key: 'lines',
            header: 'Drugs',
            render: (c) => (
              <span>
                {c.lines.map((l) => (
                  <Caption1 block key={l.itemId}>
                    {l.itemName}: {formatQty(l.counted)}
                    {l.difference !== 0 && ` (${signed(l.difference)} against the register)`}
                  </Caption1>
                ))}
                {c.explanation && <Caption1 block>{c.explanation}</Caption1>}
              </span>
            ),
          },
          {
            key: 'status',
            header: 'Status',
            width: 200,
            render: (c) => (
              <span>
                <StatusBadge
                  status={c.status}
                  tone={c.status === 'Witnessed' ? (c.hasDifference ? 'danger' : 'success') : 'warning'}
                  label={c.status === 'Witnessed' ? `Witnessed by ${c.witnessedByName ?? ''}` : 'Waiting for a witness'}
                />
              </span>
            ),
          },
        ]}
        rowActions={(c) =>
          canCount &&
          c.status === 'Counted' && (
            <Button size="small" icon={<CheckmarkRegular />} onClick={() => setWitnessing(c)}>
              Witness
            </Button>
          )
        }
        emptyTitle="No counts yet"
        emptyDescription="Count the narcotics at each change of shift, with a second person to witness the count."
      />
      {counting && <CountDialog onClose={() => setCounting(false)} />}
      {witnessing && <WitnessDialog count={witnessing} onClose={() => setWitnessing(null)} />}
    </Section>
  );
}

/** The blind count: the drugs to count, without the register's balance. */
function CountDialog({ onClose }: { onClose: () => void }) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const stores = useQuery(ndpsQueries.stores());
  const [storeId, setStoreId] = useState<string | null>(null);
  const chosen = storeId ?? stores.data?.[0]?.id ?? null;
  const sheet = useQuery({ ...ndpsQueries.sheet(chosen ?? ''), enabled: !!chosen });
  const [counted, setCounted] = useState<Record<string, string>>({});
  const [tried, setTried] = useState(false);
  const problem = sheet.data ? countProblem(sheet.data.items, counted) : 'Choose the store.';

  const save = useMutation({
    mutationFn: () =>
      ndpsApi.count(
        chosen!,
        sheet.data!.items.map((i) => ({ itemId: i.itemId, counted: Number(counted[i.itemId]) })),
        null,
      ),
    onSuccess: (count) => {
      void queryClient.invalidateQueries({ queryKey: ndpsKeys.counts() });
      notify.success(
        'Count recorded',
        count.hasDifference
          ? 'It differs from the register: the witness explains the difference.'
          : 'Ask a second person to witness it.',
      );
      onClose();
    },
  });

  return (
    <Dialog open onOpenChange={(_, d) => !d.open && !save.isPending && onClose()}>
      <DialogSurface style={{ maxWidth: 560 }}>
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
            <DialogTitle>Count the narcotics</DialogTitle>
            <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
              {save.error && (
                <MessageBar intent="error">
                  <MessageBarBody>{errorMessage(save.error, 'The count could not be recorded.')}</MessageBarBody>
                </MessageBar>
              )}
              <Field label="Store" required>
                <Select value={chosen ?? ''} onChange={(_, d) => setStoreId(d.value || null)}>
                  {stores.data?.map((s) => (
                    <option key={s.id} value={s.id}>
                      {s.name}
                    </option>
                  ))}
                </Select>
              </Field>
              <Caption1>
                Count what is in the cupboard. The register&apos;s balance is compared once the count is saved.
              </Caption1>
              {sheet.data?.items.map((i) => (
                <Field key={i.itemId} label={i.itemName} required>
                  <Input
                    type="number"
                    inputMode="decimal"
                    min={0}
                    value={counted[i.itemId] ?? ''}
                    onChange={(_, d) => setCounted((c) => ({ ...c, [i.itemId]: d.value }))}
                  />
                </Field>
              ))}
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
                Record count
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
      </DialogSurface>
    </Dialog>
  );
}

function WitnessDialog({ count, onClose }: { count: NarcoticCount; onClose: () => void }) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [explanation, setExplanation] = useState(count.explanation ?? '');
  const [tried, setTried] = useState(false);
  const problem = count.hasDifference && !explanation.trim() ? 'Explain the difference from the register.' : null;
  const witness = useMutation({
    mutationFn: () => ndpsApi.witness(count.id, explanation.trim() || null, count.version),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ndpsKeys.counts() });
      notify.success(
        'Count witnessed',
        count.hasDifference ? 'The pharmacist in charge has been told of the difference.' : '',
      );
      onClose();
    },
  });

  return (
    <Dialog open onOpenChange={(_, d) => !d.open && !witness.isPending && onClose()}>
      <DialogSurface style={{ maxWidth: 560 }}>
        <form
          onSubmit={(event) => {
            event.preventDefault();
            setTried(true);
            if (!problem && !witness.isPending) {
              witness.mutate();
            }
          }}
        >
          <DialogBody>
            <DialogTitle>
              Witness the count
              <Caption1 block>
                {count.storeName} · counted by {count.countedByName}, {formatDateTime(count.countedAt)}
              </Caption1>
            </DialogTitle>
            <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
              {witness.error && (
                <MessageBar intent="error">
                  <MessageBarBody>{errorMessage(witness.error, 'The count could not be witnessed.')}</MessageBarBody>
                </MessageBar>
              )}
              {count.lines.map((l) => (
                <Text key={l.itemId}>
                  {l.itemName}: counted {formatQty(l.counted)}, register {formatQty(l.registerBalance)}
                  {l.difference !== 0 && (
                    <Badge size="small" appearance="filled" color="danger" style={{ marginLeft: 6 }}>
                      {signed(l.difference)}
                    </Badge>
                  )}
                </Text>
              ))}
              <Caption1>Count it again yourself before you witness it.</Caption1>
              {count.hasDifference && (
                <Field label="Explanation" required validationMessage={tried ? (problem ?? undefined) : undefined}>
                  <Textarea value={explanation} maxLength={1000} onChange={(_, d) => setExplanation(d.value)} />
                </Field>
              )}
            </DialogContent>
            <DialogActions>
              <Button onClick={onClose} disabled={witness.isPending}>
                Cancel
              </Button>
              <Button
                type="submit"
                appearance="primary"
                disabled={witness.isPending}
                icon={witness.isPending ? <Spinner size="tiny" aria-hidden /> : undefined}
              >
                Witness
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
      </DialogSurface>
    </Dialog>
  );
}
