import { isApiError } from '@hc360/core';
import type { MessageKey } from '@/i18n';
import { clearPendingPayment, runCheckout, type CheckoutContext } from '@/lib/payments';
import type { PaymentCheckout } from '@/lib/types';
import { appointmentApi } from './api';

export type AppointmentPaymentResult =
  | { status: 'paid' }
  /** The patient is being sent to the gateway's hosted page; the appointment's page confirms it on return. */
  | { status: 'redirected' }
  | { status: 'not-paid'; messageKey: MessageKey }
  /** The server refused to record it (for example an unverifiable signature): its own words are shown. */
  | { status: 'error'; error: unknown };

/**
 * Online prepayment of an appointment: opens the gateway for the order the server created (at booking, or now),
 * then asks the server to verify the payment before the appointment is marked paid: the gateway's signature, or for
 * the training gateway and a hosted payment page a server-to-server status check (`confirmReturnedPayment`, run by
 * the appointment's page when the patient comes back from the hosted page). A payment problem never costs the patient their slot; the
 * appointment simply stays "pay at the hospital".
 */
export async function payAppointment(
  appointmentId: string,
  checkout: PaymentCheckout | null,
  context: Omit<CheckoutContext, 'pending'>,
): Promise<AppointmentPaymentResult> {
  let order = checkout;
  if (!order) {
    try {
      order = await appointmentApi.startPayment(appointmentId);
    } catch (error) {
      // The order it already had was paid after all (another tab, or a hosted page the patient never came back from).
      if (isApiError(error) && error.code === 'appointment.already_paid') {
        return { status: 'paid' };
      }
      return isApiError(error) && error.status === 422 && error.code === 'payment.unavailable'
        ? { status: 'not-paid', messageKey: 'payment.unavailable' }
        : { status: 'error', error };
    }
  }

  const outcome = await runCheckout(order, {
    ...context,
    pending: { kind: 'appointment', appointmentId },
    hostedLink: async () => {
      try {
        return (await appointmentApi.startPayment(appointmentId, { hosted: true })).paymentLinkUrl;
      } catch {
        return null;
      }
    },
  });
  switch (outcome.kind) {
    case 'completed':
      try {
        await appointmentApi.confirmPayment(appointmentId, {
          orderId: outcome.orderId,
          paymentId: outcome.paymentId,
          signature: outcome.signature,
        });
        return { status: 'paid' };
      } catch (error) {
        return { status: 'error', error };
      }
    case 'dismissed':
      return { status: 'not-paid', messageKey: 'payment.dismissed' };
    case 'failed':
      return { status: 'not-paid', messageKey: 'payment.failed' };
    case 'captured':
      try {
        await appointmentApi.verifyPayment(appointmentId);
        return { status: 'paid' };
      } catch (error) {
        return { status: 'error', error };
      }
    case 'redirected':
      return { status: 'redirected' };
    case 'unavailable':
    default:
      return { status: 'not-paid', messageKey: 'payment.unavailable' };
  }
}

export type ReturnedPaymentResult =
  | { status: 'paid' }
  /** The gateway has not reported the payment yet (it can take a few minutes); safe to check again. */
  | { status: 'pending' }
  | { status: 'error'; error: unknown };

/** Answers that do not settle whether the patient paid: the check is simply asked again later. */
const UNSETTLED = new Set(['payment.not_captured', 'payment.in_progress', 'payment.gateway_unavailable']);

/**
 * Confirms a payment made on the gateway's hosted page, by the server's own check with the gateway. Safe to repeat.
 * The remembered payment is forgotten only once the outcome is known. While it is not (the gateway has not reported the
 * payment, could not be reached, or the network failed), it is kept so the patient can check again ("Check payment
 * status") instead of being offered Pay a second time; the gateway's webhook may mark it paid meanwhile.
 */
export async function confirmReturnedPayment(appointmentId: string): Promise<ReturnedPaymentResult> {
  try {
    await appointmentApi.verifyPayment(appointmentId);
    clearPendingPayment();
    return { status: 'paid' };
  } catch (error) {
    const unsettled =
      !isApiError(error) || error.status === 0 || error.status >= 500 || UNSETTLED.has(error.code ?? '');
    if (unsettled) {
      return { status: 'pending' };
    }
    clearPendingPayment();
    return { status: 'error', error };
  }
}
