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
  makeStyles,
  Menu,
  MenuItem,
  MenuList,
  MenuPopover,
  MenuTrigger,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  Spinner,
  Switch,
  Text,
  Textarea,
  tokens,
  Tooltip,
  useRestoreFocusTarget,
} from '@fluentui/react-components';
import {
  ArrowRepeatAllRegular,
  ArrowSwapRegular,
  CheckmarkCircleRegular,
  DismissCircleRegular,
  MoreHorizontalRegular,
  PrintRegular,
  ShieldKeyholeRegular,
  TagRegular,
  WarningRegular,
} from '@fluentui/react-icons';
import {
  formatDateOnly,
  formatDateTime,
  formatInr,
  formatRelative,
  isApiError,
  todayIso,
  useHotkey,
  useRealtimeEvent,
  useSession,
} from '@hc360/core';
import { EmptyState, ErrorState, LoadingState, Page, Section, StatusBadge, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { useLetterhead } from '@/lib/letterhead';
import { usePermission } from '@/lib/permissions';
import { PatientBannerWithFlags } from '@/modules/emr/PatientBannerWithFlags';
import { sumNumbers } from '@/modules/inventory/decimal';
import type { BatchStock } from '@/modules/inventory/api';
import { dispensingShelfLifeDays, usableQuantity } from '@/modules/inventory/fefo';
import { addDays, formatQty, parseNumber } from '@/modules/inventory/format';
import { workflowTone, type DrugSchedule } from '@/modules/inventory/labels';
import { LineTable } from '@/modules/inventory/LineTable';
import { ReasonDialog } from '@/modules/inventory/ReasonDialog';
import { WorkingStorePicker, StoreProblem } from '@/modules/inventory/StorePicker';
import { ItemFlags } from '@/modules/inventory/StockBits';
import { useWorkingStore } from '@/modules/inventory/stores';
import {
  pharmacyApi,
  pharmacyKeys,
  pharmacyQueries,
  type DispenseLineRequest,
  type IpIssueLineInput,
  type PharmacySaleCreated,
  type PharmacyStockRow,
  type QueueEntryDetail,
  type QueueLineDto,
} from '@/modules/pharmacy/api';
import { BatchPick } from '@/modules/pharmacy/BatchPick';
import { BillSummary } from '@/modules/pharmacy/BillSummary';
import {
  DEFAULT_MAX_DISCOUNT_PERCENT,
  useCounterPrefs,
  useItemMaster,
  usePharmacyBatches,
} from '@/modules/pharmacy/counter';
import {
  controlledItems,
  evaluateSubstitution,
  itemsFromErrorData,
  planBatchIssue,
  substitutionProblems,
  suggestedQuantity,
  type BatchIssuePlan,
  type SubstitutionKind,
} from '@/modules/pharmacy/dispense';
import { MedicineSearch } from '@/modules/pharmacy/MedicineSearch';
import { PaymentPanel } from '@/modules/pharmacy/PaymentPanel';
import { ScanCheck } from '@/modules/pharmacy/ScanCheck';
import { scannedBatchProblem } from '@/modules/pharmacy/tags';
import { checkPayments, defaultPayments, resolvePayments, type PaymentDraft } from '@/modules/pharmacy/payments';
import { PHARMACY_PERMISSIONS as P } from '@/modules/pharmacy/permissions';
import { ipIssueAmount, priceSaleLine, saleTotals, type SaleLinePrice } from '@/modules/pharmacy/pricing';
import { directionsText, printIpIssueSlip, printLabels, printReceipt, type DrugLabel } from '@/modules/pharmacy/print';

export const Route = createFileRoute('/_app/pharmacy/queue/$entryId')({
  validateSearch: (s: Record<string, unknown>): { ip?: boolean } => ({
    ip: s.ip === true || s.ip === 'true' ? true : undefined,
  }),
  component: EntryPage,
});

const useStyles = makeStyles({
  scan: { padding: '12px 16px 8px', maxWidth: '460px' },
  layout: {
    display: 'grid',
    gridTemplateColumns: 'minmax(0, 1fr) 380px',
    gap: '16px',
    alignItems: 'start',
    '@media (max-width: 1180px)': { gridTemplateColumns: 'minmax(0, 1fr)' },
  },
  main: { display: 'flex', flexDirection: 'column', gap: '16px', minWidth: 0 },
  aside: { display: 'flex', flexDirection: 'column', gap: '16px', position: 'sticky', top: '76px' },
  facts: {
    display: 'flex',
    gap: '6px 18px',
    flexWrap: 'wrap',
    alignItems: 'center',
    color: tokens.colorNeutralForeground2,
  },
  drug: { display: 'flex', flexDirection: 'column', gap: '2px', minWidth: '220px' },
  muted: { color: tokens.colorNeutralForeground3 },
  problem: { color: tokens.colorPaletteRedForeground1 },
  qty: { width: '110px' },
  amount: { fontVariantNumeric: 'tabular-nums', whiteSpace: 'nowrap' },
  controlled: { borderLeft: `4px solid ${tokens.colorPaletteRedBorder2}` },
  footer: { display: 'flex', flexDirection: 'column', gap: '8px' },
  done: { display: 'flex', flexDirection: 'column', alignItems: 'center', gap: '6px', textAlign: 'center' },
  big: {
    fontSize: tokens.fontSizeHero800,
    lineHeight: tokens.lineHeightHero800,
    fontWeight: tokens.fontWeightBold,
    color: tokens.colorBrandForeground1,
  },
  row: { display: 'flex', gap: '8px', alignItems: 'center', flexWrap: 'wrap' },
});

/** Opens one prescription for dispensing (OP) or issue to the ward (IP). */
function EntryPage() {
  const { entryId } = Route.useParams();
  const search = Route.useSearch();
  const opStore = useWorkingStore('pharmacy-op', { type: 'Pharmacy' });
  const ipStore = useWorkingStore('pharmacy-ip', { type: 'Pharmacy' });
  const [inpatientKnown, setInpatientKnown] = useState<boolean | null>(null);
  const inpatient = inpatientKnown ?? search.ip === true;
  const working = inpatient ? ipStore : opStore;
  const entry = useQuery({
    ...pharmacyQueries.entry(entryId, working.storeId),
    enabled: !working.loading,
    // Keep showing this prescription while the store changes, but never another patient's prescription.
    placeholderData: (previous) => (previous?.id === entryId ? previous : undefined),
  });

  // The queue link says OP or IP; a deep link finds out from the entry itself (then the store scope follows).
  if (entry.data && entry.data.id === entryId && entry.data.isInpatient !== inpatient) {
    setInpatientKnown(entry.data.isInpatient);
  }

  if (entry.isLoading || (!entry.data && working.loading)) {
    return <LoadingState label="Opening the prescription…" />;
  }
  if (entry.error && !entry.data) {
    return (
      <Page maxWidth={900}>
        <ErrorState error={entry.error} onRetry={() => void entry.refetch()} />
      </Page>
    );
  }
  if (!entry.data) {
    return null;
  }
  return (
    <EntryScreen
      key={`${entryId}:${working.storeId ?? 'none'}`}
      entry={entry.data}
      working={working}
      refreshing={entry.isFetching}
    />
  );
}

interface SubstituteChoice {
  itemId: string;
  name: string;
  genericName: string | null;
  strength: string | null;
  schedule: DrugSchedule | null;
  isControlled: boolean;
  baseUnit: string | null;
}

interface LineChoice {
  quantity?: string;
  batchId?: string | null;
  substitute?: SubstituteChoice | null;
  reason?: string;
  consent?: boolean;
}

interface LinePlan {
  line: QueueLineDto;
  choice: LineChoice;
  item: SubstituteChoice | null;
  kind: SubstitutionKind;
  minDays: number;
  quantity: number;
  quantityText: string;
  usable: number | null;
  batches: BatchStock[] | undefined;
  plan: BatchIssuePlan | null;
  problems: string[];
  priced: SaleLinePrice[];
  amount: number;
}

type Done =
  | { kind: 'sale'; sale: PharmacySaleCreated; labels: DrugLabel[] }
  | { kind: 'issue'; id: string; number: string; total: number; labels: DrugLabel[] };

function prescribedItem(line: QueueLineDto): SubstituteChoice | null {
  return line.drugItemId
    ? {
        itemId: line.drugItemId,
        name: line.drugName,
        genericName: line.genericName,
        strength: line.strength,
        schedule: null,
        isControlled: line.isControlledDrug,
        baseUnit: null,
      }
    : null;
}

function EntryScreen({
  entry,
  working,
  refreshing,
}: {
  entry: QueueEntryDetail;
  working: ReturnType<typeof useWorkingStore>;
  refreshing: boolean;
}) {
  const styles = useStyles();
  // Closing a dialog opened from the menu returns focus to the menu button.
  const restoreFocusTarget = useRestoreFocusTarget();
  const notify = useNotify();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const letterhead = useLetterhead();
  const [prefs, setPrefs] = useCounterPrefs();
  const facilityId = useSession((s) => s.user?.facilityId);
  const canDispense = usePermission(entry.isInpatient ? P.ipIssue : P.dispense);
  const canClose = usePermission([P.dispense, P.ipIssue]);
  const canRefill = usePermission(P.refills);
  const storeId = working.storeId;

  const [choices, setChoices] = useState<Record<string, LineChoice>>({});
  const [discount, setDiscount] = useState('');
  const [notes, setNotes] = useState('');
  const [confirmControlled, setConfirmControlled] = useState(false);
  const [payments, setPayments] = useState<PaymentDraft[]>(defaultPayments);
  const [substituting, setSubstituting] = useState<QueueLineDto | null>(null);
  const [dialog, setDialog] = useState<'close' | 'authorize' | 'request-refill' | null>(null);
  const [done, setDone] = useState<Done | null>(null);
  const [changedElsewhere, setChangedElsewhere] = useState(false);

  const open = entry.status === 'Pending' || entry.status === 'PartiallyDispensed';
  const openLines = entry.lines.filter((l) => l.status === 'Pending' || l.status === 'PartiallyDispensed');
  const effective = (line: QueueLineDto) => {
    const choice = choices[line.id];
    return choice?.substitute !== undefined && choice.substitute !== null ? choice.substitute : prescribedItem(line);
  };
  const itemIds = Array.from(
    new Set(
      openLines
        .map(effective)
        .filter((i): i is SubstituteChoice => i !== null)
        .map((i) => i.itemId),
    ),
  );
  const stock = usePharmacyBatches(open ? storeId : null, itemIds);
  const master = useItemMaster(itemIds);
  const discountPercent = entry.isInpatient ? 0 : (parseNumber(discount) ?? 0);

  const plans: LinePlan[] = openLines.map((line) => {
    const choice = choices[line.id] ?? {};
    const item = effective(line);
    const minDays = entry.isInpatient ? 0 : dispensingShelfLifeDays(line.durationDays);
    const batches = item ? stock.batches.get(item.itemId) : undefined;
    const usable = batches ? usableQuantity(batches, minDays) : null;
    const suggested = usable === null ? line.remainingQuantity : suggestedQuantity(line.remainingQuantity, usable);
    const quantityText = choice.quantity ?? String(suggested);
    const quantity = parseNumber(quantityText) ?? 0;
    const kind: SubstitutionKind = item
      ? evaluateSubstitution(line.drugItemId, line.genericName, line.strength, item)
      : 'none';
    const unit = item ? (master.items.get(item.itemId)?.baseUnit ?? item.baseUnit) : null;
    const plan =
      item && quantity > 0
        ? planBatchIssue(
            {
              quantity,
              remaining: line.remainingQuantity,
              batchId: choice.batchId ?? null,
              minimumShelfLifeDays: minDays,
            },
            batches,
            unit,
          )
        : null;
    const problems =
      quantity > 0
        ? [
            ...(item ? [] : ['Choose the product to give: this line has no item from the drug master.']),
            ...(plan?.problems ?? []),
            ...substitutionProblems(kind, choice.reason ?? '', choice.consent ?? false),
          ]
        : [];
    const gstRate = item ? (master.items.get(item.itemId)?.gstRate ?? null) : null;
    const priced =
      plan && !entry.isInpatient
        ? plan.allocation.allocations.map((a) =>
            priceSaleLine(
              batches?.find((b) => b.batchId === a.batchId)?.mrp ?? 0,
              a.quantity,
              gstRate,
              discountPercent,
            ),
          )
        : [];
    const amount = entry.isInpatient
      ? sumNumbers(
          (plan?.allocation.allocations ?? []).map((a) =>
            ipIssueAmount(a.quantity, batches?.find((b) => b.batchId === a.batchId)?.mrp ?? 0),
          ),
        )
      : sumNumbers(priced.map((p) => p.net));
    return {
      line,
      choice,
      item,
      kind,
      minDays,
      quantity,
      quantityText,
      usable,
      batches,
      plan,
      problems,
      priced,
      amount,
    };
  });

  const active = plans.filter((p) => p.quantity > 0);
  const pending = active.some((p) => p.plan?.pending);
  const problems = active.flatMap((p) => p.problems.map((problem) => `${p.item?.name ?? p.line.drugName}: ${problem}`));
  const totals = saleTotals(active.flatMap((p) => p.priced));
  const ipTotal = sumNumbers(active.map((p) => p.amount));
  const controlled = controlledItems(
    active.map((p) => {
      const detail = p.item ? master.items.get(p.item.itemId) : undefined;
      return {
        name: p.item?.name ?? p.line.drugName,
        schedule: detail?.schedule ?? p.item?.schedule ?? null,
        isControlled: detail?.isControlled ?? p.item?.isControlled ?? false,
        lineIsControlled: p.line.isControlledDrug,
      };
    }),
  );
  const resolved = resolvePayments(totals.net, payments);
  const paymentCheck = checkPayments(totals.net, resolved, true);
  const discountInvalid = discountPercent < 0 || discountPercent > 100;
  const ready =
    open &&
    canDispense &&
    Boolean(storeId) &&
    active.length > 0 &&
    !pending &&
    problems.length === 0 &&
    (controlled.length === 0 || confirmControlled) &&
    (entry.isInpatient || (paymentCheck.ok && !discountInvalid));

  const setChoice = (lineId: string, patch: Partial<LineChoice>) =>
    setChoices((prev) => ({ ...prev, [lineId]: { ...prev[lineId], ...patch } }));

  // A scanned strip or pharmacy tag (INT-BAR): is it on this prescription, and which batch is in hand?
  const checkScanned = (row: PharmacyStockRow) => {
    const lines = plans.filter((p) => p.item?.itemId === row.itemId && p.quantity > 0);
    if (lines.length === 0) {
      notify.warning(
        `${row.name} is not being given on this prescription`,
        'Check the medicine in hand against the prescription.',
      );
      return;
    }
    if (!row.scanned) {
      notify.success(`${row.name} is on this prescription`);
      return;
    }
    const problem = scannedBatchProblem(row.scanned);
    if (problem) {
      notify.warning(`${row.name}: ${problem}`, 'Pick another batch, or put this one aside.');
      return;
    }
    for (const p of lines) {
      setChoice(p.line.id, { batchId: row.scanned.batchId });
    }
    notify.success(`${row.name}: batch ${row.scanned.batchNumber} picked`);
  };

  const buildLabels = (): DrugLabel[] =>
    active
      .filter((p) => p.plan)
      .map((p) => {
        const allocations = p.plan?.allocation.allocations ?? [];
        const expiries = allocations
          .map((a) => a.expiryDate)
          .filter((d): d is string => Boolean(d))
          .sort();
        return {
          patientName: entry.patient.fullName,
          drugName: p.item?.name ?? p.line.drugName,
          quantity: p.quantity,
          unit: p.item ? (master.items.get(p.item.itemId)?.baseUnit ?? null) : null,
          directions: directionsText(p.line),
          batchNumber: allocations.map((a) => a.batchNumber).join(', '),
          expiryDate: expiries[0] ?? null,
          dispensedOn: todayIso(),
        };
      });

  const refreshAll = () => {
    void queryClient.invalidateQueries({ queryKey: pharmacyKeys.queue });
    void queryClient.invalidateQueries({ queryKey: pharmacyKeys.stock });
    void queryClient.invalidateQueries({ queryKey: pharmacyKeys.dashboard });
    void queryClient.invalidateQueries({ queryKey: pharmacyKeys.sales });
    void queryClient.invalidateQueries({ queryKey: pharmacyKeys.ward });
  };

  const submit = useMutation({
    mutationFn: async ({ labels }: { labels: DrugLabel[] }): Promise<Done> => {
      if (!storeId) {
        throw new Error('No store');
      }
      if (entry.isInpatient) {
        const lines: IpIssueLineInput[] = active.flatMap((p) =>
          (p.plan?.allocation.allocations ?? []).map((a) => ({
            itemId: p.item!.itemId,
            quantity: a.quantity,
            batchId: a.batchId,
            wardIndentLineId: null,
            queueLineId: p.line.id,
            substitutionReason: p.kind === 'substitution' ? (p.choice.reason ?? '').trim() : null,
            prescriberConsent: p.kind === 'substitution' && Boolean(p.choice.consent),
          })),
        );
        const issued = await pharmacyApi.issueToWard({
          storeId,
          wardIndentId: null,
          queueEntryId: entry.id,
          patientId: null,
          admissionId: null,
          visitId: null,
          lines,
          confirmControlledDrugs: controlled.length > 0 && confirmControlled,
        });
        return { kind: 'issue', id: issued.id, number: issued.number, total: issued.totalAmount, labels };
      }
      const lines: DispenseLineRequest[] = active.flatMap((p) =>
        (p.plan?.allocation.allocations ?? []).map((a) => ({
          queueLineId: p.line.id,
          itemId: p.item!.itemId,
          quantity: a.quantity,
          batchId: a.batchId,
          substitutionReason: p.kind === 'substitution' ? (p.choice.reason ?? '').trim() : null,
          prescriberConsent: p.kind === 'substitution' && Boolean(p.choice.consent),
        })),
      );
      const sale = await pharmacyApi.dispense(entry.id, {
        storeId,
        lines,
        counsellingNotes: notes.trim() || null,
        discountPercent,
        payments: resolved
          .filter((p) => p.amount > 0)
          .map((p) => ({ mode: p.mode, amount: p.amount, reference: p.reference })),
        confirmControlledDrugs: controlled.length > 0 && confirmControlled,
      });
      return { kind: 'sale', sale, labels };
    },
    onSuccess: (result) => {
      setDone(result);
      refreshAll();
      if (result.kind === 'sale' && prefs.autoPrintReceipt) {
        void queryClient
          .fetchQuery(pharmacyQueries.sale(result.sale.id))
          .then((detail) => printReceipt(detail, letterhead))
          .catch((error: unknown) => notify.error(error, 'The receipt did not print'));
      }
      if (prefs.autoPrintLabels) {
        void printLabels(result.labels, letterhead.name).catch((error: unknown) =>
          notify.error(error, 'The labels did not print'),
        );
      }
    },
    onError: (error) => {
      if (
        isApiError(error) &&
        (error.code?.startsWith('stock.') || error.code === 'payment.mismatch' || error.isConflict)
      ) {
        stock.refetch();
        void queryClient.invalidateQueries({ queryKey: ['pharmacy', 'queue', 'detail', entry.id] });
      }
    },
  });

  useRealtimeEvent<{ entryId: string; status: string }>(
    facilityId ? `pharmacy:${facilityId}` : null,
    'pharmacy.queue.changed',
    (payload) => {
      if (payload.entryId === entry.id && !submit.isPending && !done) {
        setChangedElsewhere(true);
        void queryClient.invalidateQueries({ queryKey: ['pharmacy', 'queue', 'detail', entry.id] });
      }
    },
  );

  const dispense = () => {
    if (ready && !submit.isPending) {
      submit.mutate({ labels: buildLabels() });
    }
  };
  useHotkey('f9', dispense, { allowInInputs: true, enabled: ready });

  const printDoneReceipt = () => {
    if (done?.kind === 'sale') {
      void queryClient
        .fetchQuery(pharmacyQueries.sale(done.sale.id))
        .then((detail) => printReceipt(detail, letterhead))
        .catch((error: unknown) => notify.error(error, 'The receipt did not print'));
    } else if (done?.kind === 'issue') {
      void queryClient
        .fetchQuery(pharmacyQueries.ipIssue(done.id))
        .then((detail) => printIpIssueSlip(detail, letterhead))
        .catch((error: unknown) => notify.error(error, 'The slip did not print'));
    }
  };

  const goNext = async () => {
    try {
      const list = await queryClient.fetchQuery(pharmacyQueries.queue({ inpatient: entry.isInpatient }));
      const next = list.items.find(
        (e) => e.id !== entry.id && (e.status === 'Pending' || e.status === 'PartiallyDispensed'),
      );
      if (next) {
        void navigate({
          to: '/pharmacy/queue/$entryId',
          params: { entryId: next.id },
          search: { ip: next.isInpatient || undefined },
        });
      } else {
        notify.info('No one else is waiting');
        void navigate({ to: '/pharmacy/queue', search: { ip: entry.isInpatient || undefined } });
      }
    } catch (error) {
      notify.error(error, 'Could not open the next prescription');
    }
  };

  const refillsLeft = Math.max(entry.refillsAuthorized - entry.refillsUsed, 0);
  const hasControlledLines = entry.lines.some((l) => l.isControlledDrug);
  const canAuthorize =
    canRefill &&
    !entry.isInpatient &&
    entry.refillOfEntryId === null &&
    !hasControlledLines &&
    entry.status !== 'Cancelled';
  const canRequestRefill =
    canRefill &&
    !entry.isInpatient &&
    entry.status === 'Dispensed' &&
    refillsLeft > 0 &&
    (!entry.refillValidUntil || entry.refillValidUntil >= todayIso());
  const errorItems = isApiError(submit.error) ? itemsFromErrorData(submit.error.data) : [];

  return (
    <Page>
      <PatientBannerWithFlags
        patientId={entry.patient.id}
        patient={{
          id: entry.patient.id,
          uhid: entry.patient.uhid,
          fullName: entry.patient.fullName,
          gender: entry.patient.gender,
          age: entry.patient.age,
          phone: entry.patient.phone,
          bloodGroup: entry.patient.bloodGroup,
          category: entry.patient.category,
          isVip: entry.patient.isVip,
          isDeceased: entry.patient.isDeceased,
        }}
        actions={
          <Menu>
            <MenuTrigger disableButtonEnhancement>
              <Button icon={<MoreHorizontalRegular />} aria-label="More actions" {...restoreFocusTarget} />
            </MenuTrigger>
            <MenuPopover>
              <MenuList>
                <MenuItem
                  onClick={() => void navigate({ to: '/patients/$patientId', params: { patientId: entry.patient.id } })}
                >
                  Open patient profile
                </MenuItem>
                {canAuthorize && (
                  <MenuItem icon={<ArrowRepeatAllRegular />} onClick={() => setDialog('authorize')}>
                    Authorise refills
                  </MenuItem>
                )}
                {canRequestRefill && (
                  <MenuItem icon={<ArrowRepeatAllRegular />} onClick={() => setDialog('request-refill')}>
                    Request a refill
                  </MenuItem>
                )}
                {open && canClose && (
                  <MenuItem icon={<DismissCircleRegular />} onClick={() => setDialog('close')}>
                    Close prescription
                  </MenuItem>
                )}
              </MenuList>
            </MenuPopover>
          </Menu>
        }
      />

      <div className={styles.facts}>
        <Text weight="semibold">Prescription {entry.prescriptionNumber}</Text>
        <StatusBadge
          status={entry.status}
          tone={workflowTone(entry.status)}
          label={entry.status === 'Pending' ? 'Waiting' : undefined}
        />
        {entry.isInpatient && <Badge color="brand">In-patient: issue to the ward</Badge>}
        {entry.refillOfEntryId && (
          <Badge appearance="tint" icon={<ArrowRepeatAllRegular />}>
            Refill
          </Badge>
        )}
        <Caption1>
          {entry.prescriberName ?? 'Prescriber not recorded'}
          {entry.prescriberRegistrationNumber ? ` · Reg. ${entry.prescriberRegistrationNumber}` : ''}
        </Caption1>
        <Caption1>
          Signed {formatDateTime(entry.signedAt)} · waiting since {formatRelative(entry.receivedAt)}
        </Caption1>
        {entry.refillsAuthorized > 0 && (
          <Caption1>
            Refills: {refillsLeft} of {entry.refillsAuthorized} left
            {entry.refillValidUntil ? ` until ${formatDateOnly(entry.refillValidUntil)}` : ''}
          </Caption1>
        )}
        {refreshing && <Spinner size="extra-tiny" aria-label="Refreshing" />}
      </div>

      {changedElsewhere && (
        <MessageBar intent="warning">
          <MessageBarBody>
            <MessageBarTitle>Updated at another counter</MessageBarTitle>
            This prescription changed while it was open. The quantities below now show what is still left.
          </MessageBarBody>
          <Button appearance="transparent" onClick={() => setChangedElsewhere(false)}>
            OK
          </Button>
        </MessageBar>
      )}

      {!open && (
        <MessageBar intent={entry.status === 'Dispensed' ? 'success' : 'info'} layout="multiline">
          <MessageBarBody>
            <MessageBarTitle>{entry.status === 'Dispensed' ? 'Fully dispensed' : 'Closed'}</MessageBarTitle>
            {entry.closeReason ??
              (entry.status === 'Dispensed'
                ? 'Every line of this prescription has been supplied.'
                : 'Nothing more can be supplied on this prescription.')}
          </MessageBarBody>
        </MessageBar>
      )}

      {open && <StoreProblem working={working} kind="pharmacy store" />}

      <div className={styles.layout}>
        <div className={styles.main}>
          <Section
            title="Medicines"
            description={
              open
                ? entry.isInpatient
                  ? 'Batches are suggested earliest expiry first. Issued medicines are charged to the IP account at MRP.'
                  : 'Batches are suggested earliest expiry first and must last the course (up to 30 days). Set a quantity to 0 to skip a line.'
                : undefined
            }
            actions={
              open && working.stores.length > 1 ? (
                <WorkingStorePicker working={working} label="Dispensing from" />
              ) : undefined
            }
            flush
          >
            {open && canDispense && (
              <div className={styles.scan}>
                <ScanCheck storeId={storeId} onScanned={checkScanned} />
              </div>
            )}
            <LineTable<LinePlan | QueueLineDto>
              ariaLabel="Prescription lines"
              rows={open ? plans : entry.lines}
              getRowId={(row) => ('line' in row ? row.line.id : row.id)}
              isFlagged={(row) => 'line' in row && row.problems.length > 0}
              columns={
                open
                  ? [
                      { key: 'drug', header: 'Prescribed', render: (row) => <DrugCell plan={row as LinePlan} /> },
                      {
                        key: 'left',
                        header: 'Left to give',
                        align: 'end',
                        width: 100,
                        render: (row) => {
                          const p = row as LinePlan;
                          return (
                            <span>
                              <Text weight="semibold">{formatQty(p.line.remainingQuantity)}</Text>
                              {p.line.dispensedQuantity > 0 && (
                                <Caption1 block className={styles.muted}>
                                  of {formatQty(p.line.requestedQuantity)}
                                </Caption1>
                              )}
                              <Caption1 block className={p.usable === 0 ? styles.problem : styles.muted}>
                                {p.usable === null
                                  ? ''
                                  : p.usable > 0
                                    ? `${formatQty(p.usable)} in stock`
                                    : 'Out of stock'}
                              </Caption1>
                            </span>
                          );
                        },
                      },
                      {
                        key: 'give',
                        header: 'Give now',
                        width: 120,
                        render: (row) => {
                          const p = row as LinePlan;
                          return (
                            <Input
                              className={styles.qty}
                              type="number"
                              min={0}
                              aria-label={`Quantity of ${p.item?.name ?? p.line.drugName} to give now`}
                              value={p.quantityText}
                              disabled={!canDispense}
                              onChange={(_, d) => setChoice(p.line.id, { quantity: d.value })}
                            />
                          );
                        },
                      },
                      {
                        key: 'batch',
                        header: 'Batch',
                        render: (row) => {
                          const p = row as LinePlan;
                          if (p.quantity <= 0) {
                            return <Caption1 className={styles.muted}>Skipped</Caption1>;
                          }
                          return (
                            <BatchPick
                              batches={p.batches}
                              allocations={p.plan?.allocation.allocations ?? []}
                              pickedBatchId={p.choice.batchId ?? null}
                              onPick={(batchId) => setChoice(p.line.id, { batchId })}
                              minimumShelfLifeDays={p.minDays}
                              disabled={!canDispense || !p.item}
                            />
                          );
                        },
                      },
                      {
                        key: 'amount',
                        header: entry.isInpatient ? 'Charge' : 'Amount',
                        align: 'end',
                        width: 110,
                        render: (row) => (
                          <span className={styles.amount}>
                            {(row as LinePlan).quantity > 0 ? formatInr((row as LinePlan).amount) : '—'}
                          </span>
                        ),
                      },
                      {
                        key: 'actions',
                        header: '',
                        width: 44,
                        render: (row) => {
                          const p = row as LinePlan;
                          return (
                            <Tooltip content="Give a generic equivalent (brand substitution)" relationship="label">
                              <Button
                                appearance="subtle"
                                icon={<ArrowSwapRegular />}
                                disabled={!canDispense || !storeId}
                                onClick={() => setSubstituting(p.line)}
                              />
                            </Tooltip>
                          );
                        },
                      },
                    ]
                  : [
                      { key: 'drug', header: 'Medicine', render: (row) => <DrugSummary line={row as QueueLineDto} /> },
                      {
                        key: 'req',
                        header: 'Prescribed',
                        align: 'end',
                        render: (row) => formatQty((row as QueueLineDto).requestedQuantity),
                      },
                      {
                        key: 'given',
                        header: 'Given',
                        align: 'end',
                        render: (row) => formatQty((row as QueueLineDto).dispensedQuantity),
                      },
                      {
                        key: 'status',
                        header: 'Status',
                        render: (row) => (
                          <StatusBadge
                            status={(row as QueueLineDto).status}
                            tone={workflowTone((row as QueueLineDto).status)}
                          />
                        ),
                      },
                    ]
              }
              empty={<EmptyState title="No medicines on this prescription" />}
            />
            {open && (
              <div style={{ padding: '8px 16px 12px' }}>
                {plans
                  .filter((p) => p.quantity > 0 && p.problems.length > 0)
                  .map((p) => (
                    <Caption1 key={p.line.id} block className={styles.problem}>
                      <WarningRegular /> {p.item?.name ?? p.line.drugName}: {p.problems.join(' ')}
                    </Caption1>
                  ))}
              </div>
            )}
          </Section>

          {open && !entry.isInpatient && (
            <Section title="Counselling">
              <Field
                label="Advice given to the patient"
                hint="Printed on the invoice, e.g. take after food, avoid alcohol, complete the course"
              >
                <Textarea
                  value={notes}
                  onChange={(_, d) => setNotes(d.value)}
                  maxLength={2000}
                  rows={2}
                  disabled={!canDispense}
                />
              </Field>
            </Section>
          )}
        </div>

        {open && (
          <aside className={styles.aside}>
            {controlled.length > 0 && (
              <Section className={styles.controlled}>
                <MessageBar intent="warning" layout="multiline" icon={<ShieldKeyholeRegular />}>
                  <MessageBarBody>
                    <MessageBarTitle>Controlled medicines</MessageBarTitle>
                    {controlled.join(', ')}: Schedule H1/X or narcotic. They are written to the controlled-drug register
                    with the patient, prescriber and registration number.
                  </MessageBarBody>
                </MessageBar>
                <Checkbox
                  checked={confirmControlled}
                  onChange={(_, d) => setConfirmControlled(d.checked === true)}
                  label="I have checked the prescription, the patient's identity and the quantity"
                  disabled={!canDispense}
                />
              </Section>
            )}

            {entry.isInpatient ? (
              <Section title="Issue to the ward">
                <div className={styles.row}>
                  <Text>Charged to the IP account at MRP</Text>
                  <Text weight="bold" size={500}>
                    {formatInr(ipTotal)}
                  </Text>
                </div>
              </Section>
            ) : (
              <Section title="Bill">
                <div style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
                  <Field
                    label="Discount %"
                    hint={`Up to the hospital limit (usually ${DEFAULT_MAX_DISCOUNT_PERCENT}%)`}
                    validationState={discountInvalid ? 'error' : 'none'}
                    validationMessage={discountInvalid ? 'Enter 0 to 100' : undefined}
                  >
                    <Input
                      type="number"
                      min={0}
                      max={100}
                      value={discount}
                      placeholder="0"
                      onChange={(_, d) => setDiscount(d.value)}
                      disabled={!canDispense}
                    />
                  </Field>
                  <BillSummary totals={totals} discountPercent={discountPercent} />
                  <PaymentPanel
                    bill={totals.net}
                    drafts={payments}
                    onChange={setPayments}
                    resolved={resolved}
                    check={paymentCheck}
                    creditAllowed
                    disabled={!canDispense}
                  />
                </div>
              </Section>
            )}

            <Section>
              <div className={styles.footer}>
                {submit.error && (
                  <MessageBar intent="error" layout="multiline">
                    <MessageBarBody>
                      <MessageBarTitle>{isApiError(submit.error) ? submit.error.title : 'Not saved'}</MessageBarTitle>
                      {isApiError(submit.error) && submit.error.code === 'payment.mismatch'
                        ? `${submit.error.userMessage} Stock or prices changed since the bill was made. The bill has been refreshed: check it and the payment, then try again.`
                        : isApiError(submit.error)
                          ? submit.error.userMessage
                          : 'Nothing was saved. Try again.'}
                      {errorItems.length > 0 && <div>Items: {errorItems.join(', ')}</div>}
                    </MessageBarBody>
                  </MessageBar>
                )}
                {!canDispense && (
                  <Caption1>
                    Your account can view this prescription but cannot{' '}
                    {entry.isInpatient ? 'issue to the ward' : 'dispense'}.
                  </Caption1>
                )}
                {canDispense && problems.length > 0 && active.length > 0 && (
                  <Caption1 className={styles.problem}>Fix the highlighted lines to continue.</Caption1>
                )}
                <Button
                  appearance="primary"
                  size="large"
                  icon={submit.isPending ? <Spinner size="tiny" /> : <CheckmarkCircleRegular />}
                  disabled={!ready || submit.isPending}
                  onClick={dispense}
                  aria-keyshortcuts="F9"
                >
                  {entry.isInpatient ? `Issue to ward (F9)` : `Dispense and bill ${formatInr(totals.net)} (F9)`}
                </Button>
                <div className={styles.row}>
                  <Switch
                    checked={prefs.autoPrintReceipt}
                    onChange={(_, d) => setPrefs({ autoPrintReceipt: d.checked })}
                    label="Print receipt"
                  />
                  <Switch
                    checked={prefs.autoPrintLabels}
                    onChange={(_, d) => setPrefs({ autoPrintLabels: d.checked })}
                    label="Print labels"
                  />
                </div>
              </div>
            </Section>
          </aside>
        )}
      </div>

      {substituting && storeId && (
        <SubstituteDialog
          line={substituting}
          storeId={storeId}
          current={choices[substituting.id]}
          onClose={() => setSubstituting(null)}
          onChoose={(choice) => {
            setChoice(substituting.id, { ...choice, batchId: null, quantity: undefined });
            setSubstituting(null);
          }}
        />
      )}

      {dialog === 'close' && <CloseEntryDialog entry={entry} onClose={() => setDialog(null)} />}
      {dialog === 'authorize' && <AuthorizeRefillsDialog entry={entry} onClose={() => setDialog(null)} />}
      {dialog === 'request-refill' && <RequestRefillDialog entry={entry} onClose={() => setDialog(null)} />}

      <Dialog
        open={done !== null}
        onOpenChange={(_, d) =>
          !d.open && void navigate({ to: '/pharmacy/queue', search: { ip: entry.isInpatient || undefined } })
        }
      >
        <DialogSurface style={{ maxWidth: 520 }}>
          <DialogBody>
            <DialogTitle>{done?.kind === 'issue' ? 'Issued to the ward' : 'Dispensed'}</DialogTitle>
            <DialogContent>
              <div className={styles.done} role="status">
                <CheckmarkCircleRegular fontSize={40} color={tokens.colorPaletteGreenForeground1} />
                <Text>{entry.patient.fullName}</Text>
                {done?.kind === 'sale' && (
                  <>
                    <Caption1>Invoice {done.sale.invoiceNumber}</Caption1>
                    <span className={styles.big}>{formatInr(done.sale.netAmount)}</span>
                    <Caption1>
                      Paid {formatInr(done.sale.paidAmount)}
                      {done.sale.dueAmount > 0 ? ` · ${formatInr(done.sale.dueAmount)} due` : ''}
                    </Caption1>
                  </>
                )}
                {done?.kind === 'issue' && (
                  <>
                    <Caption1>Issue {done.number}</Caption1>
                    <span className={styles.big}>{formatInr(done.total)}</span>
                    <Caption1>charged to the IP account</Caption1>
                  </>
                )}
              </div>
            </DialogContent>
            <DialogActions fluid>
              <Button icon={<PrintRegular />} onClick={printDoneReceipt}>
                {done?.kind === 'issue' ? 'Print slip' : 'Print receipt'}
              </Button>
              <Button
                icon={<TagRegular />}
                onClick={() =>
                  done &&
                  void printLabels(done.labels, letterhead.name).catch((error: unknown) =>
                    notify.error(error, 'The labels did not print'),
                  )
                }
              >
                Print labels
              </Button>
              <Button appearance="primary" onClick={() => void goNext()} autoFocus>
                Next prescription
              </Button>
            </DialogActions>
          </DialogBody>
        </DialogSurface>
      </Dialog>
    </Page>
  );
}

function DrugSummary({ line }: { line: QueueLineDto }) {
  const styles = useStyles();
  return (
    <div className={styles.drug}>
      <span>
        <Text weight="semibold">{line.drugName}</Text>{' '}
        {line.isControlledDrug && (
          <Badge size="small" color="danger" icon={<ShieldKeyholeRegular />}>
            Controlled
          </Badge>
        )}
      </span>
      <Caption1 className={styles.muted}>
        {[line.genericName, line.strength, line.dosageForm].filter(Boolean).join(' · ')}
      </Caption1>
      <Caption1>{directionsText(line)}</Caption1>
    </div>
  );
}

function DrugCell({ plan }: { plan: LinePlan }) {
  const styles = useStyles();
  const { line, item, kind } = plan;
  return (
    <div className={styles.drug}>
      <DrugSummary line={line} />
      {item && item.itemId !== line.drugItemId && (
        <Caption1 className={kind === 'substitution' ? styles.problem : styles.muted}>
          Giving {item.name}
          {kind === 'substitution' ? ' (substitution)' : ' (same generic)'}
        </Caption1>
      )}
      {item && <ItemFlags schedule={item.schedule} isControlled={item.isControlled} />}
    </div>
  );
}

/**
 * Brand substitution (SubstitutionPolicy): any brand of a generically prescribed drug is fine; replacing a
 * prescribed brand needs the same generic and strength, a reason and the prescriber's consent.
 */
function SubstituteDialog({
  line,
  storeId,
  current,
  onClose,
  onChoose,
}: {
  line: QueueLineDto;
  storeId: string;
  current: LineChoice | undefined;
  onClose: () => void;
  onChoose: (choice: Pick<LineChoice, 'substitute' | 'reason' | 'consent'>) => void;
}) {
  const [picked, setPicked] = useState<SubstituteChoice | null>(current?.substitute ?? null);
  const [reason, setReason] = useState(current?.reason ?? '');
  const [consent, setConsent] = useState(current?.consent ?? false);
  const kind = picked ? evaluateSubstitution(line.drugItemId, line.genericName, line.strength, picked) : 'none';
  const problems = picked ? substitutionProblems(kind, reason, consent) : ['Choose the product to give.'];

  const toChoice = (row: PharmacyStockRow): SubstituteChoice => ({
    itemId: row.itemId,
    name: row.name,
    genericName: row.genericName,
    strength: row.strength,
    schedule: row.schedule,
    isControlled: row.isControlled,
    baseUnit: row.baseUnit,
  });

  return (
    <Dialog open onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface style={{ maxWidth: 720, width: '95vw' }}>
        <DialogBody>
          <DialogTitle>Give another product for {line.drugName}</DialogTitle>
          <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12, minHeight: 320 }}>
            <Caption1>
              Prescribed: {[line.drugName, line.genericName, line.strength].filter(Boolean).join(' · ')}. Only a product
              with the same generic name and strength can be given.
            </Caption1>
            <MedicineSearch
              storeId={storeId}
              label="Search by generic name"
              initialText={line.genericName ?? line.drugName}
              autoFocus
              onPick={(row) => {
                setPicked(toChoice(row));
              }}
              renderExtra={(row) => {
                const k = evaluateSubstitution(line.drugItemId, line.genericName, line.strength, toChoice(row));
                return k === 'not-equivalent' ? (
                  <Badge size="small" appearance="tint" color="danger">
                    Different medicine
                  </Badge>
                ) : (
                  <Badge size="small" appearance="tint" color="success">
                    Equivalent
                  </Badge>
                );
              }}
            />
            {picked && (
              <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
                <Text>
                  Giving <b>{picked.name}</b> {[picked.genericName, picked.strength].filter(Boolean).join(' · ')}
                </Text>
                {kind === 'not-equivalent' && (
                  <MessageBar intent="error">
                    <MessageBarBody>
                      This is a different generic or strength and cannot be given for this prescription.
                    </MessageBarBody>
                  </MessageBar>
                )}
                {kind === 'none' && <Caption1>Same generic as prescribed: no consent needed.</Caption1>}
                {kind === 'substitution' && (
                  <>
                    <Field label="Reason for the substitution" required>
                      <Input
                        value={reason}
                        maxLength={300}
                        onChange={(_, d) => setReason(d.value)}
                        placeholder="e.g. Prescribed brand out of stock"
                      />
                    </Field>
                    <Checkbox
                      checked={consent}
                      onChange={(_, d) => setConsent(d.checked === true)}
                      label="The prescriber agreed to this substitution"
                    />
                  </>
                )}
              </div>
            )}
          </DialogContent>
          <DialogActions>
            {current?.substitute && (
              <Button
                onClick={() => onChoose({ substitute: null, reason: '', consent: false })}
                disabled={!line.drugItemId}
              >
                Give the prescribed item
              </Button>
            )}
            <Button onClick={onClose}>Cancel</Button>
            <Button
              appearance="primary"
              disabled={!picked || problems.length > 0}
              onClick={() => picked && onChoose({ substitute: picked, reason: reason.trim(), consent })}
            >
              Use this product
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}

