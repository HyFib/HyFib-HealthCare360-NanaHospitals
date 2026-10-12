import {
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
  Spinner,
  Text,
  tokens,
} from '@fluentui/react-components';
import { ArrowClockwiseRegular, CopyRegular, LinkRegular } from '@fluentui/react-icons';
import { formatDateTime, formatInr } from '@hc360/core';
import { useNotify } from '@hc360/ui';
import { useMutation } from '@tanstack/react-query';
import { QRCodeSVG } from 'qrcode.react';
import { useState } from 'react';
import { billingApi, type GatewayOrderDto } from './api';
import { useAttemptKey, useRefreshBilling } from './hooks';
import { parseRupees } from './money';
import { ActionError, AmountField, LinkStatusBadge } from './ui';

const useStyles = makeStyles({
  body: { display: 'flex', flexDirection: 'column', gap: '12px' },
  link: { display: 'grid', gridTemplateColumns: 'auto 1fr', gap: '16px', alignItems: 'center' },
  qr: { padding: '8px', backgroundColor: '#fff', borderRadius: tokens.borderRadiusMedium, lineHeight: 0 },
  muted: { color: tokens.colorNeutralForeground3 },
});

export type LinkTarget = { kind: 'bill'; billId: string; due: number } | { kind: 'ip'; ipAccountId: string };

/**
 * Remote payment (INT-PG): a gateway link sent by SMS/WhatsApp, also shown as a QR code the patient can scan at the
 * counter. The receipt is recorded automatically when the gateway confirms the payment.
 */
export function PaymentLinkDialog({
  open,
  target,
  onClose,
}: {
  open: boolean;
  target: LinkTarget;
  onClose: () => void;
}) {
  const styles = useStyles();
  const notify = useNotify();
  const refresh = useRefreshBilling();
  const [attemptKey, renewAttempt] = useAttemptKey();
  const [amount, setAmount] = useState('');
  const [sms, setSms] = useState(true);
  const [whatsApp, setWhatsApp] = useState(false);
  const [order, setOrder] = useState<GatewayOrderDto | null>(null);
  const [wasOpen, setWasOpen] = useState(open);
  if (open !== wasOpen) {
    setWasOpen(open);
    if (open) {
      setAmount(target.kind === 'bill' ? String(target.due) : '');
      setOrder(null);
    }
  }

  const parsed = parseRupees(amount, { max: target.kind === 'bill' ? target.due : undefined });
  const amountError =
    amount && !parsed.ok
      ? target.kind === 'bill' && parsed.error === 'The amount is too large.'
        ? `The balance is ${formatInr(target.due)}.`
        : parsed.error
      : null;

  const create = useMutation({
    mutationFn: () =>
      billingApi.createPaymentLink(
        {
          billId: target.kind === 'bill' ? target.billId : null,
          ipAccountId: target.kind === 'ip' ? target.ipAccountId : null,
          amount: parsed.ok ? parsed.rupees : null,
          sendSms: sms,
          sendWhatsApp: whatsApp,
        },
        { idempotencyKey: attemptKey },
      ),
    onSuccess: (created) => {
      setOrder(created);
      renewAttempt();
      notify.success('Payment link created', sms || whatsApp ? 'Sent to the patient’s mobile number.' : undefined);
      void refresh();
    },
  });

  const check = useMutation({
    mutationFn: () => billingApi.refreshPaymentLink(order!.id),
    onSuccess: (updated) => {
      setOrder(updated);
      if (updated.status === 'Paid') {
        notify.success('Payment received', formatInr(updated.amount));
        void refresh();
      }
    },
  });

  const copy = () => {
    if (order?.linkUrl) {
      void navigator.clipboard?.writeText(order.linkUrl).then(() => notify.info('Link copied'));
    }
  };

  return (
    <Dialog open={open} onOpenChange={(_, data) => !data.open && !create.isPending && onClose()}>
      <DialogSurface style={{ maxWidth: 560 }}>
        <DialogBody>
          <DialogTitle>{order ? 'Payment link' : 'Send a payment link'}</DialogTitle>
          <DialogContent className={styles.body}>
            {!order ? (
              <form
                id="payment-link"
                className={styles.body}
                onSubmit={(event) => {
                  event.preventDefault();
                  if (parsed.ok && !create.isPending) {
                    create.mutate();
                  }
                }}
              >
                <Text>
                  The patient pays online by UPI, card or net banking. The receipt is recorded automatically when the
                  payment succeeds.
                </Text>
                <AmountField
                  label={target.kind === 'ip' ? 'Advance to request' : 'Amount'}
                  required
                  value={amount}
                  onChange={setAmount}
                  error={amountError}
                  hint={target.kind === 'bill' ? `Balance ${formatInr(target.due)}` : undefined}
                  autoFocus
                />
                <Checkbox checked={sms} onChange={(_, data) => setSms(Boolean(data.checked))} label="Send by SMS" />
                <Checkbox
                  checked={whatsApp}
                  onChange={(_, data) => setWhatsApp(Boolean(data.checked))}
                  label="Send on WhatsApp"
                />
                <ActionError error={create.error} />
              </form>
            ) : (
              <>
                <div className={styles.link}>
                  {order.linkUrl ? (
                    <div className={styles.qr}>
                      <QRCodeSVG value={order.linkUrl} size={148} aria-label="QR code of the payment link" />
                    </div>
                  ) : (
                    <span />
                  )}
                  <div className={styles.body}>
                    <Text size={500} weight="semibold">
                      {formatInr(order.amount)}
                    </Text>
                    <LinkStatusBadge status={order.status} />
                    <Caption1 className={styles.muted}>
                      Created {formatDateTime(order.createdAt)}
                      {order.paidAt ? ` · paid ${formatDateTime(order.paidAt)}` : ''}
                    </Caption1>
                  </div>
                </div>
                {order.linkUrl && (
                  <Input
                    value={order.linkUrl}
                    readOnly
                    contentBefore={<LinkRegular />}
                    aria-label="Payment link"
                    onFocus={(event) => event.target.select()}
                  />
                )}
                <ActionError error={check.error} />
              </>
            )}
          </DialogContent>
          <DialogActions>
            {!order ? (
              <>
                <Button onClick={onClose} disabled={create.isPending}>
                  Cancel
                </Button>
                <Button
                  type="submit"
                  form="payment-link"
                  appearance="primary"
                  disabled={!parsed.ok || create.isPending}
                  // Keeps focus while the link is made (a disabled button drops it); Enter can still submit, which is
                  // ignored.
                  disabledFocusable={create.isPending}
                  icon={create.isPending ? <Spinner size="tiny" aria-hidden /> : <LinkRegular />}
                >
                  Create link
                </Button>
              </>
            ) : (
              <>
                {order.linkUrl && (
                  <Button icon={<CopyRegular />} onClick={copy}>
                    Copy link
                  </Button>
                )}
                <Button
                  icon={check.isPending ? <Spinner size="tiny" aria-hidden /> : <ArrowClockwiseRegular />}
                  onClick={() => check.mutate()}
                  disabled={check.isPending || order.status !== 'Created'}
                  // Keeps focus while checking, and once the link is paid or expired: a disabled button drops it.
                  disabledFocusable={check.isPending || order.status !== 'Created'}
                >
                  Check payment
                </Button>
                {/* Takes focus from Create link, which the new link replaces. */}
                <Button appearance="primary" onClick={onClose} autoFocus>
                  Done
                </Button>
              </>
            )}
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}
