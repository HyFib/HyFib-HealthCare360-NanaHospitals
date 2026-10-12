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
  makeStyles,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  ProgressBar,
  Select,
  Spinner,
  Switch,
  Text,
  Textarea,
  tokens,
} from '@fluentui/react-components';
import {
  ArrowUndoRegular,
  BoxRegular,
  CheckmarkCircleRegular,
  DocumentCheckmarkRegular,
  DoorArrowRightRegular,
  EditRegular,
  HandshakeRegular,
  PrintRegular,
  ReceiptRegular,
  ShieldCheckmarkRegular,
  WarningRegular,
} from '@fluentui/react-icons';
import { formatDateOnly, formatDateTime, formatInr, todayIso, type PayerType } from '@hc360/core';
import {
  ConfirmDialog,
  DataTable,
  ErrorState,
  LoadingState,
  Section,
  StatusBadge,
  useNotify,
  useUnsavedChanges,
  useValuesChanged,
} from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { insuranceQueries } from '@/modules/insurance/api';
import { PAYER_CATEGORY_LABEL, PREAUTH_STATUS_LABEL, preAuthTone } from '@/modules/insurance/labels';
import { billingQueries } from './api';
import { istDateOf } from './dates';
import { useAttemptKey, useBillingPrint, useRefreshBilling } from './hooks';
import { ipApi, ipKeys, ipQueries, type IpAccountDetail, type SettlementResult } from './ipApi';
import { BILL_TYPE_LABEL, modeSummaryLabel, PAYER_TYPE_LABEL, PAYER_TYPES, REFUND_PURPOSE_LABEL } from './labels';
import { mastersQueries } from './mastersApi';
import { amountInputText, formatPercent, fromPaise, maxBig, parseRupees, toPaise } from './money';
import { RequestRefundDialog } from './RefundDialogs';
import {
  ActionError,
  Amount,
  AmountField,
  BillStatusBadge,
  Detail,
  DetailGrid,
  ReasonDialog,
  RefundStatusBadge,
  SummaryList,
  SummaryRow,
} from './ui';

const useStyles = makeStyles({
  stack: { display: 'flex', flexDirection: 'column', gap: '16px' },
  row: { display: 'flex', gap: '8px', flexWrap: 'wrap', alignItems: 'center' },
  body: { display: 'flex', flexDirection: 'column', gap: '12px' },
  muted: { color: tokens.colorNeutralForeground3 },
  cleared: { display: 'flex', alignItems: 'center', gap: '12px' },
});

const isActive = (account: IpAccountDetail) => account.status === 'Open' || account.status === 'DischargeInitiated';

// ------------------------------------------------------------------ bills and advances