function CloseEntryDialog({ entry, onClose }: { entry: QueueEntryDetail; onClose: () => void }) {
  const notify = useNotify();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const close = useMutation({
    mutationFn: (reason: string) => pharmacyApi.closeEntry(entry.id, reason),
    onSuccess: () => {
      notify.success(`Prescription ${entry.prescriptionNumber} closed`);
      void queryClient.invalidateQueries({ queryKey: pharmacyKeys.queue });
      void queryClient.invalidateQueries({ queryKey: pharmacyKeys.dashboard });
      void navigate({ to: '/pharmacy/queue', search: { ip: entry.isInpatient || undefined } });
    },
  });
  const partial = entry.lines.some((l) => l.dispensedQuantity > 0);
  return (
    <ReasonDialog
      title={`Close prescription ${entry.prescriptionNumber}?`}
      confirmLabel="Close prescription"
      destructive
      busy={close.isPending}
      error={close.error}
      placeholder="e.g. Patient bought the rest outside, doctor stopped the medicine"
      onSubmit={(reason) => close.mutate(reason)}
      onClose={onClose}
    >
      {partial
        ? 'Lines already partly supplied count as supplied; lines not started are cancelled. The prescription leaves the queue.'
        : 'Nothing has been supplied yet: every line is cancelled and the prescription leaves the queue.'}
    </ReasonDialog>
  );
}

