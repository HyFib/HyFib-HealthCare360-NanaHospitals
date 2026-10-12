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
  Input,
  makeStyles,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  Spinner,
  Text,
  tokens,
} from '@fluentui/react-components';
import {
  AlertUrgentRegular,
  CheckmarkCircleRegular,
  DismissCircleRegular,
  PrintRegular,
  ShieldKeyholeRegular,
  WarningRegular,
} from '@fluentui/react-icons';
import { formatDateTime, formatInr, formatRelative, isApiError, useHotkey } from '@hc360/core';
import { DataTable, EmptyState, ErrorState, LoadingState, Page, Section, StatusBadge, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { useLetterhead } from '@/lib/letterhead';
import { usePermission } from '@/lib/permissions';
import { PatientBannerWithFlags } from '@/modules/emr/PatientBannerWithFlags';
import type { BatchStock } from '@/modules/inventory/api';
import { sumNumbers } from '@/modules/inventory/decimal';
import { usableQuantity } from '@/modules/inventory/fefo';
import { formatQty, parseNumber } from '@/modules/inventory/format';
import { workflowTone } from '@/modules/inventory/labels';
import { LineTable } from '@/modules/inventory/LineTable';
import { ReasonDialog } from '@/modules/inventory/ReasonDialog';
import { ItemFlags } from '@/modules/inventory/StockBits';
import { StoreProblem, WorkingStorePicker } from '@/modules/inventory/StorePicker';
import { useWorkingStore } from '@/modules/inventory/stores';
import {
  pharmacyApi,
  pharmacyKeys,
  pharmacyQueries,
  type IpIssueCreated,
  type WardIndentDetail,
  type WardIndentLineDto,
} from '@/modules/pharmacy/api';
import { BatchPick } from '@/modules/pharmacy/BatchPick';
import { useItemMaster, usePharmacyBatches } from '@/modules/pharmacy/counter';
import {
  controlledItems,
  itemsFromErrorData,
  planBatchIssue,
  suggestedQuantity,
  type BatchIssuePlan,
} from '@/modules/pharmacy/dispense';
import { IP_READERS, PHARMACY_PERMISSIONS as P } from '@/modules/pharmacy/permissions';
import { ipIssueAmount } from '@/modules/pharmacy/pricing';
import { printIpIssueSlip } from '@/modules/pharmacy/print';

export const Route = createFileRoute('/_app/pharmacy/ward-indents/$indentId')({
  component: WardIndentPage,
});

const useStyles = makeStyles({
  facts: {
    display: 'flex',
    gap: '6px 18px',
    flexWrap: 'wrap',
    alignItems: 'center',
    color: tokens.colorNeutralForeground2,
  },
  muted: { color: tokens.colorNeutralForeground3 },
  problem: { color: tokens.colorPaletteRedForeground1 },
  footer: {
    display: 'flex',
    gap: '12px',
    alignItems: 'center',
    justifyContent: 'flex-end',
    flexWrap: 'wrap',
    padding: '12px 16px',
  },
  done: { display: 'flex', flexDirection: 'column', alignItems: 'center', gap: '6px', textAlign: 'center' },
  big: {
    fontSize: tokens.fontSizeHero800,
    lineHeight: tokens.lineHeightHero800,
    fontWeight: tokens.fontWeightBold,
    color: tokens.colorBrandForeground1,
  },
});

function WardIndentPage() {
  const { indentId } = Route.useParams();
  const ipStore = useWorkingStore('pharmacy-ip', { type: 'Pharmacy' });
  const indent = useQuery({
    ...pharmacyQueries.wardIndent(indentId, ipStore.storeId),
    enabled: !ipStore.loading,
    placeholderData: (previous) => (previous?.id === indentId ? previous : undefined),
  });
  if (indent.isLoading || (!indent.data && ipStore.loading)) {
    return <LoadingState label="Opening the indent…" />;
  }
  if (indent.error && !indent.data) {
    return (
      <Page maxWidth={900}>
        <ErrorState error={indent.error} onRetry={() => void indent.refetch()} />
      </Page>
    );
  }
  return indent.data ? (
    <IndentScreen key={`${indentId}:${ipStore.storeId ?? 'none'}`} indent={indent.data} working={ipStore} />
  ) : null;
}

interface LinePlan {
  line: WardIndentLineDto;
  quantityText: string;
  quantity: number;
  usable: number | null;
  batches: BatchStock[] | undefined;
  batchId: string | null;
  plan: BatchIssuePlan | null;
  amount: number;
}

function IndentScreen({ indent, working }: { indent: WardIndentDetail; working: ReturnType<typeof useWorkingStore> }) {
  const styles = useStyles();
  const notify = useNotify();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const letterhead = useLetterhead();
  const canIssue = usePermission(P.ipIssue);
  const canCancel = usePermission(IP_READERS);
  const storeId = working.storeId;
  const [quantities, setQuantities] = useState<Record<string, string>>({});
  const [batchIds, setBatchIds] = useState<Record<string, string | null>>({});
  const [confirmControlled, setConfirmControlled] = useState(false);
  const [cancelling, setCancelling] = useState(false);
  const [done, setDone] = useState<IpIssueCreated | null>(null);

  const open = indent.status === 'Pending' || indent.status === 'PartiallyIssued';
  const openLines = indent.lines.filter((l) => l.remainingQuantity > 0);
  const itemIds = openLines.map((l) => l.itemId);
  const stock = usePharmacyBatches(open && canIssue ? storeId : null, itemIds);
  const master = useItemMaster(itemIds);
  const issues = useQuery(pharmacyQueries.ipIssues({ admissionId: indent.admissionId }));
  const previousIssues = (issues.data?.items ?? []).filter((i) => i.wardIndentId === indent.id);

  const plans: LinePlan[] = openLines.map((line) => {
    const batches = stock.batches.get(line.itemId);
    const usable = batches ? usableQuantity(batches) : null;
    const suggested = usable === null ? line.remainingQuantity : suggestedQuantity(line.remainingQuantity, usable);
    const quantityText = quantities[line.id] ?? String(suggested);
    const quantity = parseNumber(quantityText) ?? 0;
    const batchId = batchIds[line.id] ?? null;
    const unit = master.items.get(line.itemId)?.baseUnit;
    const plan =
      quantity > 0
        ? planBatchIssue(
            { quantity, remaining: line.remainingQuantity, batchId, minimumShelfLifeDays: 0 },
            batches,
            unit,
          )
        : null;
    const amount = sumNumbers(
      (plan?.allocation.allocations ?? []).map((a) =>
        ipIssueAmount(a.quantity, batches?.find((b) => b.batchId === a.batchId)?.mrp ?? 0),
      ),
    );
    return { line, quantityText, quantity, usable, batches, batchId, plan, amount };
  });
  const active = plans.filter((p) => p.quantity > 0);
  const problems = active.flatMap((p) => (p.plan?.problems ?? []).map((problem) => `${p.line.itemName}: ${problem}`));
  const pending = active.some((p) => p.plan?.pending);
  const total = sumNumbers(active.map((p) => p.amount));

  const issue = useMutation({
    mutationFn: () =>
      pharmacyApi.issueToWard({
        storeId: storeId!,
        wardIndentId: indent.id,
        queueEntryId: null,
        patientId: null,
        admissionId: null,
        visitId: null,
        lines: active.flatMap((p) =>
          (p.plan?.allocation.allocations ?? []).map((a) => ({
            itemId: p.line.itemId,
            quantity: a.quantity,
            batchId: a.batchId,
            wardIndentLineId: p.line.id,
            queueLineId: null,
            substitutionReason: null,
            prescriberConsent: false,
          })),
        ),
        confirmControlledDrugs: confirmControlled,
      }),
    onSuccess: (result) => {
      setDone(result);
      void queryClient.invalidateQueries({ queryKey: pharmacyKeys.ward });
      void queryClient.invalidateQueries({ queryKey: pharmacyKeys.stock });
      void queryClient.invalidateQueries({ queryKey: pharmacyKeys.dashboard });
    },
    onError: (error) => {
      if (isApiError(error) && (error.code?.startsWith('stock.') || error.isConflict)) {
        stock.refetch();
      }
    },
  });

  const serverControlled =
    isApiError(issue.error) && issue.error.code === 'pharmacy.controlled_confirmation_required'
      ? itemsFromErrorData(issue.error.data)
      : [];
  const controlled = Array.from(
    new Set([
      ...controlledItems(
        active.map((p) => {
          const detail = master.items.get(p.line.itemId);
          return {
            name: p.line.itemName,
            schedule: detail?.schedule ?? null,
            isControlled: detail?.isControlled ?? false,
          };
        }),
      ),
      ...serverControlled,
    ]),
  );
  const ready =
    open &&
    canIssue &&
    Boolean(storeId) &&
    active.length > 0 &&
    problems.length === 0 &&
    !pending &&
    (controlled.length === 0 || confirmControlled);
  const submit = () => {
    if (ready && !issue.isPending) {
      issue.mutate();
    }
  };
  useHotkey('f9', submit, { allowInInputs: true, enabled: ready });

  const printSlip = (issueId: string) =>
    void queryClient
      .fetchQuery(pharmacyQueries.ipIssue(issueId))
      .then((detail) => printIpIssueSlip(detail, letterhead, { wardName: indent.wardName, bedLabel: indent.bedLabel }))
      .catch((error: unknown) => notify.error(error, 'The slip did not print'));

  return (
    <Page>
      <PatientBannerWithFlags
        patientId={indent.patient.id}
        patient={{
          id: indent.patient.id,
          uhid: indent.patient.uhid,
          fullName: indent.patient.fullName,
          gender: indent.patient.gender,
          age: indent.patient.age,
          phone: indent.patient.phone,
          bloodGroup: indent.patient.bloodGroup,
          category: indent.patient.category,
          isVip: indent.patient.isVip,
          isDeceased: indent.patient.isDeceased,
        }}
        actions={
          <Button appearance="subtle" onClick={() => void navigate({ to: '/pharmacy/ward-indents' })}>
            All indents
          </Button>
        }
      />
      <div className={styles.facts}>
        <Text weight="semibold">Indent {indent.number}</Text>
        <StatusBadge status={indent.status} tone={workflowTone(indent.status)} />
        {indent.urgency === 'Stat' && (
          <Badge color="danger" icon={<AlertUrgentRegular />}>
            STAT
          </Badge>
        )}
        <Caption1>Ward {[indent.wardName, indent.bedLabel].filter(Boolean).join(' / ') || 'not given'}</Caption1>
        <Caption1>
          Raised {formatRelative(indent.raisedAt)}
          {indent.raisedBy ? ` by ${indent.raisedBy}` : ''} · {formatDateTime(indent.raisedAt)}
        </Caption1>
      </div>
      {indent.remarks && (
        <MessageBar intent="info">
          <MessageBarBody>{indent.remarks}</MessageBarBody>
        </MessageBar>
      )}
      {indent.status === 'Cancelled' && (
        <MessageBar intent="warning">
          <MessageBarBody>
            <MessageBarTitle>Cancelled</MessageBarTitle>
            {indent.cancelReason}
          </MessageBarBody>
        </MessageBar>
      )}
      {open && canIssue && <StoreProblem working={working} kind="pharmacy store" />}

      <Section
        title={open && canIssue ? 'Issue to the ward' : 'Items requested'}
        description={
          open && canIssue
            ? 'Batches are suggested earliest expiry first. Everything issued is charged to the IP account at MRP.'
            : undefined
        }
        actions={
          open && canIssue && working.stores.length > 1 ? (
            <WorkingStorePicker working={working} label="Issue from" />
          ) : undefined
        }
        flush
      >
        {open && canIssue ? (
          <LineTable<LinePlan>
            ariaLabel="Indent lines"
            rows={plans}
            getRowId={(p) => p.line.id}
            isFlagged={(p) => p.quantity > 0 && (p.plan?.problems.length ?? 0) > 0}
            empty={<EmptyState title="Everything on this indent has been issued" />}
            columns={[
              {
                key: 'item',
                header: 'Item',
                render: (p) => {
                  const detail = master.items.get(p.line.itemId);
                  return (
                    <span>
                      <Text weight="semibold">{p.line.itemName}</Text>{' '}
                      {detail && (
                        <ItemFlags
                          schedule={detail.schedule}
                          isControlled={detail.isControlled}
                          isColdChain={detail.isColdChain}
                        />
                      )}
                      {p.line.remarks && <Caption1 block>{p.line.remarks}</Caption1>}
                      {p.quantity > 0 &&
                        (p.plan?.problems ?? []).map((problem) => (
                          <Caption1 key={problem} block className={styles.problem}>
                            <WarningRegular /> {problem}
                          </Caption1>
                        ))}
                    </span>
                  );
                },
              },
              {
                key: 'left',
                header: 'To issue',
                align: 'end',
                width: 110,
                render: (p) => (
                  <span>
                    <Text weight="semibold">{formatQty(p.line.remainingQuantity)}</Text>
                    {p.line.issuedQuantity > 0 && (
                      <Caption1 block className={styles.muted}>
                        {formatQty(p.line.issuedQuantity)} issued
                      </Caption1>
                    )}
                    <Caption1 block className={p.usable === 0 ? styles.problem : styles.muted}>
                      {p.usable === null ? '' : p.usable > 0 ? `${formatQty(p.usable)} in stock` : 'Out of stock'}
                    </Caption1>
                  </span>
                ),
              },
              {
                key: 'give',
                header: 'Issue now',
                width: 120,
                render: (p) => (
                  <Input
                    type="number"
                    min={0}
                    style={{ width: 100 }}
                    aria-label={`Quantity of ${p.line.itemName} to issue`}
                    value={p.quantityText}
                    onChange={(_, d) => setQuantities((prev) => ({ ...prev, [p.line.id]: d.value }))}
                  />
                ),
              },
              {
                key: 'batch',
                header: 'Batch',
                render: (p) =>
                  p.quantity > 0 ? (
                    <BatchPick
                      batches={p.batches}
                      allocations={p.plan?.allocation.allocations ?? []}
                      pickedBatchId={p.batchId}
                      onPick={(batchId) => setBatchIds((prev) => ({ ...prev, [p.line.id]: batchId }))}
                    />
                  ) : (
                    <Caption1 className={styles.muted}>Not now</Caption1>
                  ),
              },
              {
                key: 'amount',
                header: 'Charge',
                align: 'end',
                width: 110,
                render: (p) => (p.quantity > 0 ? formatInr(p.amount) : '—'),
              },
            ]}
          />
        ) : (
          <DataTable
            ariaLabel="Indent lines"
            rows={indent.lines}
            getRowId={(l) => l.id}
            size="small"
            columns={[
              { key: 'item', header: 'Item', render: (l) => <Text weight="semibold">{l.itemName}</Text> },
              { key: 'req', header: 'Requested', align: 'end', render: (l) => formatQty(l.requestedQuantity) },
              { key: 'issued', header: 'Issued', align: 'end', render: (l) => formatQty(l.issuedQuantity) },
              { key: 'left', header: 'Still to come', align: 'end', render: (l) => formatQty(l.remainingQuantity) },
              { key: 'remarks', header: 'Note', priority: 'low', render: (l) => l.remarks ?? '—' },
            ]}
          />
        )}
        {open && canIssue && (
          <div className={styles.footer}>
            {controlled.length > 0 && (
              <Checkbox
                checked={confirmControlled}
                onChange={(_, d) => setConfirmControlled(d.checked === true)}
                label={
                  <span>
                    <ShieldKeyholeRegular /> I have checked the controlled medicines: {controlled.join(', ')}
                  </span>
                }
              />
            )}
            {issue.error && (
              <MessageBar intent="error">
                <MessageBarBody>
                  {isApiError(issue.error) ? issue.error.userMessage : 'Nothing was issued. Try again.'}
                </MessageBarBody>
              </MessageBar>
            )}
            {canCancel && (
              <Button icon={<DismissCircleRegular />} onClick={() => setCancelling(true)}>
                Cancel indent
              </Button>
            )}
            <Button
              appearance="primary"
              size="large"
              icon={issue.isPending ? <Spinner size="tiny" aria-hidden /> : <CheckmarkCircleRegular />}
              disabled={!ready || issue.isPending}
              onClick={submit}
              aria-keyshortcuts="F9"
            >
              Issue {formatInr(total)} to the ward (F9)
            </Button>
          </div>
        )}
        {open && !canIssue && canCancel && (
          <div className={styles.footer}>
            <Button icon={<DismissCircleRegular />} onClick={() => setCancelling(true)}>
              Cancel indent
            </Button>
          </div>
        )}
      </Section>

      {previousIssues.length > 0 && (
        <Section title="Issued so far" flush>
          <DataTable
            ariaLabel="Issues against this indent"
            rows={previousIssues}
            getRowId={(i) => i.id}
            onRowClick={(i) => void navigate({ to: '/pharmacy/ip-issues', search: { id: i.id } })}
            size="small"
            columns={[
              { key: 'no', header: 'Issue', render: (i) => <Text font="monospace">{i.number}</Text> },
              { key: 'at', header: 'When', render: (i) => formatDateTime(i.issuedAt) },
              { key: 'lines', header: 'Lines', align: 'end', render: (i) => i.lineCount },
              { key: 'amount', header: 'Charged', align: 'end', render: (i) => formatInr(i.totalAmount) },
              { key: 'by', header: 'By', priority: 'low', render: (i) => i.issuedBy ?? '—' },
            ]}
          />
        </Section>
      )}

      {cancelling && <CancelIndent indent={indent} onClose={() => setCancelling(false)} />}

      <Dialog open={done !== null} onOpenChange={(_, d) => !d.open && void navigate({ to: '/pharmacy/ward-indents' })}>
        <DialogSurface style={{ maxWidth: 460 }}>
          <DialogBody>
            <DialogTitle>Issued to the ward</DialogTitle>
            <DialogContent>
              <div className={styles.done} role="status">
                <CheckmarkCircleRegular fontSize={40} color={tokens.colorPaletteGreenForeground1} />
                <Text>{indent.patient.fullName}</Text>
                <Caption1>Issue {done?.number}</Caption1>
                <span className={styles.big}>{formatInr(done?.totalAmount)}</span>
                <Caption1>charged to the IP account</Caption1>
              </div>
            </DialogContent>
            <DialogActions fluid>
              <Button icon={<PrintRegular />} onClick={() => done && printSlip(done.id)}>
                Print ward slip
              </Button>
              <Button appearance="primary" autoFocus onClick={() => void navigate({ to: '/pharmacy/ward-indents' })}>
                Back to indents
              </Button>
            </DialogActions>
          </DialogBody>
        </DialogSurface>
      </Dialog>
    </Page>
  );
}

function CancelIndent({ indent, onClose }: { indent: WardIndentDetail; onClose: () => void }) {
  const notify = useNotify();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const cancel = useMutation({
    mutationFn: (reason: string) => pharmacyApi.cancelWardIndent(indent.id, reason),
    onSuccess: () => {
      notify.success(`Indent ${indent.number} cancelled`);
      void queryClient.invalidateQueries({ queryKey: pharmacyKeys.ward });
      void navigate({ to: '/pharmacy/ward-indents' });
    },
  });
  return (
    <ReasonDialog
      title={`Cancel indent ${indent.number}?`}
      confirmLabel="Cancel indent"
      destructive
      busy={cancel.isPending}
      error={cancel.error}
      placeholder="e.g. Doctor stopped the medicine, raised twice"
      onSubmit={(reason) => cancel.mutate(reason)}
      onClose={onClose}
    >
      Whatever was already issued stays issued and charged. The rest will not be supplied.
    </ReasonDialog>
  );
}
