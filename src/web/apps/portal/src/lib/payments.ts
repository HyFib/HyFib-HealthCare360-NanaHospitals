import type { Language } from '@/i18n';
import { SESSION_KEYS } from './session';
import type { PaymentCheckout } from './types';

/**
 * Opens the payment gateway's checkout for an order the server created, and reports how it ended. The portal never
 * handles card numbers or UPI PINs: the gateway's own page does, and the server verifies every payment (signature
 * or server-to-server status) before anything is recorded.
 *
 * Providers (Integrations/Payments): "razorpay" → Razorpay Standard Checkout (script loaded only when the patient taps
 * Pay); "simulator" → training gateway whose orders are captured at once on the server; any provider that returns
 * a hosted payment link → the page is opened in this tab, and the bill or appointment it pays is remembered so its
 * page confirms the payment when the patient comes back.
 */

export type CheckoutOutcome =
  /** The gateway returned proof of payment (Razorpay: order id, payment id, signature) for the server to verify. */
  | { kind: 'completed'; orderId: string; paymentId: string; signature: string }
  /** Training gateway: the order is already captured; ask the server to confirm it. */
  | { kind: 'captured' }
  /** The patient was sent to the gateway's hosted page (this page is being left). */
  | { kind: 'redirected' }
  | { kind: 'dismissed' }
  | { kind: 'failed' }
  | { kind: 'unavailable' };

export interface CheckoutContext {
  hospital: string;
  description: string;
  language: Language;
  /** Remembered in this tab so the page can confirm the payment when the patient comes back from a hosted page. */
  pending?: PendingPayment;
  /**
   * Asks the server for a hosted payment page when the gateway's checkout cannot open here (its script is blocked by the
   * network or the browser) and the order came without one. Null when none could be made.
   */
  hostedLink?: () => Promise<string | null>;
}

export type PendingPayment =
  | { kind: 'bill'; billId: string; orderRowId: string }
  /** An appointment has one gateway order at a time, which the server checks by itself. */
  | { kind: 'appointment'; appointmentId: string };

const RAZORPAY_SRC = 'https://checkout.razorpay.com/v1/checkout.js';
const SCRIPT_TIMEOUT_MS = 15_000;

interface RazorpayResponse {
  razorpay_payment_id: string;
  razorpay_order_id: string;
  razorpay_signature: string;
}

interface RazorpayInstance {
  open(): void;
  on(event: 'payment.failed', handler: () => void): void;
}

type RazorpayConstructor = new (options: Record<string, unknown>) => RazorpayInstance;

declare global {
  interface Window {
    Razorpay?: RazorpayConstructor;
  }
}

let loading: Promise<RazorpayConstructor> | null = null;

function loadRazorpay(): Promise<RazorpayConstructor> {
  if (window.Razorpay) {
    return Promise.resolve(window.Razorpay);
  }
  loading ??= new Promise<RazorpayConstructor>((resolve, reject) => {
    const script = document.createElement('script');
    const fail = () => {
      clearTimeout(timer);
      script.remove();
      loading = null;
      reject(new Error('The payment page could not be loaded.'));
    };
    const timer = setTimeout(fail, SCRIPT_TIMEOUT_MS);
    script.src = RAZORPAY_SRC;
    script.async = true;
    script.referrerPolicy = 'no-referrer';
    script.onload = () => {
      clearTimeout(timer);
      if (window.Razorpay) {
        resolve(window.Razorpay);
      } else {
        fail();
      }
    };
    // Also fires when the hospital server's Content-Security-Policy does not allow the gateway's script.
    script.onerror = fail;
    document.head.appendChild(script);
  });
  return loading;
}

/** Only real https pages are opened (the training gateway's links point at a reserved ".invalid" host). */
export function isUsablePaymentLink(url: string | null | undefined): url is string {
  if (!url) {
    return false;
  }
  try {
    const parsed = new URL(url);
    return parsed.protocol === 'https:' && !parsed.hostname.endsWith('.invalid');
  } catch {
    return false;
  }
}

export function isTrainingGateway(checkout: Pick<PaymentCheckout, 'provider'>): boolean {
  return checkout.provider.toLowerCase() === 'simulator';
}

