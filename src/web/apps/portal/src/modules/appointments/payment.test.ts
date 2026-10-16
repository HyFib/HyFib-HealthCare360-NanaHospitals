import { ApiError } from '@hc360/core';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { clearPendingPayment, hasPendingAppointmentPayment } from '@/lib/payments';
import type { PaymentCheckout } from '@/lib/types';
import type * as AppointmentApi from './api';
import { appointmentApi } from './api';
import { confirmReturnedPayment, payAppointment } from './payment';

vi.mock('./api', async (importOriginal) => ({
  ...(await importOriginal<typeof AppointmentApi>()),
  appointmentApi: { startPayment: vi.fn(), confirmPayment: vi.fn(), verifyPayment: vi.fn() },
}));

const context = { hospital: 'Nana Hospitals', description: 'Consultation', language: 'en' as const };

/** A gateway that offers only its own hosted page (no Razorpay Checkout key). */
const hostedOrder: PaymentCheckout = {
  provider: 'paylink',
  orderId: 'order_1',
  amount: 500,
  currency: 'INR',
  checkoutKey: null,
  paymentLinkUrl: 'https://pay.example.com/l/abc',
};

describe('paying for an appointment on the gateway’s hosted page (SRS PP-03)', () => {
  const assign = vi.fn();

  beforeEach(() => {
    vi.mocked(appointmentApi.verifyPayment).mockReset();
    vi.stubGlobal('location', { ...window.location, assign });
    assign.mockReset();
  });

  afterEach(() => {
    clearPendingPayment();
    vi.unstubAllGlobals();
  });

  it('opens the hosted page and remembers the appointment, instead of refusing to take the payment', async () => {
    const result = await payAppointment('appt-1', hostedOrder, context);

    expect(result).toEqual({ status: 'redirected' });
    expect(assign).toHaveBeenCalledWith('https://pay.example.com/l/abc');
    expect(hasPendingAppointmentPayment('appt-1')).toBe(true);
  });

  it('marks it paid on return once the server confirms it with the gateway, and forgets it', async () => {
    await payAppointment('appt-1', hostedOrder, context);
    vi.mocked(appointmentApi.verifyPayment).mockResolvedValue({} as never);

    expect(await confirmReturnedPayment('appt-1')).toEqual({ status: 'paid' });
    expect(hasPendingAppointmentPayment('appt-1')).toBe(false);
  });

  it('keeps it to check again while the gateway has not reported the payment yet', async () => {
    await payAppointment('appt-1', hostedOrder, context);
    vi.mocked(appointmentApi.verifyPayment).mockRejectedValue(
      new ApiError({ status: 422, title: 'Not paid', code: 'payment.not_captured' }),
    );

    expect(await confirmReturnedPayment('appt-1')).toEqual({ status: 'pending' });
    expect(hasPendingAppointmentPayment('appt-1')).toBe(true);
  });

  it('forgets it and reports the server’s answer when the check fails otherwise', async () => {
    await payAppointment('appt-1', hostedOrder, context);
    const refusal = new ApiError({ status: 422, title: 'Unavailable', code: 'payment.unavailable' });
    vi.mocked(appointmentApi.verifyPayment).mockRejectedValue(refusal);

    expect(await confirmReturnedPayment('appt-1')).toEqual({ status: 'error', error: refusal });
    expect(hasPendingAppointmentPayment('appt-1')).toBe(false);
  });
});
