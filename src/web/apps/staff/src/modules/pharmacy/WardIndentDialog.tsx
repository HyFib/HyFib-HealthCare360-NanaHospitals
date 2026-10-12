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
  Input,
  makeStyles,
  MessageBar,
  MessageBarBody,
  Radio,
  RadioGroup,
  Spinner,
  Text,
  Textarea,
  tokens,
} from '@fluentui/react-components';
import { DeleteRegular, PersonRegular } from '@fluentui/react-icons';
import { isApiError, type PagedResult } from '@hc360/core';
import { EmptyState, useNotify, useUnsavedChanges, useValuesChanged } from '@hc360/ui';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { api } from '@/lib/api';
import { usePermission } from '@/lib/permissions';
import type { ItemLookup } from '@/modules/inventory/api';
import { formatQty, parseNumber } from '@/modules/inventory/format';
import { ItemPicker, SearchPicker } from '@/modules/inventory/ItemPicker';
import { LineTable } from '@/modules/inventory/LineTable';
import { CATALOG_READERS } from '@/modules/inventory/permissions';
import { ItemFlags } from '@/modules/inventory/StockBits';
import { pharmacyApi, pharmacyKeys, type AdmissionOption, type IndentUrgency, type PharmacyStockRow } from './api';
import { MedicineSearch } from './MedicineSearch';
import { COUNTER_STAFF } from './permissions';

const useStyles = makeStyles({
  patient: {
    display: 'flex',
    gap: '10px',
    alignItems: 'center',
    padding: '8px 12px',
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground2,
  },
  grid: { display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(180px, 1fr))', gap: '12px' },
  muted: { color: tokens.colorNeutralForeground3 },
});

interface IndentLine {
  itemId: string;
  name: string;
  unit: string;
  schedule: ItemLookup['schedule'] | null;
  isControlled: boolean;
  isColdChain: boolean;
  available: number | null;
  quantity: string;
  remarks: string;
}

/**
 * Nurse's request to the IP pharmacy for one admitted patient (PH-03). STAT indents go to the top of the IP counter
 * list. Medicines issued against it are charged to the patient's IP account at MRP.
 */
