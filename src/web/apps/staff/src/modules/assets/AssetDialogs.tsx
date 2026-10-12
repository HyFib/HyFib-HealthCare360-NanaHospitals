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
  MessageBar,
  MessageBarBody,
  Select,
  Spinner,
  Switch,
  Textarea,
} from '@fluentui/react-components';
import { formatDateOnly, formatInr, isApiError, todayIso } from '@hc360/core';
import { useNotify, useUnsavedChanges, useValuesChanged } from '@hc360/ui';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useState, type ReactNode } from 'react';
import { addDays, parseNumber } from '@/modules/inventory/format';
import { VendorPicker } from '@/modules/purchase/VendorPicker';
import { assetApi, assetKeys, type AssetContractDto, type PmPlanDto } from './api';
import {
  CALIBRATION_RESULTS,
  calibrationLabel,
  CONTRACT_TYPES,
  contractTypeLabel,
  PM_OUTCOMES,
  pmOutcomeLabel,
  type CalibrationResult,
  type MaintenanceContractType,
  type PmVisitOutcome,
} from './labels';

function FormDialog({
  title,
  children,
  submitLabel,
  valid,
  dirty,
  busy,
  error,
  onSubmit,
  onClose,
  width = 560,
}: {
  title: string;
  children: ReactNode;
  submitLabel: string;
  valid: boolean;
  /** The form has unsaved changes: closing it asks first (SRS UX-08). */
  dirty: boolean;
  busy: boolean;
  error: unknown;
  onSubmit: () => void;
  onClose: () => void;
  width?: number;
}) {
  const guard = useUnsavedChanges({ dirty, busy, onClose });
  return (
    <Dialog open onOpenChange={guard.onOpenChange}>
      <DialogSurface style={{ maxWidth: width, width: '95vw' }}>
        <form
          onSubmit={(event) => {
            event.preventDefault();
            if (valid && !busy) {
              onSubmit();
            }
          }}
        >
          <DialogBody>
            <DialogTitle>{title}</DialogTitle>
            <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
              {children}
              {Boolean(error) && (
                <MessageBar intent="error">
                  <MessageBarBody>{isApiError(error) ? error.userMessage : 'Not saved. Try again.'}</MessageBarBody>
                </MessageBar>
              )}
            </DialogContent>
            <DialogActions>
              <Button onClick={guard.requestClose} disabled={busy}>
                Cancel
              </Button>
              <Button
                type="submit"
                appearance="primary"
                disabled={!valid || busy}
                // Keeps focus while busy (a disabled button drops it); Enter can still submit, which is ignored.
                disabledFocusable={busy}
                icon={busy ? <Spinner size="tiny" /> : undefined}
              >
                {submitLabel}
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
        {guard.dialogs}
      </DialogSurface>
    </Dialog>
  );
}

const grid2 = { display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 12 } as const;

/** Preventive maintenance schedule for one asset. */
export function PmPlanDialog({
  assetId,
  plan,
  onClose,
}: {
  assetId: string;
  plan: PmPlanDto | null;
  onClose: () => void;
}) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [title, setTitle] = useState(plan?.title ?? 'Preventive maintenance');
  const [frequency, setFrequency] = useState(String(plan?.frequencyDays ?? 180));
  const [nextDue, setNextDue] = useState(plan?.nextDueDate ?? addDays(todayIso(), 30));
  const [checklist, setChecklist] = useState(plan?.checklist ?? '');
  const [assignedTo, setAssignedTo] = useState(plan?.assignedTo ?? '');
  const [active, setActive] = useState(plan?.isActive ?? true);
  const dirty = useValuesChanged({ title, frequency, nextDue, checklist, assignedTo, active });
  const days = parseNumber(frequency) ?? 0;
  const valid = title.trim().length > 0 && Number.isInteger(days) && days >= 1 && days <= 3650 && Boolean(nextDue);
  const save = useMutation({
    mutationFn: () =>
      assetApi.savePlan(assetId, plan?.id ?? null, {
        title: title.trim(),
        frequencyDays: days,
        nextDueDate: nextDue,
        checklist: checklist.trim() || null,
        assignedTo: assignedTo.trim() || null,
        vendorId: plan?.vendorId ?? null,
        isActive: active,
        version: plan?.version ?? null,
      }),
    onSuccess: () => {
      notify.success(plan ? 'Maintenance plan saved' : 'Maintenance plan added');
      void queryClient.invalidateQueries({ queryKey: assetKeys.all });
      onClose();
    },
  });
  return (
    <FormDialog
      title={plan ? 'Edit maintenance plan' : 'Add a maintenance plan'}
      submitLabel="Save"
      valid={valid}
      dirty={dirty}
      busy={save.isPending}
      error={save.error}
      onSubmit={() => save.mutate()}
      onClose={onClose}
    >
      <Field label="What is done" required>
        <Input value={title} maxLength={200} onChange={(_, d) => setTitle(d.value)} autoFocus />
      </Field>
      <div style={grid2}>
        <Field label="Every (days)" required hint="e.g. 90 for quarterly, 180 for half-yearly">
          <Input type="number" min={1} max={3650} value={frequency} onChange={(_, d) => setFrequency(d.value)} />
        </Field>
        <Field label="Next due" required>
          <Input type="date" value={nextDue} onChange={(_, d) => setNextDue(d.value)} />
        </Field>
      </div>
      <Field label="Done by" hint="Engineer or vendor service team">
        <Input value={assignedTo} maxLength={120} onChange={(_, d) => setAssignedTo(d.value)} />
      </Field>
      <Field label="Checklist">
        <Textarea
          value={checklist}
          rows={4}
          maxLength={4000}
          onChange={(_, d) => setChecklist(d.value)}
          placeholder="One check per line, e.g. Test alarms, Clean filters, Electrical safety test"
        />
      </Field>
      <Switch checked={active} onChange={(_, d) => setActive(d.checked)} label="Plan is active" />
    </FormDialog>
  );
}