/** Every bill of the admission, the advance receipts and the refunds of the account. */
export function IpBillsPanel({ account }: { account: IpAccountDetail }) {
  const styles = useStyles();
  const navigate = useNavigate();
  const canRequestRefund = usePermission('billing.refunds.request');
  const canReadReceipts = usePermission(['billing.charges.read', 'billing.payments.collect']);
  const canReadRefunds = usePermission(['billing.refunds.request', 'billing.refunds.approve', 'billing.charges.read']);
  const { printReceiptById } = useBillingPrint();
  const [refunding, setRefunding] = useState(false);
  const bills = useQuery(billingQueries.bills({ admissionId: account.admissionId, pageSize: 50 }));
  const receipts = useQuery({
    ...billingQueries.receipts({ ipAccountId: account.id, pageSize: 50 }),
    enabled: canReadReceipts,
  });
  const refunds = useQuery({
    ...billingQueries.refunds({ patientId: account.patientId, pageSize: 50 }),
    enabled: canReadRefunds,
  });
  const accountRefunds = (refunds.data?.items ?? []).filter((r) => r.ipAccountId === account.id);
  const pending = accountRefunds
    .filter((r) => r.status === 'Requested' || r.status === 'Approved')
    .reduce((sum, r) => sum + toPaise(r.amount), 0n);
  const refundable = fromPaise(maxBig(toPaise(account.depositBalance) - pending, 0n));

  return (
    <div className={styles.stack}>
      <Section title="Bills of this admission" flush>
        <DataTable
          ariaLabel="Bills of this admission"
          rows={bills.data?.items}
          loading={bills.isLoading}
          error={bills.error}
          onRetry={() => void bills.refetch()}
          getRowId={(b) => b.id}
          onRowClick={(b) => void navigate({ to: '/billing/bills/$billId', params: { billId: b.id } })}
          size="small"
          columns={[
            { key: 'no', header: 'Bill', render: (b) => <Text font="monospace">{b.billNumber ?? 'Draft'}</Text> },
            {
              key: 'kind',
              header: 'Kind',
              render: (b) => (b.isFinalBill ? 'Final bill' : `${BILL_TYPE_LABEL[b.type]} bill`),
            },
            {
              key: 'date',
              header: 'Date',
              priority: 'low',
              render: (b) => formatDateTime(b.finalizedAt ?? b.createdAt),
            },
            { key: 'net', header: 'Total', align: 'end', render: (b) => <Amount value={b.netAmount} /> },
            {
              key: 'payer',
              header: 'Insurer pays',
              align: 'end',
              priority: 'low',
              render: (b) => <Amount value={b.payerAmount} />,
            },
            {
              key: 'due',
              header: 'Due',
              align: 'end',
              render: (b) => <Amount value={b.dueAmount} strong tone={b.dueAmount > 0 ? 'negative' : 'muted'} />,
            },
            { key: 'status', header: 'Status', render: (b) => <BillStatusBadge status={b.status} /> },
          ]}
          emptyTitle="No bills yet"
          emptyDescription="Raise an interim bill during the stay, or prepare the final bill at discharge."
        />
      </Section>

      {canReadReceipts && (
        <Section
          title="Advance and payments"
          description={`Received ${formatInr(account.depositReceived)} · adjusted against bills ${formatInr(account.depositAdjusted)} · refunded ${formatInr(account.depositRefunded)}`}
          actions={
            canRequestRefund &&
            refundable > 0 &&
            !isActive(account) && (
              <Button icon={<ArrowUndoRegular />} onClick={() => setRefunding(true)}>
                Refund unused advance
              </Button>
            )
          }
          flush
        >
          <DataTable
            ariaLabel="Advance receipts"
            rows={receipts.data?.items}
            loading={receipts.isLoading}
            error={receipts.error}
            onRetry={() => void receipts.refetch()}
            getRowId={(r) => r.id}
            size="small"
            columns={[
              { key: 'no', header: 'Receipt', render: (r) => <Text font="monospace">{r.receiptNumber}</Text> },
              { key: 'at', header: 'Received', render: (r) => formatDateTime(r.receivedAt) },
              {
                key: 'purpose',
                header: 'For',
                priority: 'low',
                render: (r) => (r.purpose === 'Deposit' ? 'Advance' : r.billId ? 'Bill payment' : r.purpose),
              },
              { key: 'mode', header: 'Mode', render: (r) => modeSummaryLabel(r.modes) },
              { key: 'amount', header: 'Amount', align: 'end', render: (r) => <Amount value={r.amount} strong /> },
            ]}
            rowActions={(r) => (
              <Button
                size="small"
                appearance="subtle"
                icon={<PrintRegular />}
                aria-label={`Reprint ${r.receiptNumber}`}
                onClick={() => void printReceiptById(r.id)}
              />
            )}
            emptyTitle="No advance received yet"
          />
        </Section>
      )}

      {canReadRefunds && accountRefunds.length > 0 && (
        <Section title="Refunds of the advance" flush>
          <DataTable
            ariaLabel="Refunds of the advance"
            rows={accountRefunds}
            getRowId={(r) => r.id}
            size="small"
            onRowClick={(r) => void navigate({ to: '/billing/refunds/$refundId', params: { refundId: r.id } })}
            columns={[
              { key: 'no', header: 'Refund', render: (r) => <Text font="monospace">{r.refundNumber}</Text> },
              { key: 'purpose', header: 'For', priority: 'low', render: (r) => REFUND_PURPOSE_LABEL[r.purpose] },
              { key: 'at', header: 'Requested', render: (r) => formatDateTime(r.requestedAt) },
              { key: 'amount', header: 'Amount', align: 'end', render: (r) => <Amount value={r.amount} strong /> },
              { key: 'status', header: 'Status', render: (r) => <RefundStatusBadge status={r.status} /> },
            ]}
          />
        </Section>
      )}
      {refunding && (
        <RequestRefundDialog
          open
          target={{
            kind: 'deposit',
            ipAccountId: account.id,
            max: refundable,
            label: `Unused advance of admission ${account.admissionNumber}`,
          }}
          onClose={() => setRefunding(false)}
        />
      )}
    </div>
  );
}