export async function runCheckout(checkout: PaymentCheckout, context: CheckoutContext): Promise<CheckoutOutcome> {
  const provider = checkout.provider.toLowerCase();

  if (provider === 'simulator') {
    return { kind: 'captured' };
  }

  if (provider === 'razorpay' && checkout.checkoutKey) {
    let Razorpay: RazorpayConstructor;
    try {
      Razorpay = await loadRazorpay();
    } catch {
      if (isUsablePaymentLink(checkout.paymentLinkUrl)) {
        return openHostedPage(checkout.paymentLinkUrl, context);
      }
      const link = context.hostedLink ? await context.hostedLink() : null;
      return isUsablePaymentLink(link) ? openHostedPage(link, context) : { kind: 'unavailable' };
    }
    return new Promise<CheckoutOutcome>((resolve) => {
      let failedOnce = false;
      const instance = new Razorpay({
        key: checkout.checkoutKey,
        order_id: checkout.orderId,
        amount: Math.round(checkout.amount * 100),
        currency: checkout.currency || 'INR',
        name: context.hospital,
        description: context.description,
        theme: { color: '#0A7CA7' },
        config: context.language === 'hi' ? { display: { language: 'hi' } } : undefined,
        modal: {
          escape: true,
          confirm_close: true,
          ondismiss: () => resolve({ kind: failedOnce ? 'failed' : 'dismissed' }),
        },
        handler: (response: RazorpayResponse) =>
          resolve({
            kind: 'completed',
            orderId: response.razorpay_order_id,
            paymentId: response.razorpay_payment_id,
            signature: response.razorpay_signature,
          }),
      });
      // Razorpay keeps its window open after a failed attempt so the patient can retry with another method.
      instance.on('payment.failed', () => {
        failedOnce = true;
      });
      instance.open();
    });
  }

  if (isUsablePaymentLink(checkout.paymentLinkUrl)) {
    return openHostedPage(checkout.paymentLinkUrl, context);
  }
  return { kind: 'unavailable' };
}

function openHostedPage(url: string, context: CheckoutContext): CheckoutOutcome {
  if (!context.pending) {
    // Nothing could confirm the payment when the patient comes back.
    return { kind: 'unavailable' };
  }
  rememberPendingPayment(context.pending);
  window.location.assign(url);
  return { kind: 'redirected' };
}

export function rememberPendingPayment(pending: PendingPayment): void {
  try {
    // False positive: what is kept is the random id of the patient's own bill or appointment, no personal or clinical
    // data, in this tab only (sessionStorage ends with the tab). CodeQL takes the word "appointment" for health data.
    // codeql[js/clear-text-storage-of-sensitive-data]
    sessionStorage.setItem(SESSION_KEYS.pendingPayment, JSON.stringify(pending));
  } catch {
    // Without storage the patient can still tap "Check payment status".
  }
}

function readPendingPayment(): Record<string, unknown> | null {
  try {
    const raw = sessionStorage.getItem(SESSION_KEYS.pendingPayment);
    const value: unknown = raw ? JSON.parse(raw) : null;
    return value !== null && typeof value === 'object' ? (value as Record<string, unknown>) : null;
  } catch {
    return null;
  }
}

/** The payment this tab started on a hosted page for `billId`, if any (read only; see `clearPendingPayment`). */
export function peekPendingPayment(billId: string): Extract<PendingPayment, { kind: 'bill' }> | null {
  const value = readPendingPayment();
  return value?.kind === 'bill' && value.billId === billId && typeof value.orderRowId === 'string'
    ? { kind: 'bill', billId, orderRowId: value.orderRowId }
    : null;
}

/** The payment this tab sent the patient to a hosted page for, whatever it pays (read only). */
export function peekAnyPendingPayment(): PendingPayment | null {
  const value = readPendingPayment();
  if (value?.kind === 'bill' && typeof value.billId === 'string' && typeof value.orderRowId === 'string') {
    return { kind: 'bill', billId: value.billId, orderRowId: value.orderRowId };
  }
  if (value?.kind === 'appointment' && typeof value.appointmentId === 'string') {
    return { kind: 'appointment', appointmentId: value.appointmentId };
  }
  return null;
}

/** Whether this tab sent the patient to a hosted page to pay for `appointmentId` (read only). */
export function hasPendingAppointmentPayment(appointmentId: string): boolean {
  const value = readPendingPayment();
  return value?.kind === 'appointment' && value.appointmentId === appointmentId;
}

/** Forgets the hosted-page payment once its outcome is known. */
export function clearPendingPayment(): void {
  try {
    sessionStorage.removeItem(SESSION_KEYS.pendingPayment);
  } catch {
    // Nothing was stored.
  }
}
