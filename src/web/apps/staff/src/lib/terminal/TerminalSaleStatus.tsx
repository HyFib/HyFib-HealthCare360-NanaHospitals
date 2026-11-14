import {
  Caption1,
  makeStyles,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  Spinner,
  Text,
  tokens,
} from '@fluentui/react-components';
import { CheckmarkCircleRegular, DismissCircleRegular, WarningRegular } from '@fluentui/react-icons';
import { formatInr } from '@hc360/core';
import type { ReactNode } from 'react';
import {
  isTerminalSalePending,
  isTerminalSaleUnconfirmed,
  terminalDetailsLine,
  terminalStatusLabel,
  type PosTransactionStatus,
} from './labels';
import { UnconfirmedSaleNotice } from './UnconfirmedSale';
import { useNow } from './useNow';

/** What the terminal reported about a card/UPI payment, for display. */
export interface TerminalSaleDetails {
  amount: number;
  status: PosTransactionStatus;
  message: string | null;
  cardLast4: string | null;
  approvalCode: string | null;
  rrn: string | null;
  /** When the sale was sent, and, for an unconfirmed sale, when a new one may be sent. */
  createdAt?: string | null;
  retryAfter?: string | null;
}

const useStyles = makeStyles({
  status: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    gap: '8px',
    padding: '12px 0',
    textAlign: 'center',
  },
  big: { fontSize: '28px', fontWeight: tokens.fontWeightBold },
  muted: { color: tokens.colorNeutralForeground3 },
});

/**
 * How a card/UPI terminal payment stands: spinner while it waits, a check or a cross when it ends, the amount, the
 * status, the terminal's message and, once approved, the card, approval and RRN. `lostContact` shows the bar for a
 * failed status check; `children` adds screen-specific lines (a receipt note) and `footer` goes last (an error). A sale
 * whose answer from the terminal was lost is neither a success nor a decline: it shows what to check on the terminal and
 * when a new sale may be sent.
 */
export function TerminalSaleStatus({
  sale,
  lostContact,
  payer,
  children,
  footer,
}: {
  sale: TerminalSaleDetails;
  lostContact?: boolean;
  /** Who pays, in the waiting status: "patient" (the default) or "customer". */
  payer?: string;
  children?: ReactNode;
  footer?: ReactNode;
}) {
  const styles = useStyles();
  const pending = isTerminalSalePending(sale.status);
  const unconfirmed = isTerminalSaleUnconfirmed(sale.status);
  const now = useNow();
  return (
    <div className={styles.status} role="status" aria-live="polite">
      {pending && <Spinner size="large" aria-hidden />}
      {sale.status === 'Approved' && (
        <CheckmarkCircleRegular fontSize={48} color={tokens.colorPaletteGreenForeground1} />
      )}
      {unconfirmed && <WarningRegular fontSize={48} color={tokens.colorPaletteMarigoldForeground1} />}
      {!pending && !unconfirmed && sale.status !== 'Approved' && (
        <DismissCircleRegular fontSize={48} color={tokens.colorPaletteRedForeground1} />
      )}
      <span className={styles.big}>{formatInr(sale.amount)}</span>
      <Text weight="semibold">{terminalStatusLabel(sale.status, payer)}</Text>
      {sale.message && !unconfirmed && <Caption1 className={styles.muted}>{sale.message}</Caption1>}
      {unconfirmed && <UnconfirmedSaleNotice sale={sale} now={now} />}
      {sale.status === 'Approved' && <Caption1>{terminalDetailsLine(sale)}</Caption1>}
      {children}
      {lostContact && (
        <MessageBar intent="warning">
          <MessageBarBody>
            <MessageBarTitle>Lost contact with the server</MessageBarTitle>
            Still checking the terminal. Do not collect the money again until this shows the result.
          </MessageBarBody>
        </MessageBar>
      )}
      {footer}
    </div>
  );
}
