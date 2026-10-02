import { useRealtimeEvent, useSession } from '@hc360/core';
import { useNotify } from '@hc360/ui';
import { useQuery, useQueryClient, type Query } from '@tanstack/react-query';
import { useCallback, useState } from 'react';
import { api } from '@/lib/api';
import { printServerDocument } from '@/lib/documents';
import { usePermission } from '@/lib/permissions';
import { billDocument, billingQueries, type BillDetail, type ReceiptDto, type RefundDto } from './api';
import { NO_DISCOUNT_AUTHORITY, type DiscountAuthority } from './billTotals';
import { useCounterPrefs } from './counterPrefs';
import {
  buildCounterBillHtml,
  buildReceiptHtml,
  buildRefundVoucherHtml,
  sendToPrinter,
  type PrintFacility,
  type PrintFormat,
} from './print';

/** Billing data that changes when money moves (masters, the facility header and Tally logs do not). */
const LIVE_KEYS = new Set(['bills', 'dues', 'receipts', 'ip', 'shifts', 'refunds', 'charges', 'payment-links', 'pos']);

function isLiveBillingQuery(query: Query): boolean {
  const [module, area] = query.queryKey as unknown[];
  return module === 'billing' && typeof area === 'string' && LIVE_KEYS.has(area);
}

/** Keeps billing screens current: every bill, receipt or terminal change in the facility refreshes what is on screen. */
export function useBillingLive(): void {
  const facilityId = useSession((s) => s.user?.facilityId);
  const queryClient = useQueryClient();
  const refresh = () => void queryClient.invalidateQueries({ predicate: isLiveBillingQuery });
  useRealtimeEvent(facilityId ? `facility:${facilityId}` : null, 'billing.bill.changed', refresh);
  useRealtimeEvent(facilityId ? `facility:${facilityId}` : null, 'billing.pos.changed', refresh);
}

/** Refreshes every live billing query now (after a payment, a refund, a finalisation…). */
export function useRefreshBilling(): () => Promise<void> {
  const queryClient = useQueryClient();
  return useCallback(() => queryClient.invalidateQueries({ predicate: isLiveBillingQuery }), [queryClient]);
}

/** The signed-in user's own discount authority (0 % when the server cannot be asked). */
export function useDiscountAuthority(): { authority: DiscountAuthority; loading: boolean } {
  const allowed = usePermission(['billing.op.create', 'billing.ip.manage']);
  const query = useQuery({ ...billingQueries.myDiscountAuthority(), enabled: allowed });
  return { authority: query.data ?? NO_DISCOUNT_AUTHORITY, loading: query.isLoading };
}

/** The hospital header for prints (name only until the facility record arrives). */
export function usePrintFacility(): PrintFacility {
  const facilityId = useSession((s) => s.user?.facilityId);
  const facilityName = useSession((s) => s.user?.facilityName) ?? 'Nana Hospitals';
  const facility = useQuery({ ...billingQueries.facility(facilityId ?? ''), enabled: Boolean(facilityId) });
  return facility.data ?? { name: facilityName };
}

/**
 * Idempotency key for one money operation. The same key is reused when the cashier presses the button again after a
 * timeout, so the server records the payment once; `renew` starts a new operation (after any edit).
 */
export function useAttemptKey(): [string, () => void] {
  const [key, setKey] = useState(() => crypto.randomUUID());
  const renew = useCallback(() => setKey(crypto.randomUUID()), []);
  return [key, renew];
}

/**
 * Print helpers bound to this counter's printer settings; failures become a toast, never a lost payment. The A4 bill
 * comes from the server (the same document as its PDF); the 80 mm bill and the receipts are built here.
 */
export function useBillingPrint() {
  const facility = usePrintFacility();
  const prefs = useCounterPrefs();
  const notify = useNotify();

  const printBill = useCallback(
    (bill: BillDetail, format: PrintFormat = 'A4') =>
      (format === 'A4'
        ? printServerDocument(billDocument(bill.id))
        : sendToPrinter(buildCounterBillHtml(bill, facility), format)
      ).catch((error: unknown) => notify.error(error, 'Printing failed')),
    [facility, notify],
  );

  const printReceipt = useCallback(
    (receipt: ReceiptDto, format: PrintFormat = prefs.receiptFormat) =>
      sendToPrinter(buildReceiptHtml(receipt, facility, format), format).catch((error: unknown) =>
        notify.error(error, 'Printing failed'),
      ),
    [facility, notify, prefs.receiptFormat],
  );

  const printReceiptById = useCallback(
    async (receiptId: string, format: PrintFormat = prefs.receiptFormat) => {
      try {
        const receipt = await api.get<ReceiptDto>(`/api/v1/billing/receipts/${receiptId}`);
        await sendToPrinter(buildReceiptHtml(receipt, facility, format), format);
      } catch (error) {
        notify.error(error, 'Could not print the receipt');
      }
    },
    [facility, notify, prefs.receiptFormat],
  );

  const printRefund = useCallback(
    (refund: RefundDto) =>
      sendToPrinter(buildRefundVoucherHtml(refund, facility), 'A4').catch((error: unknown) =>
        notify.error(error, 'Printing failed'),
      ),
    [facility, notify],
  );

  return { facility, prefs, printBill, printReceipt, printReceiptById, printRefund };
}
