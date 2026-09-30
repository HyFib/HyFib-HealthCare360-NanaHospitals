import { isApiError } from '@hc360/core';
import type { MessageKey } from '@/i18n';
import { runCheckout, type CheckoutContext } from '@/lib/payments';
import type { PaymentCheckout } from '@/lib/types';
import { appointmentApi } from './api';

export type AppointmentPaymentResult =
  | { status: 'paid' }
  | { status: 'not-paid'; messageKey: MessageKey }
  /** The server refused to record it (for example an unverifiable signature): its own words are shown. */
  | { status: 'error'; error: unknown };

/**
 * Online prepayment of an appointment: opens the gateway for the order the server created (at booking, or now),
 * then asks the server to verify the payment before the appointment is marked paid: the gateway's signature, or for
 * the training gateway a server-to-server status check. A payment problem never costs the patient their slot; the
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
      return isApiError(error) && error.status === 422
        ? { status: 'not-paid', messageKey: 'payment.unavailable' }
        : { status: 'error', error };
    }
  }

  const outcome = await runCheckout(order, context);
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
    // A hosted payment page is not offered for appointments: nothing would confirm it when the patient comes back.
    case 'redirected':
    case 'unavailable':
    default:
      return { status: 'not-paid', messageKey: 'payment.unavailable' };
  }
}
