import {
  Button,
  Caption1,
  Field,
  Input,
  makeStyles,
  Menu,
  MenuItem,
  MenuList,
  MenuPopover,
  MenuTrigger,
  Select,
  Tab,
  TabList,
  Text,
  tokens,
  useRestoreFocusTarget,
} from '@fluentui/react-components';
import {
  AddRegular,
  CheckmarkCircleRegular,
  DismissCircleRegular,
  DocumentEditRegular,
  MoreHorizontalRegular,
  ReceiptRegular,
  SendRegular,
} from '@fluentui/react-icons';
import { formatDateOnly, formatDateTime, formatInr, todayIso } from '@hc360/core';
import { DataTable, StatusBadge, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import {
  deadlineText,
  DISPUTE_SOURCE_LABEL,
  DISPUTE_STATUS_LABEL,
  disputeApi,
  disputeKeys,
  disputeQueries,
  gatewayText,
  nextStep,
  type PaymentDisputeDto,
  type PaymentDisputeStatus,
} from './disputesApi';
import { PAYMENT_MODE_LABEL } from './labels';
import { Amount, ReasonDialog } from './ui';

const useStyles = makeStyles({
  muted: { color: tokens.colorNeutralForeground3 },
  stack: { display: 'flex', flexDirection: 'column', gap: '2px', minWidth: 0 },
  toolbar: { display: 'flex', flexWrap: 'wrap', alignItems: 'center', gap: '8px', padding: '8px 16px 12px' },
  late: { color: tokens.colorPaletteRedForeground1 },
  fields: { display: 'grid', gap: '12px', gridTemplateColumns: 'repeat(auto-fit, minmax(160px, 1fr))' },
});

const STATUS_TONE: Record<PaymentDisputeStatus, 'warning' | 'informative' | 'success' | 'danger'> = {
  Open: 'warning',
  Contested: 'informative',
  Won: 'success',
  Lost: 'danger',
};

type Action = 'describe' | 'evidence' | 'won' | 'accept';

/**
 * Chargebacks on the payments of one channel (`terminal`: the card and UPI terminals; otherwise the gateway): open cases
 * by their deadline, or the closed ones. An accountant records the bank's notice, the evidence sent, a win, or asks to
 * accept the loss (a receipt reversal someone else approves); a case can be opened from the bank's notice.
 */
export function PaymentDisputes({ terminal }: { terminal: boolean }) {
  const styles = useStyles();
  const notify = useNotify();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const restoreFocusTarget = useRestoreFocusTarget();
  const [open, setOpen] = useState(true);
  const [page, setPage] = useState(1);
  const [acting, setActing] = useState<{ action: Action; dispute: PaymentDisputeDto } | null>(null);
  const [opening, setOpening] = useState(false);
  const [reasonCode, setReasonCode] = useState('');
  const [respondBy, setRespondBy] = useState('');
  const [sentOn, setSentOn] = useState(todayIso());
  const [receiptNumber, setReceiptNumber] = useState('');
  const [lookedUp, setLookedUp] = useState('');
  const [paymentId, setPaymentId] = useState('');
  const [amount, setAmount] = useState('');
  const list = useQuery(disputeQueries.list({ open, terminal, page }));
  // A receipt paid in more than one card, UPI or online payment: the accountant chooses the one the notice is about.
  const lines = useQuery({ ...disputeQueries.paymentLines(lookedUp), enabled: opening && lookedUp !== '' });
  const choosing = (lines.data?.length ?? 0) > 1;
  const refresh = () => void queryClient.invalidateQueries({ queryKey: disputeKeys.all });

  const act = useMutation({
    mutationFn: async ({ action, dispute, note }: { action: Action; dispute: PaymentDisputeDto; note: string }) => {
      switch (action) {
        case 'describe':
          await disputeApi.describe(dispute.id, {
            reasonCode: reasonCode.trim() || null,
            // The notice's words already recorded stay unless new ones are typed.
            reason: note || dispute.reason,
            respondBy: respondBy || null,
            version: dispute.version,
          });
          return "The bank's notice is recorded.";
        case 'evidence':
          await disputeApi.evidence(dispute.id, { sentOn, note, version: dispute.version });
          return 'The case waits for the bank to decide.';
        case 'won':
          await disputeApi.won(dispute.id, note, dispute.version);
          return 'The case is closed as won.';
        case 'accept':
          await disputeApi.acceptLoss(dispute.id, note, dispute.version);
          return 'Someone else who approves reversals decides; the receipt is reversed when they approve.';
      }
    },
    onSuccess: (message) => {
      notify.success(`Chargeback ${acting?.dispute.number ?? ''}`.trim(), message);
      setActing(null);
      refresh();
    },
  });
  const openCase = useMutation({
    mutationFn: (note: string) =>
      disputeApi.open({
        receiptNumber: receiptNumber.trim(),
        paymentId: choosing && paymentId ? paymentId : null,
        amount: Number(amount),
        reasonCode: reasonCode.trim() || null,
        reason: note || null,
        respondBy: respondBy || null,
      }),
    onSuccess: (dispute) => {
      notify.success(`Chargeback ${dispute.number} opened`, `Receipt ${dispute.receiptNumber}.`);
      setOpening(false);
      refresh();
    },
  });

  const start = (action: Action, dispute: PaymentDisputeDto) => {
    act.reset();
    setReasonCode(dispute.reasonCode ?? '');
    setRespondBy(dispute.respondBy ?? '');
    setSentOn(todayIso());
    setActing({ action, dispute });
  };
  const closed = (d: PaymentDisputeDto) => d.status === 'Won' || d.status === 'Lost';

  return (
    <>
      <div className={styles.toolbar}>
        <TabList
          size="small"
          selectedValue={open ? 'open' : 'closed'}
          onTabSelect={(_, d) => {
            setOpen(d.value === 'open');
            setPage(1);
          }}
        >
          <Tab value="open">Open</Tab>
          <Tab value="closed">Closed</Tab>
        </TabList>
        <Button
          icon={<AddRegular />}
          onClick={() => {
            openCase.reset();
            setReceiptNumber('');
            setLookedUp('');
            setPaymentId('');
            setAmount('');
            setReasonCode('');
            setRespondBy('');
            setOpening(true);
          }}
        >
          Open a case…
        </Button>
        <Caption1 className={styles.muted}>
          HC360 opens a case when a settlement takes money back on a payment it recorded; open one yourself from the
          bank&apos;s notice.
        </Caption1>
      </div>
      <DataTable
        ariaLabel={open ? 'Open chargebacks' : 'Closed chargebacks'}
        rows={list.data?.items}
        loading={list.isLoading}
        error={list.error}
        onRetry={() => void list.refetch()}
        getRowId={(d) => d.id}
        columns={[
          {
            key: 'case',
            header: 'Case',
            render: (d) => (
              <span className={styles.stack}>
                <Text weight="semibold">{d.number}</Text>
                <Caption1 className={styles.muted}>{DISPUTE_SOURCE_LABEL[d.source]}</Caption1>
                {d.gatewayDisputeId && <Caption1 className={styles.muted}>{gatewayText(d)}</Caption1>}
              </span>
            ),
          },
          {
            key: 'payment',
            header: 'Payment',
            render: (d) => (
              <span className={styles.stack}>
                <Text>Receipt {d.receiptNumber}</Text>
                <Caption1 className={styles.muted}>
                  {[d.paymentReference, d.reasonCode && `Reason ${d.reasonCode}`].filter(Boolean).join(' · ') ||
                    'No reference'}
                </Caption1>
              </span>
            ),
          },
          {
            key: 'state',
            header: 'Where it stands',
            render: (d) => {
              const deadline = deadlineText(d, formatDateOnly);
              return (
                <span className={styles.stack}>
                  <StatusBadge status={d.status} tone={STATUS_TONE[d.status]} label={DISPUTE_STATUS_LABEL[d.status]} />
                  {deadline && (
                    <Caption1 className={d.daysLeft !== null && d.daysLeft < 0 ? styles.late : styles.muted}>
                      {deadline}
                    </Caption1>
                  )}
                  <Caption1 className={styles.muted}>
                    {closed(d) ? (d.outcome ?? nextStep(d)) : nextStep(d)}
                    {d.evidenceSentOn && !closed(d) && ` · Evidence sent ${formatDateOnly(d.evidenceSentOn)}`}
                    {closed(d) && d.closedAt && ` · ${formatDateTime(d.closedAt)}`}
                  </Caption1>
                </span>
              );
            },
          },
          {
            key: 'amount',
            header: 'Taken back',
            align: 'end',
            width: 130,
            render: (d) => <Amount value={d.amount} strong />,
          },
        ]}
        rowActions={(d) => (
          <Menu>
            <MenuTrigger disableButtonEnhancement>
              <Button
                size="small"
                appearance="subtle"
                icon={<MoreHorizontalRegular />}
                aria-label={`Actions for chargeback ${d.number}`}
                {...restoreFocusTarget}
              />
            </MenuTrigger>
            <MenuPopover>
              <MenuList>
                {!closed(d) && (
                  <>
                    <MenuItem icon={<DocumentEditRegular />} onClick={() => start('describe', d)}>
                      Record the bank&apos;s notice…
                    </MenuItem>
                    <MenuItem icon={<SendRegular />} onClick={() => start('evidence', d)}>
                      Evidence sent…
                    </MenuItem>
                    {!d.lossWaiting && (
                      <MenuItem icon={<CheckmarkCircleRegular />} onClick={() => start('won', d)}>
                        Won…
                      </MenuItem>
                    )}
                    {!d.lossWaiting && (
                      <MenuItem icon={<DismissCircleRegular />} onClick={() => start('accept', d)}>
                        Accept the loss…
                      </MenuItem>
                    )}
                  </>
                )}
                {d.billId && (
                  <MenuItem
                    icon={<ReceiptRegular />}
                    onClick={() => void navigate({ to: '/billing/bills/$billId', params: { billId: d.billId! } })}
                  >
                    Open the bill
                  </MenuItem>
                )}
              </MenuList>
            </MenuPopover>
          </Menu>
        )}
        emptyTitle={open ? 'No open chargebacks' : 'No closed chargebacks'}
        emptyDescription={
          open
            ? 'No bank has taken money back on these payments, or every case is closed.'
            : 'Cases appear here once they are won or lost.'
        }
        page={page}
        pageSize={25}
        totalCount={list.data?.totalCount}
        onPageChange={setPage}
        size="small"
      />
      <ReasonDialog
        open={acting !== null}
        title={
          acting
            ? {
                describe: `The bank's notice for ${acting.dispute.number}`,
                evidence: `Evidence sent for ${acting.dispute.number}`,
                won: `${acting.dispute.number} won?`,
                accept: `Accept the loss of ${acting.dispute.number}?`,
              }[acting.action]
            : ''
        }
        description={
          acting?.action === 'accept'
            ? `Receipt ${acting.dispute.receiptNumber} is asked to be reversed as taken back by a chargeback. Someone else who approves reversals decides; when they approve, the bill owes the money again and the reversal goes in the day book.`
            : acting?.action === 'won'
              ? 'The money came back, or the bank dropped the chargeback. The case closes.'
              : acting?.action === 'describe' && acting.dispute.reason
                ? `Recorded: “${acting.dispute.reason}”. Leave the note empty to keep it.`
                : undefined
        }
        label={
          acting?.action === 'describe'
            ? 'What the notice says'
            : acting?.action === 'evidence'
              ? 'What was sent, and to whom'
              : acting?.action === 'won'
                ? 'How the money came back'
                : 'Why the loss is accepted'
        }
        optional={acting?.action === 'describe'}
        destructive={acting?.action === 'accept'}
        confirmLabel={
          acting?.action === 'accept' ? 'Ask for approval' : acting?.action === 'won' ? 'Close as won' : 'Save'
        }
        maxLength={500}
        busy={act.isPending}
        error={act.error}
        dirty={
          acting?.action === 'describe' &&
          (reasonCode !== (acting.dispute.reasonCode ?? '') || respondBy !== (acting.dispute.respondBy ?? ''))
        }
        onConfirm={(note) => acting && act.mutate({ ...acting, note })}
        onCancel={() => {
          setActing(null);
          act.reset();
        }}
      >
        {acting?.action === 'describe' && (
          <div className={styles.fields}>
            <Field label="Reason code">
              <Input value={reasonCode} maxLength={32} onChange={(_, v) => setReasonCode(v.value)} />
            </Field>
            <Field
              label="Answer by"
              hint={
                acting.dispute.gatewayDisputeId && acting.dispute.respondBy
                  ? 'Set by the gateway for this dispute'
                  : undefined
              }
            >
              <Input
                type="date"
                value={respondBy}
                disabled={!!acting.dispute.gatewayDisputeId && !!acting.dispute.respondBy}
                onChange={(_, v) => setRespondBy(v.value)}
              />
            </Field>
          </div>
        )}
        {acting?.action === 'evidence' && (
          <Field label="Sent on" required>
            <Input type="date" value={sentOn} max={todayIso()} onChange={(_, v) => setSentOn(v.value)} />
          </Field>
        )}
      </ReasonDialog>
      <ReasonDialog
        open={opening}
        title="Open a chargeback case"
        description="From the bank's or the gateway's notice, before a settlement carries it. The settlement that takes the money later joins this case."
        label="What the notice says"
        optional
        confirmLabel="Open the case"
        maxLength={500}
        busy={openCase.isPending}
        error={openCase.error}
        dirty={receiptNumber !== '' || amount !== ''}
        onConfirm={(note) => {
          if (receiptNumber.trim() && Number(amount) > 0 && (!choosing || paymentId)) {
            openCase.mutate(note);
          }
        }}
        onCancel={() => {
          setOpening(false);
          openCase.reset();
        }}
      >
        <div className={styles.fields}>
          <Field
            label="Receipt number"
            required
            validationMessage={
              lines.isError
                ? `No receipt ${lookedUp} in this facility.`
                : lines.data?.length === 0
                  ? 'This receipt has no card, UPI or online payment for a bank to take back.'
                  : undefined
            }
          >
            <Input
              value={receiptNumber}
              maxLength={24}
              onChange={(_, v) => {
                setReceiptNumber(v.value);
                setPaymentId('');
              }}
              onBlur={() => setLookedUp(receiptNumber.trim())}
            />
          </Field>
          {choosing && (
            <Field
              label="Payment taken back"
              required
              hint="The receipt has more than one: choose the one the notice names."
            >
              <Select value={paymentId} onChange={(_, d) => setPaymentId(d.value)}>
                <option value="">Choose the payment…</option>
                {lines.data?.map((p) => (
                  <option key={p.id} value={p.id} disabled={p.hasOpenCase}>
                    {[PAYMENT_MODE_LABEL[p.mode], formatInr(p.amount), p.reference, p.hasOpenCase && 'a case is open']
                      .filter(Boolean)
                      .join(' · ')}
                  </option>
                ))}
              </Select>
            </Field>
          )}
          <Field label="Amount taken back (₹)" required>
            <Input type="number" inputMode="decimal" min={0} value={amount} onChange={(_, v) => setAmount(v.value)} />
          </Field>
          <Field label="Reason code">
            <Input value={reasonCode} maxLength={32} onChange={(_, v) => setReasonCode(v.value)} />
          </Field>
          <Field label="Answer by">
            <Input type="date" value={respondBy} onChange={(_, v) => setRespondBy(v.value)} />
          </Field>
        </div>
      </ReasonDialog>
    </>
  );
}