/** A completed (or missed) preventive maintenance visit; the next due date moves on from it. */
export function PmVisitDialog({
  plan,
  onClose,
}: {
  plan: Pick<PmPlanDto, 'id' | 'title' | 'assetName' | 'assetTag' | 'frequencyDays' | 'nextDueDate'>;
  onClose: () => void;
}) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const today = todayIso();
  const [visitDate, setVisitDate] = useState(today);
  const [performedBy, setPerformedBy] = useState('');
  const [vendor, setVendor] = useState<{ id: string; name: string } | null>(null);
  const [outcome, setOutcome] = useState<PmVisitOutcome>('Completed');
  const [findings, setFindings] = useState('');
  const [cost, setCost] = useState('');
  const dirty = useValuesChanged({ visitDate, performedBy, vendor, outcome, findings, cost });
  const costValue = parseNumber(cost) ?? 0;
  const valid = Boolean(visitDate) && visitDate <= today && costValue >= 0;
  const save = useMutation({
    mutationFn: () =>
      assetApi.recordVisit(plan.id, {
        visitDate,
        performedBy: performedBy.trim() || null,
        vendorId: vendor?.id ?? null,
        outcome,
        findings: findings.trim() || null,
        cost: costValue,
        serviceReportFileId: null,
      }),
    onSuccess: () => {
      notify.success(
        'Visit recorded',
        outcome === 'NotDone'
          ? 'The plan stays due.'
          : `Next visit due about ${formatDateOnly(addDays(visitDate, plan.frequencyDays))}.`,
      );
      void queryClient.invalidateQueries({ queryKey: assetKeys.all });
      onClose();
    },
  });
  return (
    <FormDialog
      title={`Record visit: ${plan.title}`}
      submitLabel="Record visit"
      valid={valid}
      dirty={dirty}
      busy={save.isPending}
      error={save.error}
      onSubmit={() => save.mutate()}
      onClose={onClose}
    >
      <Caption1>
        {plan.assetName} ({plan.assetTag}) · due {formatDateOnly(plan.nextDueDate)} · every {plan.frequencyDays} days
      </Caption1>
      <div style={grid2}>
        <Field label="Visit date" required>
          <Input type="date" max={today} value={visitDate} onChange={(_, d) => setVisitDate(d.value)} autoFocus />
        </Field>
        <Field label="Outcome">
          <Select value={outcome} onChange={(_, d) => setOutcome(d.value as PmVisitOutcome)}>
            {PM_OUTCOMES.map((o) => (
              <option key={o} value={o}>
                {pmOutcomeLabel[o]}
              </option>
            ))}
          </Select>
        </Field>
      </div>
      <div style={grid2}>
        <Field label="Done by">
          <Input value={performedBy} maxLength={120} onChange={(_, d) => setPerformedBy(d.value)} />
        </Field>
        <Field label="Cost (₹)">
          <Input type="number" min={0} value={cost} onChange={(_, d) => setCost(d.value)} placeholder="0" />
        </Field>
      </div>
      <VendorPicker
        label="Service vendor (optional)"
        value={vendor}
        onPick={(v) => setVendor({ id: v.id, name: v.name })}
      />
      <Field label="Findings">
        <Textarea
          value={findings}
          rows={3}
          maxLength={2000}
          onChange={(_, d) => setFindings(d.value)}
          placeholder="What was checked, replaced or found"
        />
      </Field>
    </FormDialog>
  );
}