// ------------------------------------------------------------------ package

/** Package vs actuals (IPB-04): what the package absorbs, the excess billed separately and the hospital's gain or loss. */
export function IpPackagePanel({ account }: { account: IpAccountDetail }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const refresh = useRefreshBilling();
  const canManage = usePermission('billing.ip.manage');
  const status = useQuery(ipQueries.packageStatus(account.id));
  const packages = useQuery({ ...mastersQueries.packages(), enabled: canManage });
  const [choice, setChoice] = useState<string | null | undefined>(undefined);

  const assign = useMutation({
    mutationFn: (packageId: string | null) => ipApi.assignPackage(account.id, packageId, account.version),
    onSuccess: (_, packageId) => {
      notify.success(packageId ? 'Package assigned' : 'Package removed', 'The draft final bill was prepared again.');
      setChoice(undefined);
      void queryClient.invalidateQueries({ queryKey: ipKeys.account(account.id) });
      void refresh();
    },
  });

  if (status.isLoading) {
    return <LoadingState />;
  }
  if (status.error) {
    return <ErrorState error={status.error} onRetry={() => void status.refetch()} />;
  }
  const p = status.data;
  const chosenName = packages.data?.find((x) => x.id === choice)?.name;

  return (
    <div className={styles.stack}>
      <Section
        title={p ? `${p.name} (${p.code})` : 'No package'}
        description={p ? 'Package price against the actual services of the stay' : 'Services are billed item by item.'}
        actions={
          canManage &&
          isActive(account) && (
            <div className={styles.row}>
              <Select
                value={choice ?? ''}
                onChange={(_, d) => setChoice(d.value || undefined)}
                aria-label="Choose a package"
              >
                <option value="">{p ? 'Change package…' : 'Choose a package…'}</option>
                {(packages.data ?? [])
                  .filter((x) => x.id !== account.packageId && x.usedFor !== 'OutPatient')
                  .map((x) => (
                    <option key={x.id} value={x.id}>
                      {x.name} · {formatInr(x.price)}
                    </option>
                  ))}
              </Select>
              {p && (
                <Button icon={<BoxRegular />} onClick={() => setChoice(null)}>
                  Remove package
                </Button>
              )}
            </div>
          )
        }
      >
        {p ? (
          <div className={styles.stack}>
            <DetailGrid>
              <Detail label="Package price">
                <Amount value={p.packagePrice} size="large" />
              </Detail>
              <Detail label="Actual services">
                <Amount value={p.actuals} size="large" />
              </Detail>
              <Detail label="Covered by the package">
                <Amount value={p.covered} />
              </Detail>
              <Detail label="Billed beyond the package">
                <Amount value={p.excess} tone={p.excess > 0 ? 'negative' : undefined} />
              </Detail>
              <Detail label={p.variance >= 0 ? 'Hospital gain' : 'Hospital loss'}>
                <Amount value={Math.abs(p.variance)} tone={p.variance >= 0 ? 'positive' : 'negative'} />
              </Detail>
            </DetailGrid>
            <Field
              label={`Package used: ${p.packagePrice > 0 ? formatPercent(Math.min((p.covered / p.packagePrice) * 100, 999)) : '—'}`}
            >
              <ProgressBar
                value={p.packagePrice > 0 ? Math.min(p.covered / p.packagePrice, 1) : 0}
                color={p.covered > p.packagePrice ? 'error' : 'brand'}
                thickness="large"
                aria-label="Share of the package used"
              />
            </Field>
            <DataTable
              ariaLabel="Package utilisation by service"
              rows={p.lines}
              getRowId={(l) => l.chargeId}
              size="small"
              columns={[
                { key: 'service', header: 'Service', render: (l) => `${l.description} (${l.serviceCode})` },
                { key: 'gross', header: 'Amount', align: 'end', render: (l) => <Amount value={l.gross} /> },
                { key: 'covered', header: 'Covered', align: 'end', render: (l) => <Amount value={l.covered} /> },
                {
                  key: 'excess',
                  header: 'Excess',
                  align: 'end',
                  render: (l) => <Amount value={l.excess} tone={l.excess > 0 ? 'negative' : 'muted'} />,
                },
              ]}
              emptyTitle="No services charged yet"
            />
          </div>
        ) : (
          <Text className={styles.muted}>
            Assign a package (for example a normal delivery or a fixed-price surgery) to bill the stay at the package
            price.
          </Text>
        )}
      </Section>
      <ConfirmDialog
        open={choice !== undefined}
        title={choice ? `Assign ${chosenName ?? 'this package'}?` : 'Remove the package?'}
        confirmLabel={choice ? 'Assign package' : 'Remove package'}
        destructive={choice === null}
        busy={assign.isPending}
        onConfirm={() => assign.mutate(choice ?? null)}
        onCancel={() => {
          setChoice(undefined);
          assign.reset();
        }}
      >
        {choice
          ? 'The stay is billed at the package price; services beyond its inclusions are billed separately.'
          : 'Every service of the stay is billed item by item again.'}
        <ActionError error={assign.error} />
      </ConfirmDialog>
    </div>
  );
}