function AuthorizeRefillsDialog({ entry, onClose }: { entry: QueueEntryDetail; onClose: () => void }) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const today = todayIso();
  const minimum = Math.max(entry.refillsUsed, 1);
  const [refills, setRefills] = useState(String(Math.max(entry.refillsAuthorized, minimum)));
  const [validUntil, setValidUntil] = useState(
    entry.refillValidUntil && entry.refillValidUntil > today ? entry.refillValidUntil : addDays(today, 90),
  );
  const [note, setNote] = useState('');
  const count = parseNumber(refills) ?? 0;
  const valid =
    Number.isInteger(count) && count >= minimum && count <= 12 && validUntil > today && note.trim().length >= 3;
  const save = useMutation({
    mutationFn: () => pharmacyApi.authorizeRefills(entry.id, { refills: count, validUntil, note: note.trim() }),
    onSuccess: () => {
      notify.success(
        `${count} refill${count === 1 ? '' : 's'} authorised until ${formatDateOnly(validUntil)}`,
        'The patient can now ask for a refill at the counter or on the portal.',
      );
      void queryClient.invalidateQueries({ queryKey: ['pharmacy', 'queue', 'detail', entry.id] });
      onClose();
    },
  });
  return (
    <Dialog open onOpenChange={(_, d) => !d.open && !save.isPending && onClose()}>
      <DialogSurface style={{ maxWidth: 480 }}>
        <DialogBody>
          <DialogTitle>Authorise refills</DialogTitle>
          <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
            <Caption1>
              For chronic medication only. Prescriptions with Schedule H1/X or narcotic medicines always need a new
              prescription.
            </Caption1>
            <Field label="Number of refills" required hint={`Between ${minimum} and 12`}>
              <Input
                type="number"
                min={minimum}
                max={12}
                value={refills}
                onChange={(_, d) => setRefills(d.value)}
                autoFocus
              />
            </Field>
            <Field label="Valid until" required>
              <Input
                type="date"
                min={addDays(today, 1)}
                value={validUntil}
                onChange={(_, d) => setValidUntil(d.value)}
              />
            </Field>
            <Field label="Authorised by" required hint='Record who authorised it, e.g. "Dr. Mehta, by phone, 25 Sep"'>
              <Input value={note} maxLength={300} onChange={(_, d) => setNote(d.value)} />
            </Field>
            {save.error && (
              <MessageBar intent="error">
                <MessageBarBody>{isApiError(save.error) ? save.error.userMessage : 'Not saved.'}</MessageBarBody>
              </MessageBar>
            )}
          </DialogContent>
          <DialogActions>
            <Button onClick={onClose} disabled={save.isPending}>
              Cancel
            </Button>
            <Button
              appearance="primary"
              disabled={!valid || save.isPending}
              // Keeps focus while saving: a disabled button drops it.
              disabledFocusable={save.isPending}
              onClick={() => save.mutate()}
            >
              Authorise
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}