/** Calibration certificate (AST-05). */
export function CalibrationDialog({
  asset,
  onClose,
}: {
  asset: { id: string; name: string; assetTag: string };
  onClose: () => void;
}) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const today = todayIso();
  const [calibratedOn, setCalibratedOn] = useState(today);
  const [agency, setAgency] = useState('');
  const [certificate, setCertificate] = useState('');
  const [nextDue, setNextDue] = useState(addDays(today, 365));
  const [outcome, setOutcome] = useState<CalibrationResult>('Pass');
  const [remarks, setRemarks] = useState('');
  const dirty = useValuesChanged({ calibratedOn, agency, certificate, nextDue, outcome, remarks });
  const valid =
    Boolean(calibratedOn) &&
    calibratedOn <= today &&
    agency.trim().length > 0 &&
    Boolean(nextDue) &&
    nextDue > calibratedOn;
  const save = useMutation({
    mutationFn: () =>
      assetApi.recordCalibration(asset.id, {
        calibratedOn,
        agency: agency.trim(),
        certificateNumber: certificate.trim() || null,
        certificateFileId: null,
        nextDueDate: nextDue,
        outcome,
        remarks: remarks.trim() || null,
      }),
    onSuccess: () => {
      notify.success(
        'Calibration recorded',
        outcome === 'Fail'
          ? 'The equipment failed calibration: take it out of use until it is fixed.'
          : `Next due ${formatDateOnly(nextDue)}.`,
      );
      void queryClient.invalidateQueries({ queryKey: assetKeys.all });
      onClose();
    },
  });
  return (
    <FormDialog
      title={`Record calibration: ${asset.name}`}
      submitLabel="Record"
      valid={valid}
      dirty={dirty}
      busy={save.isPending}
      error={save.error}
      onSubmit={() => save.mutate()}
      onClose={onClose}
    >
      <Caption1>{asset.assetTag}</Caption1>
      <div style={grid2}>
        <Field label="Calibrated on" required>
          <Input type="date" max={today} value={calibratedOn} onChange={(_, d) => setCalibratedOn(d.value)} autoFocus />
        </Field>
        <Field label="Result">
          <Select value={outcome} onChange={(_, d) => setOutcome(d.value as CalibrationResult)}>
            {CALIBRATION_RESULTS.map((r) => (
              <option key={r} value={r}>
                {calibrationLabel[r]}
              </option>
            ))}
          </Select>
        </Field>
      </div>
      <div style={grid2}>
        <Field label="Agency" required hint="NABL-accredited lab or vendor">
          <Input value={agency} maxLength={200} onChange={(_, d) => setAgency(d.value)} />
        </Field>
        <Field label="Certificate number">
          <Input value={certificate} maxLength={64} onChange={(_, d) => setCertificate(d.value)} />
        </Field>
      </div>
      <Field
        label="Next due"
        required
        validationMessage={
          nextDue && calibratedOn && nextDue <= calibratedOn ? 'Must be after the calibration date' : undefined
        }
      >
        <Input
          type="date"
          min={addDays(calibratedOn || today, 1)}
          value={nextDue}
          onChange={(_, d) => setNextDue(d.value)}
        />
      </Field>
      <Field label="Remarks">
        <Textarea value={remarks} rows={2} maxLength={1000} onChange={(_, d) => setRemarks(d.value)} />
      </Field>
    </FormDialog>
  );
}

