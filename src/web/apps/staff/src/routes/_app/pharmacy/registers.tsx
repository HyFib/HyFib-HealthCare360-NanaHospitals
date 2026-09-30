import {
  Button,
  Caption1,
  Field,
  Input,
  MessageBar,
  MessageBarBody,
  Select,
  Tab,
  TabList,
  Text,
  tokens,
  Tooltip,
} from '@fluentui/react-components';
import { ArrowDownloadRegular, DismissRegular, ShieldKeyholeRegular } from '@fluentui/react-icons';
import { formatDateTime, todayIso } from '@hc360/core';
import { ConfirmDialog, DataTable, FilterBar, Page, PageHeader, Section, useNotify } from '@hc360/ui';
import { useMutation, useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { daysBetween, formatQty } from '@/modules/inventory/format';
import { ItemPicker } from '@/modules/inventory/ItemPicker';
import { movementLabel, type DrugSchedule } from '@/modules/inventory/labels';
import { CATALOG_READERS } from '@/modules/inventory/permissions';
import { StorePicker } from '@/modules/inventory/StorePicker';
import { ExpiryText, ScheduleBadge } from '@/modules/inventory/StockBits';
import { useWorkingStore } from '@/modules/inventory/stores';
import {
  pharmacyApi,
  pharmacyQueries,
  type ControlledRegisterRow,
  type RegisterFilters,
  type ScheduleHRegisterRow,
} from '@/modules/pharmacy/api';

type RegisterTab = 'controlled' | 'schedule-h';
const DATE = /^\d{4}-\d{2}-\d{2}$/;

interface RegisterSearch extends RegisterFilters {
  tab?: RegisterTab;
  itemName?: string;
}

export const Route = createFileRoute('/_app/pharmacy/registers')({
  validateSearch: (s: Record<string, unknown>): RegisterSearch => ({
    tab: s.tab === 'schedule-h' ? 'schedule-h' : undefined,
    storeId: typeof s.storeId === 'string' && s.storeId ? s.storeId : undefined,
    itemId: typeof s.itemId === 'string' && s.itemId ? s.itemId : undefined,
    itemName: typeof s.itemName === 'string' && s.itemName ? s.itemName : undefined,
    schedule: s.schedule === 'H1' || s.schedule === 'X' ? (s.schedule as DrugSchedule) : undefined,
    from: typeof s.from === 'string' && DATE.test(s.from) ? s.from : undefined,
    to: typeof s.to === 'string' && DATE.test(s.to) ? s.to : undefined,
    page: typeof s.page === 'number' && s.page > 1 ? s.page : undefined,
  }),
  component: RegistersPage,
});

const MAX_EXPORT_DAYS = 366;

/** Statutory registers (Drugs & Cosmetics Rules, NDPS): the controlled-drug register with running balances and the Schedule H register. */
function RegistersPage() {
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const notify = useNotify();
  const canPickItem = usePermission(CATALOG_READERS);
  const working = useWorkingStore('pharmacy-op', { type: 'Pharmacy' });
  const [confirmExport, setConfirmExport] = useState(false);
  const tab: RegisterTab = search.tab ?? 'controlled';
  const today = todayIso();
  const from = search.from ?? `${today.slice(0, 8)}01`;
  const to = search.to ?? today;
  const filters: RegisterFilters = {
    storeId: search.storeId,
    itemId: search.itemId,
    schedule: search.schedule,
    from,
    to,
    page: search.page,
  };
  const controlled = useQuery({ ...pharmacyQueries.controlledRegister(filters), enabled: tab === 'controlled' });
  const scheduleH = useQuery({ ...pharmacyQueries.scheduleHRegister(filters), enabled: tab === 'schedule-h' });
  const set = (patch: Partial<RegisterSearch>) =>
    void navigate({ search: (prev) => ({ ...prev, ...patch, page: undefined }) });
  const rangeDays = daysBetween(from, to);
  const tooLong = rangeDays > MAX_EXPORT_DAYS;

  const exportCsv = useMutation({
    mutationFn: () => pharmacyApi.exportRegister(tab, filters),
    onSuccess: (blob) => {
      const url = URL.createObjectURL(blob);
      const link = document.createElement('a');
      link.href = url;
      link.download = `${tab === 'controlled' ? 'controlled-drug-register' : 'schedule-h-register'}-${from.replace(/-/g, '')}-${to.replace(/-/g, '')}.csv`;
      document.body.appendChild(link);
      link.click();
      link.remove();
      setTimeout(() => URL.revokeObjectURL(url), 10_000);
      setConfirmExport(false);
      notify.success('Register downloaded', 'The download was recorded in the audit log.');
    },
    onError: (error) => {
      setConfirmExport(false);
      notify.error(error, 'Could not download the register');
    },
  });

  return (
    <Page>
      <PageHeader
        title="Drug registers"
        subtitle="Statutory records kept automatically from every sale, issue, receipt and adjustment. Read-only."
        actions={
          <Tooltip
            content={tooLong ? 'Download at most one year at a time' : 'CSV for inspections (opens in Excel)'}
            relationship="description"
          >
            <Button
              appearance="primary"
              icon={<ArrowDownloadRegular />}
              disabled={tooLong || exportCsv.isPending}
              onClick={() => setConfirmExport(true)}
            >
              Download CSV
            </Button>
          </Tooltip>
        }
      />
      <Section flush>
        <div style={{ padding: '0 8px' }}>
          <TabList
            selectedValue={tab}
            onTabSelect={(_, d) => set({ tab: d.value === 'schedule-h' ? 'schedule-h' : undefined })}
          >
            <Tab value="controlled" icon={<ShieldKeyholeRegular />}>
              Controlled drugs (H1, X, NDPS)
            </Tab>
            <Tab value="schedule-h">Schedule H supplies</Tab>
          </TabList>
        </div>
        <div style={{ padding: '8px 16px 12px' }}>
          <FilterBar>
            <Field label="From">
              <Input type="date" value={from} max={to} onChange={(_, d) => set({ from: d.value || undefined })} />
            </Field>
            <Field label="To">
              <Input
                type="date"
                value={to}
                min={from}
                max={today}
                onChange={(_, d) => set({ to: d.value || undefined })}
              />
            </Field>
            {working.stores.length > 0 && (
              <StorePicker
                stores={working.stores}
                value={search.storeId ?? null}
                onChange={(storeId) => set({ storeId: storeId ?? undefined })}
                allowAll
                allLabel="All pharmacies"
                label="Pharmacy"
              />
            )}
            {tab === 'controlled' && (
              <>
                <Field label="Schedule">
                  <Select
                    value={search.schedule ?? ''}
                    onChange={(_, d) => set({ schedule: (d.value || undefined) as DrugSchedule | undefined })}
                  >
                    <option value="">All controlled</option>
                    <option value="H1">Schedule H1</option>
                    <option value="X">Schedule X</option>
                  </Select>
                </Field>
                {canPickItem && !search.itemId && (
                  <div style={{ minWidth: 260 }}>
                    <ItemPicker
                      label="Drug"
                      placeholder="All drugs, or search one"
                      showStock={false}
                      onPick={(item) => set({ itemId: item.id, itemName: item.name })}
                    />
                  </div>
                )}
                {search.itemId && (
                  <Field label="Drug">
                    <Button
                      icon={<DismissRegular />}
                      iconPosition="after"
                      onClick={() => set({ itemId: undefined, itemName: undefined })}
                    >
                      {search.itemName ?? 'Selected drug'}
                    </Button>
                  </Field>
                )}
              </>
            )}
          </FilterBar>
          {tooLong && (
            <MessageBar intent="warning" style={{ marginTop: 8 }}>
              <MessageBarBody>The period is longer than a year. Narrow it to download the register.</MessageBarBody>
            </MessageBar>
          )}
        </div>
        {tab === 'controlled' ? (
          <DataTable<ControlledRegisterRow>
            ariaLabel="Controlled-drug register"
            rows={controlled.data?.items}
            loading={controlled.isLoading}
            error={controlled.error}
            onRetry={() => void controlled.refetch()}
            getRowId={(r) => r.id}
            size="small"
            columns={[
              { key: 'when', header: 'Date and time', width: 160, render: (r) => formatDateTime(r.occurredAt) },
              {
                key: 'drug',
                header: 'Drug',
                render: (r) => (
                  <span>
                    <Text weight="semibold">{r.itemName}</Text>{' '}
                    <ScheduleBadge schedule={r.schedule} isControlled={r.isControlled} />
                    <Caption1 block style={{ color: tokens.colorNeutralForeground3 }}>
                      {r.batchNumber} · <ExpiryText date={r.expiryDate} />
                    </Caption1>
                  </span>
                ),
              },
              {
                key: 'movement',
                header: 'Movement',
                priority: 'low',
                render: (r) => movementLabel[r.movementType] ?? r.movementType,
              },
              { key: 'in', header: 'In', align: 'end', render: (r) => (r.quantityIn ? formatQty(r.quantityIn) : '') },
              {
                key: 'out',
                header: 'Out',
                align: 'end',
                render: (r) => (r.quantityOut ? formatQty(r.quantityOut) : ''),
              },
              {
                key: 'balance',
                header: 'Balance',
                align: 'end',
                render: (r) => <Text weight="semibold">{formatQty(r.balanceAfter)}</Text>,
              },
              {
                key: 'patient',
                header: 'Patient',
                render: (r) =>
                  r.patientName ? (
                    <Tooltip content={r.patientAddress ?? 'Address not recorded'} relationship="description">
                      <span>
                        {r.patientName}
                        {r.patientUhid && <Caption1 block>{r.patientUhid}</Caption1>}
                      </span>
                    </Tooltip>
                  ) : (
                    '—'
                  ),
              },
              {
                key: 'prescriber',
                header: 'Prescriber',
                priority: 'low',
                render: (r) =>
                  r.prescriberName ? (
                    <span>
                      {r.prescriberName}
                      {r.prescriberRegistrationNumber && (
                        <Caption1 block>Reg. {r.prescriberRegistrationNumber}</Caption1>
                      )}
                    </span>
                  ) : (
                    '—'
                  ),
              },
              {
                key: 'doc',
                header: 'Document',
                priority: 'low',
                render: (r) => (
                  <span>
                    {r.referenceNumber ?? '—'}
                    {r.prescriptionNumber && <Caption1 block>Rx {r.prescriptionNumber}</Caption1>}
                  </span>
                ),
              },
              { key: 'by', header: 'Recorded by', priority: 'low', render: (r) => r.recordedBy ?? '—' },
            ]}
            emptyTitle="No controlled-drug movements in this period"
            emptyDescription="Every receipt, sale, issue, return, transfer and adjustment of H1, X and NDPS medicines is written here automatically."
            page={search.page ?? 1}
            pageSize={50}
            totalCount={controlled.data?.totalCount}
            onPageChange={(p) => void navigate({ search: (prev) => ({ ...prev, page: p > 1 ? p : undefined }) })}
          />
        ) : (
          <DataTable<ScheduleHRegisterRow>
            ariaLabel="Schedule H register"
            rows={scheduleH.data?.items}
            loading={scheduleH.isLoading}
            error={scheduleH.error}
            onRetry={() => void scheduleH.refetch()}
            getRowId={(r) => `${r.documentNumber}-${r.itemName}-${r.batchNumber}-${r.issuedAt}`}
            size="small"
            columns={[
              { key: 'when', header: 'Date and time', width: 160, render: (r) => formatDateTime(r.issuedAt) },
              {
                key: 'doc',
                header: 'Document',
                render: (r) => (
                  <span>
                    <Text font="monospace">{r.documentNumber}</Text>
                    <Caption1 block>{r.source}</Caption1>
                  </span>
                ),
              },
              { key: 'patient', header: 'Patient', render: (r) => r.patientName ?? '—' },
              { key: 'prescriber', header: 'Prescriber', priority: 'low', render: (r) => r.prescriberName ?? '—' },
              { key: 'rx', header: 'Prescription', priority: 'low', render: (r) => r.prescriptionNumber ?? '—' },
              { key: 'drug', header: 'Drug', render: (r) => <Text weight="semibold">{r.itemName}</Text> },
              {
                key: 'batch',
                header: 'Batch',
                priority: 'low',
                render: (r) => <Text font="monospace">{r.batchNumber}</Text>,
              },
              { key: 'qty', header: 'Qty', align: 'end', render: (r) => formatQty(r.quantity) },
            ]}
            emptyTitle="No Schedule H supplies in this period"
            emptyDescription="Schedule H medicines sold at the counters or issued to the wards appear here. H1 medicines are in the controlled register."
            page={search.page ?? 1}
            pageSize={50}
            totalCount={scheduleH.data?.totalCount}
            onPageChange={(p) => void navigate({ search: (prev) => ({ ...prev, page: p > 1 ? p : undefined }) })}
          />
        )}
      </Section>
      <ConfirmDialog
        open={confirmExport}
        title="Download the register?"
        confirmLabel="Download"
        busy={exportCsv.isPending}
        onConfirm={() => exportCsv.mutate()}
        onCancel={() => setConfirmExport(false)}
      >
        The file contains patient names, UHIDs and addresses. The download is recorded in the audit log with your name.
        Keep it on a secure computer and delete it once the inspection is over.
      </ConfirmDialog>
    </Page>
  );
}