// ------------------------------------------------------------------ payer and insurance

const THIRD_PARTY: readonly string[] = ['Insurance', 'Corporate', 'GovernmentScheme'];

/** Who pays for the stay: payer, tariff, policy and pre-authorisations; changing the payer can re-price unbilled services. */
export function IpPayerPanel({ account }: { account: IpAccountDetail }) {
  const styles = useStyles();
  const navigate = useNavigate();
  const canManage = usePermission('billing.ip.manage');
  const canSeePreAuths = usePermission(['insurance.preauth.manage', 'insurance.claims.manage', 'billing.ip.manage']);
  const [editing, setEditing] = useState(false);
  const policy = useQuery({ ...insuranceQueries.policy(account.policyId ?? ''), enabled: Boolean(account.policyId) });
  const preAuths = useQuery({
    ...insuranceQueries.preAuths({ admissionId: account.admissionId }),
    enabled: canSeePreAuths,
  });

  return (
    <div className={styles.stack}>
      <Section
        title="Payer"
        actions={
          canManage &&
          isActive(account) && (
            <Button icon={<EditRegular />} onClick={() => setEditing(true)}>
              Change payer or estimate
            </Button>
          )
        }
      >
        <DetailGrid>
          <Detail label="Paid by">{PAYER_TYPE_LABEL[account.payerType]}</Detail>
          <Detail label="Insurer / corporate / scheme">{account.payerName}</Detail>
          <Detail label="Tariff">{account.tariffPlanName}</Detail>
          <Detail label="Policy">
            {policy.data
              ? `${policy.data.payerName ?? ''} ${policy.data.policyNumberMasked}${policy.data.tpaName ? ` · TPA ${policy.data.tpaName}` : ''}`
              : account.policyId
                ? '…'
                : null}
          </Detail>
          <Detail label="Estimate given">
            <Amount value={account.estimatedAmount} />
          </Detail>
          <Detail label="Room class (for pricing)">{account.bedServiceCode}</Detail>
          <Detail label="Attending doctor">{account.attendingPractitionerName}</Detail>
          <Detail label="Notes">{account.notes}</Detail>
        </DetailGrid>
      </Section>
      {canSeePreAuths && (
        <Section title="Pre-authorisations" description="Cashless approvals of the insurer for this admission" flush>
          <DataTable
            ariaLabel="Pre-authorisations of this admission"
            rows={preAuths.data?.items}
            loading={preAuths.isLoading}
            error={preAuths.error}
            onRetry={() => void preAuths.refetch()}
            getRowId={(p) => p.id}
            onRowClick={(p) => void navigate({ to: '/insurance/preauths/$preAuthId', params: { preAuthId: p.id } })}
            size="small"
            columns={[
              { key: 'no', header: 'Number', render: (p) => <Text font="monospace">{p.number || 'Draft'}</Text> },
              { key: 'payer', header: 'Insurer', render: (p) => p.payerName ?? '—' },
              {
                key: 'requested',
                header: 'Requested',
                align: 'end',
                render: (p) => <Amount value={p.requestedAmount} />,
              },
              {
                key: 'approved',
                header: 'Approved',
                align: 'end',
                render: (p) => <Amount value={p.effectiveApprovedAmount} strong />,
              },
              {
                key: 'status',
                header: 'Status',
                render: (p) => (
                  <StatusBadge status={p.status} tone={preAuthTone(p.status)} label={PREAUTH_STATUS_LABEL[p.status]} />
                ),
              },
            ]}
            emptyTitle="No pre-authorisation"
            emptyDescription={
              account.payerType === 'Insurance'
                ? 'The insurance desk requests cashless approval from the Insurance screens.'
                : undefined
            }
          />
        </Section>
      )}
      {editing && <IpPayerDialog account={account} onClose={() => setEditing(false)} />}
    </div>
  );
}

