import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it } from 'vitest';
import { createTranslator, useLanguageStore } from '@/i18n';
import { formattersFor } from '@/i18n/useFormat';
import type { PortalRefund } from '@/lib/types';
import { refundReferenceText, refundStatusText } from './labels';
import { RefundList } from './RefundList';

/** Gap #37: the patient sees where each refund stands and the reference their bank or the counter can trace it by. */

const REFUND: PortalRefund = {
  id: '0192a000-0000-7000-8000-0000000000r1',
  refundNumber: 'RF2600017',
  patientId: '5f0c6d3e-8c1a-4a53-9a3f-2b1d6f7e9a10',
  patientName: 'Asha Verma',
  billId: '0192a000-0000-7000-8000-0000000000b1',
  billNumber: 'NHOB2600042',
  purpose: 'Bill',
  amount: 1250,
  stage: 'Requested',
  requestedAt: '2026-10-28T05:30:00Z',
  sentAt: null,
  mode: null,
  reference: null,
  creditedAt: null,
};

const SENT = '2026-10-30T06:00:00Z';
const CREDITED = '2026-11-02T07:00:00Z';

afterEach(() => useLanguageStore.setState({ language: 'en' }));

describe('where a refund stands', () => {
  const t = createTranslator('en');
  const f = formattersFor('en');
  const text = (refund: Partial<PortalRefund>) => refundStatusText({ ...REFUND, ...refund }, t, f);

  it('says each stage in words, with its date', () => {
    expect(text({ stage: 'Requested' })).toBe(
      `Asked for on ${f.date(REFUND.requestedAt)}. A second person at the hospital checks every refund before it is paid.`,
    );
    expect(text({ stage: 'Approved' })).toBe('Approved. The hospital will pay it to you soon.');
    expect(text({ stage: 'NotApproved' })).toBe('This refund was not approved. Please ask at the billing counter.');
    expect(text({ stage: 'PaidOut', mode: 'Cash', sentAt: SENT })).toBe(
      `Paid to you in cash at the counter on ${f.date(SENT)}.`,
    );
    expect(text({ stage: 'PaidOut', mode: 'NetBanking', sentAt: SENT })).toBe(
      `Paid back by bank transfer on ${f.date(SENT)}.`,
    );
    expect(text({ stage: 'PaidOut', mode: 'Cheque', sentAt: SENT })).toBe(`Paid back by cheque on ${f.date(SENT)}.`);
    expect(text({ stage: 'KeptAsAdvance', mode: 'Advance', sentAt: SENT })).toContain(
      'Kept as an advance on your account',
    );
    expect(text({ stage: 'WithTheBank', mode: 'PaymentGateway', sentAt: SENT })).toBe(
      `Sent back to the card or account you paid with on ${f.date(SENT)}. Banks usually take 5 to 7 working days to credit it.`,
    );
    expect(text({ stage: 'Credited', mode: 'PaymentGateway', sentAt: SENT, creditedAt: CREDITED })).toBe(
      `Your bank credited it on ${f.date(CREDITED)}.`,
    );
    expect(text({ stage: 'BankReturned', mode: 'PaymentGateway', sentAt: SENT })).toContain(
      'Your bank could not credit this refund.',
    );
    expect(text({ stage: 'Replaced', mode: 'Cheque', sentAt: SENT })).toBe(
      'This payment did not reach you, so the hospital has made a new refund for it.',
    );
  });

  it("gives the bank's reference for money sent through the gateway and the counter's for a transfer", () => {
    expect(refundReferenceText({ ...REFUND, stage: 'Credited', reference: '74332745123456789012345' }, t)).toBe(
      'Bank reference 74332745123456789012345: your bank can trace the refund with it',
    );
    expect(refundReferenceText({ ...REFUND, stage: 'WithTheBank', reference: '412233445566' }, t)).toBe(
      'Bank reference 412233445566: your bank can trace the refund with it',
    );
    expect(
      refundReferenceText({ ...REFUND, stage: 'PaidOut', mode: 'NetBanking', reference: 'NEFT0012345678' }, t),
    ).toBe('Reference NEFT0012345678');
    expect(refundReferenceText({ ...REFUND, stage: 'PaidOut', mode: 'Cash', reference: null }, t)).toBeNull();
  });
});

describe('the refund list', () => {
  const renderList = (refunds: PortalRefund[], showBill: boolean) =>
    render(
      <FluentProvider theme={webLightTheme}>
        <RefundList refunds={refunds} showBill={showBill} />
      </FluentProvider>,
    );

  it('shows each refund with its amount, stage, number, bill and reference', () => {
    renderList(
      [
        {
          ...REFUND,
          stage: 'Credited',
          mode: 'PaymentGateway',
          sentAt: SENT,
          creditedAt: CREDITED,
          reference: '74332745123456789012345',
        },
        {
          ...REFUND,
          id: '0192a000-0000-7000-8000-0000000000r2',
          refundNumber: 'RF2600018',
          purpose: 'Advance',
          billId: null,
          billNumber: null,
        },
      ],
      true,
    );

    expect(screen.getByText('₹1,250.00 · Refund on a bill')).toBeTruthy();
    expect(screen.getByText('Credited')).toBeTruthy();
    expect(
      screen.getByText(
        'Refund RF2600017 · For bill NHOB2600042 · Bank reference 74332745123456789012345: your bank can trace the refund with it',
      ),
    ).toBeTruthy();
    expect(screen.getByText('₹1,250.00 · Unused advance payment')).toBeTruthy();
    expect(screen.getByText('Refund RF2600018')).toBeTruthy();
    expect(screen.getByText('Asked for')).toBeTruthy();
  });

  it("leaves out the bill on the bill's own page", () => {
    renderList([REFUND], false);

    expect(screen.getByText('Refund RF2600017')).toBeTruthy();
    expect(screen.queryByText(/NHOB2600042/)).toBeNull();
  });

  it("reads in the patient's language", () => {
    useLanguageStore.setState({ language: 'hi' });
    renderList([{ ...REFUND, stage: 'WithTheBank', mode: 'PaymentGateway', sentAt: SENT }], true);

    expect(screen.getByText('आपके बैंक के पास')).toBeTruthy();
    expect(screen.getByText(/बैंक इसे जमा करने में आमतौर पर 5 से 7 कार्य दिवस लेते हैं।$/)).toBeTruthy();
  });
});