function RequestRefillDialog({ entry, onClose }: { entry: QueueEntryDetail; onClose: () => void }) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const today = todayIso();
  const [pickup, setPickup] = useState(today);
  const [notes, setNotes] = useState('');
  const request = useMutation({
    mutationFn: () =>
      pharmacyApi.requestRefill({
        queueEntryId: entry.id,
        preferredPickupDate: pickup || null,
        notes: notes.trim() || null,
      }),
    onSuccess: (result) => {
      notify.success(
        `Refill request ${result.number} raised`,
        'Approve it under Refill requests to put it in the queue.',
      );
      void queryClient.invalidateQueries({ queryKey: pharmacyKeys.refills });
      onClose();
    },
  });
  return (
    <Dialog open onOpenChange={(_, d) => !d.open && !request.isPending && onClose()}>
      <DialogSurface style={{ maxWidth: 480 }}>
        <DialogBody>
          <DialogTitle>Request a refill</DialogTitle>
          <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
            <Field label="Preferred pickup date" hint="Within the next 30 days">
              <Input
                type="date"
                min={today}
                max={addDays(today, 30)}
                value={pickup}
                onChange={(_, d) => setPickup(d.value)}
              />
            </Field>
            <Field label="Notes">
              <Input value={notes} maxLength={500} onChange={(_, d) => setNotes(d.value)} />
            </Field>
            {request.error && (
              <MessageBar intent="error">
                <MessageBarBody>{isApiError(request.error) ? request.error.userMessage : 'Not saved.'}</MessageBarBody>
              </MessageBar>
            )}
          </DialogContent>
          <DialogActions>
            <Button onClick={onClose} disabled={request.isPending}>
              Cancel
            </Button>
            <Button
              appearance="primary"
              disabled={request.isPending}
              // Keeps focus while sending: a disabled button drops it.
              disabledFocusable={request.isPending}
              onClick={() => request.mutate()}
            >
              Raise request
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}