function IpPayerDialog({ account, onClose }: { account: IpAccountDetail; onClose: () => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const refresh = useRefreshBilling();
  const [payerType, setPayerType] = useState<PayerType>(account.payerType);
  const [payerId, setPayerId] = useState(account.payerId ?? '');
  const [tariffPlanId, setTariffPlanId] = useState(account.tariffPlanId ?? '');
  const [policyId, setPolicyId] = useState(account.policyId ?? '');
  const [estimate, setEstimate] = useState(amountInputText(account.estimatedAmount));
  const [reprice, setReprice] = useState(true);
  const [notes, setNotes] = useState(account.notes ?? '');
  const needsPayer = THIRD_PARTY.includes(payerType);
  const payers = useQuery({ ...insuranceQueries.payers(), enabled: needsPayer });
  const plans = useQuery(mastersQueries.tariffPlans());
  const policies = useQuery({ ...insuranceQueries.policies(account.patientId), enabled: payerType === 'Insurance' });
  const parsedEstimate = parseRupees(estimate, { allowZero: true });
  const payerOptions = (payers.data ?? []).filter((p) =>
    payerType === 'Insurance'
      ? p.category === 'Insurer' || p.category === 'Tpa'
      : payerType === 'Corporate'
        ? p.category === 'Corporate'
        : p.category === 'GovernmentScheme',
  );
  const valid = parsedEstimate.ok && (!needsPayer || payerId !== '');
  const changesPricing =
    payerType !== account.payerType ||
    (payerId || null) !== account.payerId ||
    (tariffPlanId || null) !== account.tariffPlanId;

  const save = useMutation({
    mutationFn: () =>
      ipApi.update(account.id, {
        estimatedAmount: parsedEstimate.ok ? parsedEstimate.rupees : 0,
        payerType,
        payerId: needsPayer ? payerId || null : null,
        tariffPlanId: tariffPlanId || null,
        policyId: payerType === 'Insurance' ? policyId || null : null,
        repriceUnbilledCharges: changesPricing && reprice,
        notes: notes.trim() || null,
        version: account.version,
      }),
    onSuccess: () => {
      notify.success(
        'IP account updated',
        changesPricing && reprice ? 'Unbilled services were priced again on the new tariff.' : undefined,
      );
      void queryClient.invalidateQueries({ queryKey: ipKeys.account(account.id) });
      void refresh();
      onClose();
    },
  });

  const guard = useUnsavedChanges({
    dirty: useValuesChanged({ payerType, payerId, tariffPlanId, policyId, estimate, reprice, notes }),
    busy: save.isPending,
    onClose,
  });

  return (
    <Dialog open onOpenChange={guard.onOpenChange}>
      <DialogSurface style={{ maxWidth: 600 }}>
        <form
          onSubmit={(event) => {
            event.preventDefault();
            if (valid && !save.isPending) {
              save.mutate();
            }
          }}
        >
          <DialogBody>
            <DialogTitle>Payer and estimate</DialogTitle>
            <DialogContent className={styles.body}>
              <Field label="Paid by" required>
                <Select value={payerType} onChange={(_, d) => setPayerType(d.value as PayerType)}>
                  {PAYER_TYPES.map((t) => (
                    <option key={t} value={t}>
                      {PAYER_TYPE_LABEL[t]}
                    </option>
                  ))}
                </Select>
              </Field>
              {needsPayer && (
                <Field label="Insurer, corporate or scheme" required>
                  <Select value={payerId} onChange={(_, d) => setPayerId(d.value)}>
                    <option value="">Choose…</option>
                    {payerOptions.map((p) => (
                      <option key={p.id} value={p.id}>
                        {p.name} ({PAYER_CATEGORY_LABEL[p.category]})
                      </option>
                    ))}
                  </Select>
                </Field>
              )}
              {payerType === 'Insurance' && (
                <Field label="Policy" hint="Record new policies on the Insurance → Policies screen">
                  <Select value={policyId} onChange={(_, d) => setPolicyId(d.value)}>
                    <option value="">No policy linked</option>
                    {(policies.data ?? []).map((p) => (
                      <option key={p.id} value={p.id}>
                        {p.payerName} · {p.policyNumberMasked} · valid to {formatDateOnly(p.validTo)}
                        {p.isValidToday ? '' : ' (not valid today)'}
                      </option>
                    ))}
                  </Select>
                </Field>
              )}
              <Field label="Tariff" hint="Leave as is to use the payer's tariff">
                <Select value={tariffPlanId} onChange={(_, d) => setTariffPlanId(d.value)}>
                  <option value="">Default for the payer</option>
                  {(plans.data ?? []).map((p) => (
                    <option key={p.id} value={p.id}>
                      {p.name}
                    </option>
                  ))}
                </Select>
              </Field>
              <AmountField
                label="Estimate given to the patient"
                value={estimate}
                onChange={setEstimate}
                error={parsedEstimate.ok ? null : parsedEstimate.error}
              />
              {changesPricing && (
                <Switch
                  checked={reprice}
                  onChange={(_, d) => setReprice(d.checked)}
                  label="Price the services not yet billed again on the new tariff"
                />
              )}
              <Field label="Notes">
                <Textarea value={notes} onChange={(_, d) => setNotes(d.value)} maxLength={1000} rows={2} />
              </Field>
              <ActionError error={save.error} />
            </DialogContent>
            <DialogActions>
              <Button onClick={guard.requestClose} disabled={save.isPending}>
                Cancel
              </Button>
              <Button
                type="submit"
                appearance="primary"
                disabled={!valid || save.isPending}
                icon={save.isPending ? <Spinner size="tiny" /> : undefined}
              >
                Save
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
        {guard.dialogs}
      </DialogSurface>
    </Dialog>
  );
}

// ------------------------------------------------------------------ discharge billing

/**
 * Discharge billing (IPB-05..08): interim bills during the stay, the final bill, settlement of the advance, and the
 * clearance the ward waits for — or management's approval to discharge with dues.
 */
export function IpDischargePanel({ account }: { account: IpAccountDetail }) {
  const styles = useStyles();
  const notify = useNotify();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const refresh = useRefreshBilling();
  const canManage = usePermission('billing.ip.manage');
  const canApproveDues = usePermission('billing.discounts.approve');
  const [settleKey, renewSettleKey] = useAttemptKey();
  const [dialog, setDialog] = useState<'interim' | 'settle' | 'dues' | null>(null);
  const [upTo, setUpTo] = useState('');
  const [settlement, setSettlement] = useState<SettlementResult | null>(null);
  const clearance = useQuery({ ...ipQueries.clearance(account.id), refetchInterval: 60_000 });

  const afterChange = () => {
    void queryClient.invalidateQueries({ queryKey: ipKeys.account(account.id) });
    void refresh();
  };

  const interim = useMutation({
    mutationFn: () => ipApi.createInterimBill(account.id, upTo || null),
    onSuccess: (bill) => {
      afterChange();
      notify.success('Interim bill drafted', 'Check it, then finalise and collect.');
      void navigate({ to: '/billing/bills/$billId', params: { billId: bill.id } });
    },
  });
  const finalBill = useMutation({
    mutationFn: () => ipApi.prepareFinalBill(account.id),
    onSuccess: (bill) => {
      afterChange();
      notify.success(
        bill.isFinalBill ? 'Final bill prepared' : 'Supplementary bill prepared',
        'Check it, apply any discount, then finalise.',
      );
      void navigate({ to: '/billing/bills/$billId', params: { billId: bill.id } });
    },
  });
  const settle = useMutation({
    mutationFn: () => ipApi.settle(account.id, settleKey),
    onSuccess: (result) => {
      renewSettleKey();
      setDialog(null);
      setSettlement(result);
      afterChange();
    },
  });
  const approveDues = useMutation({
    mutationFn: (reason: string) => ipApi.approveDischargeWithDues(account.id, reason),
    onSuccess: () => {
      setDialog(null);
      notify.warning(
        'Discharge with dues approved',
        'The ward can discharge the patient; the dues stay on the account.',
      );
      afterChange();
    },
  });

  const c = clearance.data;
  return (
    <div className={styles.stack}>
      <Section title="Discharge clearance">
        {clearance.isLoading ? (
          <LoadingState />
        ) : clearance.error || !c ? (
          <ErrorState error={clearance.error} onRetry={() => void clearance.refetch()} />
        ) : (
          <div className={styles.stack}>
            <div className={styles.cleared} role="status">
              {c.cleared ? (
                <CheckmarkCircleRegular fontSize={36} color={tokens.colorPaletteGreenForeground1} />
              ) : (
                <WarningRegular fontSize={36} color={tokens.colorPaletteMarigoldForeground1} />
              )}
              <span>
                <Text size={500} weight="semibold">
                  {c.cleared ? 'Cleared for discharge' : 'Not cleared yet'}
                </Text>
                {c.reason && (
                  <Caption1 block className={styles.muted}>
                    {c.reason}
                  </Caption1>
                )}
              </span>
            </div>
            <SummaryList>
              <SummaryRow
                label="Services not billed yet"
                hint={c.unbilledCount > 0 ? `${c.unbilledCount} item(s)` : undefined}
              >
                <Amount value={c.unbilledAmount} />
              </SummaryRow>
              <SummaryRow label="Final bill">
                {c.hasFinalBill ? (
                  <Badge appearance="tint" color="success">
                    Finalised
                  </Badge>
                ) : (
                  <Badge appearance="tint" color="warning">
                    Not finalised
                  </Badge>
                )}
              </SummaryRow>
              <SummaryRow label="Patient's share still due">
                <Amount value={c.patientDue} />
              </SummaryRow>
              <SummaryRow label="Advance held">
                <Amount value={c.depositBalance} />
              </SummaryRow>
              <SummaryRow label="Outstanding" strong>
                <Amount value={c.outstanding} strong tone={c.outstanding > 0 ? 'negative' : 'positive'} />
              </SummaryRow>
            </SummaryList>
            {c.dischargeWithDuesApproved && (
              <MessageBar intent="warning">
                <MessageBarBody>
                  <MessageBarTitle>Discharge with dues approved</MessageBarTitle>
                  {account.dischargeApprovalReason}
                </MessageBarBody>
              </MessageBar>
            )}
          </div>
        )}
      </Section>

      {canManage && (
        <Section
          title="Billing steps"
          description="Interim bills are for self-pay stays; insured and package stays are billed on the final bill."
        >
          <div className={styles.row}>
            {isActive(account) && (
              <Button icon={<ReceiptRegular />} onClick={() => setDialog('interim')} disabled={interim.isPending}>
                Interim bill
              </Button>
            )}
            <Button
              appearance="primary"
              icon={finalBill.isPending ? <Spinner size="tiny" /> : <DocumentCheckmarkRegular />}
              onClick={() => finalBill.mutate()}
              disabled={finalBill.isPending}
            >
              {c?.hasFinalBill ? 'Bill late services' : 'Prepare final bill'}
            </Button>
            <Button icon={<HandshakeRegular />} onClick={() => setDialog('settle')} disabled={!c?.hasFinalBill}>
              Settle with the advance
            </Button>
            {canApproveDues && c && !c.cleared && !c.dischargeWithDuesApproved && (
              <Button icon={<ShieldCheckmarkRegular />} onClick={() => setDialog('dues')}>
                Approve discharge with dues
              </Button>
            )}
          </div>
          <ActionError error={finalBill.error ?? interim.error} />
        </Section>
      )}

      {settlement && (
        <MessageBar intent={settlement.remainingDue > 0 ? 'warning' : 'success'} layout="multiline">
          <MessageBarBody>
            <MessageBarTitle>Account settled</MessageBarTitle>
            {formatInr(settlement.advanceAdjusted)} of the advance was adjusted against the bills.{' '}
            {settlement.remainingDue > 0
              ? `${formatInr(settlement.remainingDue)} is still to be collected.`
              : 'Nothing is left to collect.'}{' '}
            {settlement.refundRequested > 0
              ? `A refund of ${formatInr(settlement.refundRequested)} (unused advance) now waits for approval.`
              : ''}
          </MessageBarBody>
          <div className={styles.row}>
            <Button
              icon={<DoorArrowRightRegular />}
              onClick={() =>
                void navigate({ to: '/billing/bills/$billId', params: { billId: settlement.finalBillId } })
              }
            >
              Open the final bill
            </Button>
            {settlement.refundId && (
              <Button
                icon={<ArrowUndoRegular />}
                onClick={() =>
                  void navigate({ to: '/billing/refunds/$refundId', params: { refundId: settlement.refundId! } })
                }
              >
                Open the refund
              </Button>
            )}
          </div>
        </MessageBar>
      )}

      <Dialog open={dialog === 'interim'} onOpenChange={(_, d) => !d.open && !interim.isPending && setDialog(null)}>
        <DialogSurface style={{ maxWidth: 460 }}>
          <DialogBody>
            <DialogTitle>Interim bill</DialogTitle>
            <DialogContent className={styles.body}>
              <Text>Bills the services charged so far (or up to a date) so the family can pay during the stay.</Text>
              <Field label="Services up to" hint="Leave empty for everything charged so far">
                <Input
                  type="date"
                  value={upTo}
                  max={todayIso()}
                  min={istDateOf(account.admittedAt)}
                  onChange={(_, d) => setUpTo(d.value)}
                />
              </Field>
              <ActionError error={interim.error} />
            </DialogContent>
            <DialogActions>
              <Button onClick={() => setDialog(null)} disabled={interim.isPending}>
                Cancel
              </Button>
              <Button
                appearance="primary"
                onClick={() => interim.mutate()}
                disabled={interim.isPending}
                icon={interim.isPending ? <Spinner size="tiny" /> : undefined}
              >
                Draft interim bill
              </Button>
            </DialogActions>
          </DialogBody>
        </DialogSurface>
      </Dialog>

      <ConfirmDialog
        open={dialog === 'settle'}
        title="Settle the account with the advance?"
        confirmLabel="Settle"
        busy={settle.isPending}
        onConfirm={() => settle.mutate()}
        onCancel={() => {
          setDialog(null);
          settle.reset();
        }}
      >
        <Text block>
          The advance of <Amount value={account.depositBalance} strong /> is adjusted against the open bills of this
          admission, oldest first. Any unused advance becomes a refund request for approval.
        </Text>
        <ActionError error={settle.error} />
      </ConfirmDialog>

      <ReasonDialog
        open={dialog === 'dues'}
        title="Approve discharge with dues?"
        description={
          c
            ? `${formatInr(c.outstanding)} is outstanding. The patient may leave; the dues stay on the account and are recorded against your name.`
            : undefined
        }
        confirmLabel="Approve discharge"
        destructive
        busy={approveDues.isPending}
        error={approveDues.error}
        onConfirm={(reason) => approveDues.mutate(reason)}
        onCancel={() => setDialog(null)}
      />
    </div>
  );
}