export function WardIndentDialog({
  ipStoreId,
  onClose,
  onRaised,
}: {
  ipStoreId: string | null;
  onClose: () => void;
  onRaised?: (id: string) => void;
}) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const canUseCatalog = usePermission(CATALOG_READERS);
  const isCounterStaff = usePermission(COUNTER_STAFF);
  const canReadAdmissions = usePermission('inpatient.admissions.read');
  const [admission, setAdmission] = useState<AdmissionOption | null>(null);
  const [wardName, setWardName] = useState('');
  const [bedLabel, setBedLabel] = useState('');
  const [urgency, setUrgency] = useState<IndentUrgency>('Routine');
  const [remarks, setRemarks] = useState('');
  const [lines, setLines] = useState<IndentLine[]>([]);

  const addLine = (line: Omit<IndentLine, 'quantity' | 'remarks'>) => {
    if (lines.some((l) => l.itemId === line.itemId)) {
      notify.info(`${line.name} is already on this indent`, 'Change its quantity instead.');
      return;
    }
    setLines([...lines, { ...line, quantity: '', remarks: '' }]);
  };
  const update = (itemId: string, patch: Partial<IndentLine>) =>
    setLines(lines.map((l) => (l.itemId === itemId ? { ...l, ...patch } : l)));

  const pickAdmission = (a: AdmissionOption) => {
    setAdmission(a);
    setWardName(a.wardName ?? '');
    setBedLabel(a.bedLabel ?? '');
  };

  const invalidLines = lines.filter((l) => !((parseNumber(l.quantity) ?? 0) > 0));
  const valid = admission !== null && lines.length > 0 && invalidLines.length === 0;

  const raise = useMutation({
    mutationFn: () =>
      pharmacyApi.raiseWardIndent({
        patientId: admission!.patientId,
        admissionId: admission!.id,
        visitId: null,
        wardName: wardName.trim() || null,
        bedLabel: bedLabel.trim() || null,
        urgency,
        prescriptionId: null,
        remarks: remarks.trim() || null,
        lines: lines.map((l) => ({
          itemId: l.itemId,
          quantity: parseNumber(l.quantity) ?? 0,
          remarks: l.remarks.trim() || null,
        })),
      }),
    onSuccess: (result) => {
      notify.success(
        `Ward indent ${result.number} sent to the IP pharmacy`,
        urgency === 'Stat' ? 'Marked STAT: it goes to the top of the list.' : undefined,
      );
      void queryClient.invalidateQueries({ queryKey: pharmacyKeys.ward });
      void queryClient.invalidateQueries({ queryKey: pharmacyKeys.dashboard });
      onRaised?.(result.id);
      onClose();
    },
  });

  const guard = useUnsavedChanges({
    dirty: useValuesChanged({ admission, wardName, bedLabel, urgency, remarks, lines }),
    busy: raise.isPending,
    onClose,
  });

  return (
    <Dialog open onOpenChange={guard.onOpenChange}>
      <DialogSurface style={{ maxWidth: 900, width: '96vw' }}>
        <DialogBody>
          <DialogTitle>Raise ward indent</DialogTitle>
          <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 14 }}>
            {!canReadAdmissions ? (
              <MessageBar intent="warning">
                <MessageBarBody>
                  Your account cannot look up admitted patients. Ask the administrator for access to admissions.
                </MessageBarBody>
              </MessageBar>
            ) : admission ? (
              <div className={styles.patient}>
                <PersonRegular fontSize={24} />
                <div style={{ flex: 1 }}>
                  <Text weight="semibold">{admission.patientName}</Text>
                  <Caption1 block>
                    {admission.uhid} · {admission.age ?? '—'} / {admission.gender.charAt(0)} · Admission{' '}
                    {admission.admissionNumber}
                    {admission.consultantName ? ` · ${admission.consultantName}` : ''}
                  </Caption1>
                </div>
                <Button size="small" onClick={() => setAdmission(null)}>
                  Change patient
                </Button>
              </div>
            ) : (
              <SearchPicker<AdmissionOption>
                label="Admitted patient"
                required
                placeholder="Name, UHID, mobile or admission number"
                queryKey={['pharmacy', 'admissions', 'picker']}
                fetch={(search, signal) =>
                  api
                    .get<PagedResult<AdmissionOption>>('/api/v1/inpatient/admissions', {
                      query: { search, activeOnly: true, pageSize: 10 },
                      signal,
                    })
                    .then((r) => r.items)
                }
                minLength={3}
                getKey={(a) => a.id}
                onPick={pickAdmission}
                autoFocus
                emptyText="No admitted patient matches."
                renderOption={(a) => (
                  <div style={{ display: 'flex', flexDirection: 'column' }}>
                    <Text weight="semibold">
                      {a.patientName} · {a.uhid}
                    </Text>
                    <Caption1>
                      {[a.wardName, a.bedLabel].filter(Boolean).join(' / ') || 'No bed'} · Admission {a.admissionNumber}
                    </Caption1>
                  </div>
                )}
              />
            )}

            <div className={styles.grid}>
              <Field label="Ward">
                <Input value={wardName} maxLength={100} onChange={(_, d) => setWardName(d.value)} />
              </Field>
              <Field label="Bed">
                <Input value={bedLabel} maxLength={32} onChange={(_, d) => setBedLabel(d.value)} />
              </Field>
              <Field label="Urgency">
                <RadioGroup
                  layout="horizontal"
                  value={urgency}
                  onChange={(_, d) => setUrgency(d.value as IndentUrgency)}
                >
                  <Radio value="Routine" label="Routine" />
                  <Radio value="Stat" label="STAT (needed now)" />
                </RadioGroup>
              </Field>
            </div>

            {canUseCatalog ? (
              <ItemPicker
                storeId={ipStoreId}
                label="Add medicine or consumable"
                onPick={(item) =>
                  addLine({
                    itemId: item.id,
                    name: item.name,
                    unit: item.baseUnit,
                    schedule: item.schedule,
                    isControlled: item.isControlled,
                    isColdChain: item.isColdChain,
                    available: item.available,
                  })
                }
              />
            ) : isCounterStaff && ipStoreId ? (
              <MedicineSearch
                storeId={ipStoreId}
                label="Add medicine or consumable"
                onPick={(row: PharmacyStockRow) =>
                  addLine({
                    itemId: row.itemId,
                    name: row.name,
                    unit: row.baseUnit,
                    schedule: row.schedule,
                    isControlled: row.isControlled,
                    isColdChain: row.isColdChain,
                    available: row.available,
                  })
                }
              />
            ) : (
              <MessageBar intent="warning">
                <MessageBarBody>
                  Your account cannot search the item master. Ask the administrator for the permission to raise store
                  indents.
                </MessageBarBody>
              </MessageBar>
            )}

            <LineTable<IndentLine>
              ariaLabel="Indent lines"
              rows={lines}
              getRowId={(l) => l.itemId}
              empty={
                <EmptyState
                  title="No items yet"
                  description="Search above by name or scan the barcode to add what the patient needs."
                />
              }
              columns={[
                {
                  key: 'item',
                  header: 'Item',
                  render: (l) => (
                    <span>
                      <Text weight="semibold">{l.name}</Text>{' '}
                      <ItemFlags schedule={l.schedule} isControlled={l.isControlled} isColdChain={l.isColdChain} />
                      {l.available !== null && (
                        <Caption1 block className={styles.muted}>
                          {l.available > 0
                            ? `${formatQty(l.available, l.unit)} in the IP pharmacy`
                            : 'Out of stock in the IP pharmacy'}
                        </Caption1>
                      )}
                    </span>
                  ),
                },
                {
                  key: 'qty',
                  header: 'Quantity',
                  width: 140,
                  render: (l) => (
                    <Input
                      type="number"
                      min={0}
                      aria-label={`Quantity of ${l.name}`}
                      contentAfter={<Caption1>{l.unit}</Caption1>}
                      value={l.quantity}
                      onChange={(_, d) => update(l.itemId, { quantity: d.value })}
                    />
                  ),
                },
                {
                  key: 'remarks',
                  header: 'Note for the pharmacist',
                  priority: 'low',
                  render: (l) => (
                    <Input
                      value={l.remarks}
                      maxLength={300}
                      aria-label={`Note for ${l.name}`}
                      onChange={(_, d) => update(l.itemId, { remarks: d.value })}
                    />
                  ),
                },
                {
                  key: 'remove',
                  header: '',
                  width: 48,
                  render: (l) => (
                    <Button
                      appearance="subtle"
                      icon={<DeleteRegular />}
                      aria-label={`Remove ${l.name}`}
                      onClick={() => setLines(lines.filter((x) => x.itemId !== l.itemId))}
                    />
                  ),
                },
              ]}
            />

            <Field label="Remarks">
              <Textarea
                value={remarks}
                maxLength={1000}
                rows={2}
                onChange={(_, d) => setRemarks(d.value)}
                placeholder="e.g. Post-operative, needs before 6 pm"
              />
            </Field>

            {raise.error && (
              <MessageBar intent="error">
                <MessageBarBody>
                  {isApiError(raise.error) ? raise.error.userMessage : 'Could not raise the indent.'}
                </MessageBarBody>
              </MessageBar>
            )}
          </DialogContent>
          <DialogActions>
            <Button onClick={guard.requestClose} disabled={raise.isPending}>
              Cancel
            </Button>
            <Button
              appearance="primary"
              disabled={!valid || raise.isPending}
              icon={raise.isPending ? <Spinner size="tiny" /> : undefined}
              onClick={() => raise.mutate()}
            >
              {urgency === 'Stat' ? 'Send STAT indent' : 'Send indent'}
            </Button>
          </DialogActions>
        </DialogBody>
        {guard.dialogs}
      </DialogSurface>
    </Dialog>
  );
}