/** AMC (labour and visits) or CMC (includes parts) contract; warranty is recorded on the asset itself. */
export function ContractDialog({
  assetId,
  contract,
  onClose,
}: {
  assetId: string;
  contract: AssetContractDto | null;
  onClose: () => void;
}) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const today = todayIso();
  const [type, setType] = useState<MaintenanceContractType>(contract?.type ?? 'Amc');
  const [vendor, setVendor] = useState<{ id: string; name: string } | null>(
    contract?.vendorId ? { id: contract.vendorId, name: contract.vendorName ?? 'Vendor' } : null,
  );
  const [number, setNumber] = useState(contract?.contractNumber ?? '');
  const [start, setStart] = useState(contract?.startDate ?? today);
  const [end, setEnd] = useState(contract?.endDate ?? addDays(today, 364));
  const [cost, setCost] = useState(contract ? String(contract.cost) : '');
  const [coverage, setCoverage] = useState(contract?.coverageNotes ?? '');
  const dirty = useValuesChanged({ type, vendor, number, start, end, cost, coverage });
  const costValue = parseNumber(cost) ?? 0;
  const valid = Boolean(start) && Boolean(end) && end > start && costValue >= 0;
  const save = useMutation({
    mutationFn: () =>
      assetApi.saveContract(assetId, contract?.id ?? null, {
        type,
        vendorId: vendor?.id ?? null,
        contractNumber: number.trim() || null,
        startDate: start,
        endDate: end,
        cost: costValue,
        coverageNotes: coverage.trim() || null,
        version: contract?.version ?? null,
      }),
    onSuccess: () => {
      notify.success(
        contract ? 'Contract saved' : 'Contract added',
        `${contractTypeLabel[type]} until ${formatDateOnly(end)} · ${formatInr(costValue)}`,
      );
      void queryClient.invalidateQueries({ queryKey: assetKeys.all });
      onClose();
    },
  });
  return (
    <FormDialog
      title={contract ? 'Edit maintenance contract' : 'Add maintenance contract'}
      submitLabel="Save"
      valid={valid}
      dirty={dirty}
      busy={save.isPending}
      error={save.error}
      onSubmit={() => save.mutate()}
      onClose={onClose}
    >
      <Field label="Type">
        <Select value={type} onChange={(_, d) => setType(d.value as MaintenanceContractType)}>
          {CONTRACT_TYPES.map((t) => (
            <option key={t} value={t}>
              {contractTypeLabel[t]}
            </option>
          ))}
        </Select>
      </Field>
      <VendorPicker label="Service vendor" value={vendor} onPick={(v) => setVendor({ id: v.id, name: v.name })} />
      <div style={grid2}>
        <Field label="Contract number">
          <Input value={number} maxLength={64} onChange={(_, d) => setNumber(d.value)} />
        </Field>
        <Field label="Cost (₹)">
          <Input type="number" min={0} value={cost} onChange={(_, d) => setCost(d.value)} />
        </Field>
        <Field label="Starts" required>
          <Input type="date" value={start} onChange={(_, d) => setStart(d.value)} />
        </Field>
        <Field
          label="Ends"
          required
          validationMessage={end && start && end <= start ? 'Must end after it starts' : undefined}
        >
          <Input type="date" min={addDays(start || today, 1)} value={end} onChange={(_, d) => setEnd(d.value)} />
        </Field>
      </div>
      <Field label="What it covers">
        <Textarea
          value={coverage}
          rows={3}
          maxLength={1000}
          onChange={(_, d) => setCoverage(d.value)}
          placeholder="e.g. 4 PM visits a year, breakdown calls within 24 hours, parts excluded"
        />
      </Field>
    </FormDialog>
  );
}

/** Disposal of a condemned asset (auction, buy-back, e-waste recycler). */
export function DisposeDialog({
  asset,
  onClose,
}: {
  asset: { id: string; name: string; assetTag: string; bookValue?: number | null };
  onClose: () => void;
}) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const today = todayIso();
  const [date, setDate] = useState(today);
  const [value, setValue] = useState('');
  const [method, setMethod] = useState('');
  const dirty = useValuesChanged({ date, value, method });
  const amount = parseNumber(value) ?? 0;
  const valid = Boolean(date) && amount >= 0 && method.trim().length > 0;
  const dispose = useMutation({
    mutationFn: () => assetApi.dispose(asset.id, { disposedOn: date, disposalValue: amount, method: method.trim() }),
    onSuccess: () => {
      notify.success(`${asset.name} disposed`, 'It leaves the asset register.');
      void queryClient.invalidateQueries({ queryKey: assetKeys.all });
      onClose();
    },
  });
  return (
    <FormDialog
      title={`Dispose of ${asset.name}?`}
      submitLabel="Record disposal"
      valid={valid}
      dirty={dirty}
      busy={dispose.isPending}
      error={dispose.error}
      onSubmit={() => dispose.mutate()}
      onClose={onClose}
    >
      <Caption1>
        {asset.assetTag}
        {asset.bookValue !== undefined && asset.bookValue !== null ? ` · book value ${formatInr(asset.bookValue)}` : ''}
        . Only condemned assets can be disposed of.
      </Caption1>
      <div style={grid2}>
        <Field label="Disposed on" required>
          <Input type="date" max={today} value={date} onChange={(_, d) => setDate(d.value)} autoFocus />
        </Field>
        <Field label="Amount received (₹)">
          <Input type="number" min={0} value={value} onChange={(_, d) => setValue(d.value)} placeholder="0" />
        </Field>
      </div>
      <Field label="How" required>
        <Input
          value={method}
          maxLength={100}
          onChange={(_, d) => setMethod(d.value)}
          placeholder="e.g. Auction, buy-back, authorised e-waste recycler"
        />
      </Field>
    </FormDialog>
  );
}
