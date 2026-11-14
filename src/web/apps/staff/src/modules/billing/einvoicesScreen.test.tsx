import type * as Core from '@hc360/core';
import { expectNoAxeViolations } from '@hc360/ui/testing';
import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fakeServer, json, openScreen, SCREEN_TESTS, SLOW, stubBrowserApis, type FakeRequest } from '@/test/appScreen';
import type { EInvoiceDetail, EInvoiceRegister, EInvoiceRegistration } from './einvoiceApi';

// Billing › GST e-invoices, whole with the shell, against a fake server: a facility connected to the e-invoice portal
// asks it for the IRN, and can still record one generated on the portal's own screens, which the server checks with the
// portal first. Each refusal is shown in plain words over the server's own sentence, and the document is read again.

vi.mock('@hc360/core', async (importOriginal) => ({
  ...(await importOriginal<typeof Core>()),
  RealtimeProvider: ({ children }: { children: ReactNode }) => children,
}));

const ID = '0192a000-0000-7000-8000-0000000000e1';
const DOC = `/api/v1/billing/einvoices/bill/${ID}`;
const today = new Date().toISOString().slice(0, 10);
const now = new Date().toISOString();
const ACK_NO = '112610012345678';
const SIGNED_QR = 'eyJhbGciOiJSUzI1NiJ9.eyJkYXRhIjoie30ifQ.c2ln';
const IRN = '5a1c'.repeat(16);

const REGISTER: EInvoiceRegister = {
  applicable: true,
  applicableFrom: '2026-04-01',
  from: today,
  to: today,
  rows: [
    {
      source: 'bill',
      id: ID,
      number: 'NHOB260000123',
      date: today,
      buyerGstin: '32AAACH7409R1ZA',
      buyerName: 'Malabar Traders Ltd',
      total: 1180,
      state: 'Pending',
      reportBy: today,
      isOverdue: false,
      irn: null,
      ackNo: null,
      ackDate: null,
      type: 'INV',
      against: null,
    },
  ],
};

const pending = (portalConnected: boolean): EInvoiceDetail => ({
  source: 'bill',
  id: ID,
  number: 'NHOB260000123',
  date: today,
  buyerGstin: '32AAACH7409R1ZA',
  buyerName: 'Malabar Traders Ltd',
  placeOfSupply: '32',
  lines: 2,
  total: 1180,
  state: 'Pending',
  problems: [],
  registration: null,
  type: 'INV',
  against: null,
  againstDate: null,
  portalConnected,
  ewayBillNeeded: false,
});

const REGISTRATION: EInvoiceRegistration = {
  irn: 'a'.repeat(64),
  ackNo: ACK_NO,
  ackDate: now,
  status: 'Registered',
  recordedByName: 'Dr. Meera Iyer',
  recordedAt: now,
  cancelledAt: null,
  cancelReason: null,
  cancelRemark: null,
  cancelBy: new Date(Date.now() + 20 * 3600_000).toISOString(),
  canCancel: true,
  qrCodeSvg: 'data:image/svg+xml;base64,PHN2ZyB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciLz4=',
  version: 3,
  provider: 'manual',
  ewbNo: null,
  ewbDate: null,
  ewbValidTill: null,
};

const live = (portalConnected: boolean): EInvoiceDetail => ({
  ...pending(portalConnected),
  state: 'Registered',
  registration: REGISTRATION,
});

/** Past the 24 hours: the portal cancels the IRN no more. */
const lapsed = (portalConnected: boolean): EInvoiceDetail => ({
  ...live(portalConnected),
  registration: { ...REGISTRATION, canCancel: false, cancelBy: new Date(Date.now() - 4 * 3600_000).toISOString() },
});

/** Cancelled on the portal within the 24 hours, its answer lost, and recorded once the portal said so. */
const CANCELLED_IN_TIME: EInvoiceDetail = {
  ...pending(true),
  state: 'IrnCancelled',
  registration: {
    ...REGISTRATION,
    status: 'Cancelled',
    canCancel: false,
    cancelBy: new Date(Date.now() - 4 * 3600_000).toISOString(),
    cancelledAt: new Date(Date.now() - 26 * 3600_000).toISOString(),
    cancelReason: 'DataEntryMistake',
    cancelRemark: 'Wrong buyer',
  },
};

/** An invoice of goods above the e-way bill threshold, three days old, with its live IRN and no e-way bill yet. */
const goods = (portalConnected: boolean): EInvoiceDetail => ({
  ...live(portalConnected),
  date: new Date(Date.now() - 3 * 86_400_000).toISOString().slice(0, 10),
  ewayBillNeeded: true,
});

/** The instant as a `datetime-local` value, the browser's local time, to the minute. */
const localInput = (at: Date) => {
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${at.getFullYear()}-${pad(at.getMonth() + 1)}-${pad(at.getDate())}T${pad(at.getHours())}:${pad(at.getMinutes())}`;
};

/** Enters an e-way bill from its print: its number and when it was generated (an hour ago); its validity left blank. */
async function enterEwayBill(user: ReturnType<typeof userEvent.setup>, form: ReturnType<typeof within>, ewbNo: string) {
  const number = form.getByRole('textbox', { name: /E-way bill number/ });
  await user.clear(number);
  await user.type(number, ewbNo);
  fireEvent.change(form.getByLabelText(/Generated on/), {
    target: { value: localInput(new Date(Date.now() - 3600_000)) },
  });
}

/** A refusal as the server sends it: a business rule is a 422 with its code and its own sentence. */
const refused = (code: string, detail: string, status = 422) =>
  json({ title: 'Cannot complete this action', status, code, detail }, status);

let server: ReturnType<typeof fakeServer>;

/** Opens the register and the bill's dialog, with `answers` for the bill's calls. */
async function openBill(answers: Record<string, unknown>) {
  server = fakeServer({ 'GET /api/v1/billing/einvoices': REGISTER, ...answers });
  const user = userEvent.setup();
  openScreen('/billing/einvoices');
  await user.click(await screen.findByText('NHOB260000123 (Bill)', {}, SLOW));
  const dialog = await screen.findByRole('dialog', {}, SLOW);
  await within(dialog).findByText(/Malabar Traders Ltd · GSTIN 32AAACH7409R1ZA/, {}, SLOW);
  return { user, dialog: within(dialog) };
}

const reads = () => server.requests.filter((r) => r.method === 'GET' && r.path === DOC).length;
const posted = (path: string): FakeRequest[] => server.requests.filter((r) => r.method === 'POST' && r.path === path);

beforeEach(() => {
  stubBrowserApis();
});

afterEach(() => {
  expect(server.unanswered).toEqual([]);
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('GST e-invoices', SCREEN_TESTS, () => {
  it("connected, keeps the portal's button as the main action and records an IRN generated on the portal by hand", async () => {
    let detail = pending(true);
    const recordAnswers: unknown[] = [
      refused(
        'einvoice.irp_unconfirmed',
        `The portal does not confirm IRN ${IRN} for this invoice. The portal's acknowledgement number for it is 112610012345679, not 112610012345678. Nothing was recorded.`,
      ),
      refused(
        'einvoice.irp_unreachable',
        'The e-invoice portal did not answer. The IRN could not be checked with the portal, and nothing was recorded. Try again once the portal answers.',
      ),
    ];
    const { user, dialog } = await openBill({
      [`GET ${DOC}`]: () => detail,
      [`POST ${DOC}/irn/generate`]: refused(
        'einvoice.irp_unreachable',
        'The e-invoice portal did not answer. Nothing was recorded; try again.',
      ),
      [`POST ${DOC}/irn`]: () => recordAnswers.shift(),
    });

    // The portal's own button stays the main action; recording by hand is folded away under it.
    const ask = dialog.getByRole('button', { name: 'Get IRN from the portal' });
    const byHand = dialog.getByRole('button', { name: 'Record an IRN generated on the portal' });
    expect(byHand).toHaveAttribute('aria-expanded', 'false');
    expect(dialog.queryByRole('textbox', { name: /Acknowledgement number/ })).not.toBeInTheDocument();
    expect(dialog.queryByRole('button', { name: 'Record IRN' })).not.toBeInTheDocument();

    // The portal's API does not answer: nothing changed, and the dialog reads the document again.
    let before = reads();
    await user.click(ask);
    expect(await dialog.findByText('No IRN yet: the portal did not answer', {}, SLOW)).toBeInTheDocument();
    expect(dialog.getByText(/did not answer\. Nothing was recorded; try again\./)).toBeInTheDocument();
    await waitFor(() => expect(reads()).toBe(before + 1));

    // The IRN generated on the portal's screens is recorded by hand.
    await user.click(byHand);
    expect(byHand).toHaveAttribute('aria-expanded', 'true');
    const panel = within(dialog.getByRole('group', { name: 'Record an IRN generated on the portal' }));
    expect(byHand).toHaveAttribute('aria-controls', dialog.getByRole('group', { name: /generated on the portal/ }).id);
    expect(panel.getByRole('button', { name: 'Download e-invoice JSON' })).toBeEnabled();
    const record = panel.getByRole('button', { name: 'Record IRN' });
    expect(record).toBeDisabled();
    await user.type(panel.getByRole('textbox', { name: /Acknowledgement number/ }), ACK_NO);
    await user.click(panel.getByRole('textbox', { name: /Signed QR code/ }));
    await user.paste(SIGNED_QR);
    expect(record).toBeEnabled();

    // The portal holds that IRN under another acknowledgement: refused, and what was typed stays to be corrected.
    before = reads();
    await user.click(record);
    expect(
      await panel.findByText('Not recorded: compare what was entered with the portal', {}, SLOW),
    ).toBeInTheDocument();
    expect(
      panel.getByText(/acknowledgement number for it is 112610012345679, not 112610012345678/),
    ).toBeInTheDocument();
    expect(posted(`${DOC}/irn`).at(-1)?.body).toEqual({ ackNo: ACK_NO, signedQrCode: SIGNED_QR });
    await waitFor(() => expect(reads()).toBe(before + 1));
    expect(panel.getByRole('textbox', { name: /Acknowledgement number/ })).toHaveValue(ACK_NO);
    expect(panel.getByRole('textbox', { name: /Signed QR code/ })).toHaveValue(SIGNED_QR);
    expect(byHand).toHaveAttribute('aria-expanded', 'true');

    // The portal cannot be asked about it: not recorded yet, and trying again takes one click.
    await user.click(record);
    expect(await panel.findByText('Not recorded yet: the portal could not be asked', {}, SLOW)).toBeInTheDocument();
    expect(panel.getByText(/could not be checked with the portal, and nothing was recorded/)).toBeInTheDocument();
    await expectNoAxeViolations();

    // Confirmed by the portal, it is recorded: the dialog shows the IRN and the form is gone.
    detail = live(true);
    recordAnswers.push(live(true));
    await user.click(record);
    expect(await dialog.findByText('a'.repeat(64), {}, SLOW)).toBeInTheDocument();
    expect(dialog.queryByRole('button', { name: 'Record an IRN generated on the portal' })).not.toBeInTheDocument();
    expect(dialog.queryByRole('button', { name: 'Get IRN from the portal' })).not.toBeInTheDocument();
    expect(posted(`${DOC}/irn`)).toHaveLength(3);
  });

  it('connected, says when the portal holds the IRN asked for as cancelled', async () => {
    const { user, dialog } = await openBill({
      [`GET ${DOC}`]: pending(true),
      [`POST ${DOC}/irn/generate`]: refused(
        'einvoice.irp_cancelled',
        `The portal holds this invoice's number under IRN ${IRN}, cancelled there, and registers a number only once: nothing is recorded. Cancel the invoice here and issue it again under a new number.`,
      ),
    });

    await user.click(dialog.getByRole('button', { name: 'Get IRN from the portal' }));
    expect(await dialog.findByText('Cancelled on the portal: it needs a new number', {}, SLOW)).toBeInTheDocument();
    expect(dialog.getByText(/issue it again under a new number/)).toBeInTheDocument();
  });

  it('connected, keeps the cancellation to try again when the portal left its outcome unknown', async () => {
    const { user, dialog } = await openBill({
      [`GET ${DOC}`]: live(true),
      [`POST ${DOC}/irn/cancel`]: refused(
        'einvoice.irp_cancel_unknown',
        `The e-invoice portal did not answer. Whether the portal cancelled IRN ${IRN} is not known: it did not say how it holds it. Try again once the portal answers: HC360 then asks it first, and records the cancellation if it was made.`,
      ),
    });

    await user.type(dialog.getByRole('textbox', { name: /Remark/ }), 'Wrong buyer');
    const cancel = dialog.getByRole('button', { name: 'Cancel IRN on the portal' });
    const before = reads();
    await user.click(cancel);
    expect(await dialog.findByText('Not known whether the portal cancelled the IRN', {}, SLOW)).toBeInTheDocument();
    expect(dialog.getByText(/records the cancellation if it was made/)).toBeInTheDocument();
    expect(posted(`${DOC}/irn/cancel`)[0]?.body).toEqual({
      reason: 'DataEntryMistake',
      remark: 'Wrong buyer',
      version: 3,
    });
    await waitFor(() => expect(reads()).toBe(before + 1));
    expect(dialog.getByRole('textbox', { name: /Remark/ })).toHaveValue('Wrong buyer');
    expect(cancel).toBeEnabled();
  });

  it('connected, past the 24 hours, asks the portal whether a cancellation made in time went through', async () => {
    let detail = lapsed(true);
    const answers: unknown[] = [
      refused(
        'einvoice.cancel_window',
        'The portal cancels an IRN only within 24 hours of its generation. Issue a credit note against the invoice instead.',
      ),
      refused(
        'einvoice.irp_cancel_unknown',
        `The portal cancels an IRN only within 24 hours of its generation, and did not say whether it cancelled IRN ${IRN} in time (2283: IRN details are not found). Check the IRN on the portal: while it is active there, issue a credit note against the invoice instead.`,
      ),
    ];
    const { user, dialog } = await openBill({
      [`GET ${DOC}`]: () => detail,
      [`POST ${DOC}/irn/cancel`]: () => answers.shift(),
    });

    // The IRN stays; asking about a cancellation made in time is folded away under its own button.
    expect(dialog.getByText(/More than 24 hours have passed/)).toBeInTheDocument();
    expect(dialog.queryByRole('button', { name: 'Cancel IRN on the portal' })).not.toBeInTheDocument();
    const ask = dialog.getByRole('button', { name: 'Ask the portal whether it was cancelled' });
    expect(ask).toHaveAttribute('aria-expanded', 'false');
    expect(dialog.queryByRole('textbox', { name: /Remark/ })).not.toBeInTheDocument();

    await user.click(ask);
    expect(ask).toHaveAttribute('aria-expanded', 'true');
    const panel = within(dialog.getByRole('group', { name: 'Ask the portal whether it was cancelled' }));
    const send = panel.getByRole('button', { name: 'Ask the portal' });
    expect(send).toBeDisabled();
    await user.type(panel.getByRole('textbox', { name: /Remark/ }), 'Wrong buyer');

    // The portal holds the IRN as active: nothing was cancelled in time, and a credit note undoes the invoice.
    const before = reads();
    await user.click(send);
    expect(await panel.findByText('Not cancelled on the portal: the IRN stays', {}, SLOW)).toBeInTheDocument();
    expect(panel.getByText(/Issue a credit note against the invoice instead/)).toBeInTheDocument();
    expect(posted(`${DOC}/irn/cancel`)[0]?.body).toEqual({
      reason: 'DataEntryMistake',
      remark: 'Wrong buyer',
      version: 3,
    });
    await waitFor(() => expect(reads()).toBe(before + 1));

    // The portal would not say how it holds the IRN: it is to be looked up there.
    await user.click(send);
    expect(await panel.findByText('Not known whether the portal cancelled the IRN', {}, SLOW)).toBeInTheDocument();
    expect(panel.getByText(/Check the IRN on the portal/)).toBeInTheDocument();
    expect(panel.getByRole('textbox', { name: /Remark/ })).toHaveValue('Wrong buyer');
    await expectNoAxeViolations();

    // The portal had cancelled it in time: the cancellation is recorded, and the question goes.
    detail = CANCELLED_IN_TIME;
    answers.push(CANCELLED_IN_TIME);
    await user.click(send);
    expect(await dialog.findByText('IRN cancelled', {}, SLOW)).toBeInTheDocument();
    expect(dialog.queryByRole('button', { name: 'Ask the portal whether it was cancelled' })).not.toBeInTheDocument();
    expect(posted(`${DOC}/irn/cancel`)).toHaveLength(3);
  });

  it('not connected, past the 24 hours, only says a credit note undoes the invoice', async () => {
    const { dialog } = await openBill({ [`GET ${DOC}`]: lapsed(false) });

    expect(dialog.getByText(/More than 24 hours have passed/)).toBeInTheDocument();
    expect(dialog.queryByRole('button', { name: 'Ask the portal whether it was cancelled' })).not.toBeInTheDocument();
    expect(dialog.queryByRole('button', { name: 'Record IRN cancellation' })).not.toBeInTheDocument();
  });

  it('not connected, records the IRN by hand as before, with the field the server refused named', async () => {
    const { user, dialog } = await openBill({
      [`GET ${DOC}`]: pending(false),
      [`POST ${DOC}/irn`]: json(
        {
          title: 'One or more validation errors occurred.',
          status: 400,
          errors: {
            signedQrCode: ["This signed QR code is not this invoice's: The code's buyer GSTIN is 33AAACH7409R1Z8."],
          },
        },
        400,
      ),
    });

    expect(dialog.queryByRole('button', { name: 'Record an IRN generated on the portal' })).not.toBeInTheDocument();
    expect(dialog.queryByRole('button', { name: 'Get IRN from the portal' })).not.toBeInTheDocument();
    await user.type(dialog.getByRole('textbox', { name: /Acknowledgement number/ }), ACK_NO);
    await user.click(dialog.getByRole('textbox', { name: /Signed QR code/ }));
    await user.paste(SIGNED_QR);
    await user.click(dialog.getByRole('button', { name: 'Record IRN' }));
    expect(await dialog.findByText('The IRN was not recorded', {}, SLOW)).toBeInTheDocument();
    expect(dialog.getByText(/The code's buyer GSTIN is 33AAACH7409R1Z8\./)).toBeInTheDocument();
  });

  it('not connected, records an e-way bill generated on the e-way bill portal from its print', async () => {
    let detail = goods(false);
    const { user, dialog } = await openBill({
      [`GET ${DOC}`]: () => detail,
      [`POST ${DOC}/ewaybill/record`]: () => {
        detail = {
          ...goods(false),
          registration: { ...REGISTRATION, ewbNo: 331000123456, ewbDate: now, ewbValidTill: null },
        };
        return detail;
      },
    });

    // Not connected, the portal cannot generate one: entering it from its print is the dialog's main action.
    expect(dialog.queryByRole('button', { name: 'Generate e-way bill' })).not.toBeInTheDocument();
    expect(dialog.queryByRole('button', { name: 'Record an e-way bill generated elsewhere' })).not.toBeInTheDocument();
    expect(dialog.getByText(/generate the e-way bill on the e-way bill portal/)).toBeInTheDocument();
    const record = dialog.getByRole('button', { name: 'Record e-way bill' });
    expect(record).toBeDisabled();
    await enterEwayBill(user, dialog, '3310 0012 345');
    expect(record).toBeDisabled();
    expect(record).toHaveAttribute('title', expect.stringContaining('the 12 digits on its print'));
    await enterEwayBill(user, dialog, '3310 0012 3456');
    expect(record).toBeEnabled();
    await expectNoAxeViolations();

    await user.click(record);
    expect(await dialog.findByText(/E-way bill 331000123456/, {}, SLOW)).toBeInTheDocument();
    const sent = posted(`${DOC}/ewaybill/record`)[0]?.body as { ewbNo: string; generatedAt: string; validUntil: null };
    expect(sent.ewbNo).toBe('3310 0012 3456');
    expect(sent.validUntil).toBeNull();
    expect(Date.now() - Date.parse(sent.generatedAt)).toBeGreaterThan(3500_000);
    expect(dialog.queryByRole('button', { name: 'Record e-way bill' })).not.toBeInTheDocument();
  });

  it("connected, keeps recording one generated elsewhere under the portal's way, and says when the portal holds another", async () => {
    let detail = goods(true);
    const recordAnswers: unknown[] = [
      refused(
        'einvoice.ewaybill_unconfirmed',
        "The portal holds e-way bill 331000999999 for this invoice's IRN, not 331000123456. Nothing was recorded.",
      ),
    ];
    const { user, dialog } = await openBill({
      [`GET ${DOC}`]: () => detail,
      [`POST ${DOC}/ewaybill/record`]: () => recordAnswers.shift(),
    });

    // The portal's own way stays the main action; recording by hand is folded away under it.
    expect(dialog.getByRole('button', { name: 'Generate e-way bill' })).toBeInTheDocument();
    const byHand = dialog.getByRole('button', { name: 'Record an e-way bill generated elsewhere' });
    expect(byHand).toHaveAttribute('aria-expanded', 'false');
    await user.click(byHand);
    const panel = within(dialog.getByRole('group', { name: 'Record an e-way bill generated elsewhere' }));
    await enterEwayBill(user, panel, '331000123456');

    // The portal holds another e-way bill for the IRN: refused in plain words, what was typed stays to be corrected.
    const before = reads();
    await user.click(panel.getByRole('button', { name: 'Record e-way bill' }));
    expect(
      await panel.findByText('Not recorded: the portal does not confirm this e-way bill', {}, SLOW),
    ).toBeInTheDocument();
    expect(panel.getByText(/holds e-way bill 331000999999 for this invoice's IRN/)).toBeInTheDocument();
    await waitFor(() => expect(reads()).toBe(before + 1));
    expect(panel.getByRole('textbox', { name: /E-way bill number/ })).toHaveValue('331000123456');
    await expectNoAxeViolations();

    // Corrected to the portal's number, it is recorded.
    detail = {
      ...goods(true),
      registration: { ...REGISTRATION, ewbNo: 331000999999, ewbDate: now, ewbValidTill: null },
    };
    recordAnswers.push(detail);
    await enterEwayBill(user, panel, '331000999999');
    await user.click(panel.getByRole('button', { name: 'Record e-way bill' }));
    expect(await dialog.findByText(/E-way bill 331000999999/, {}, SLOW)).toBeInTheDocument();
    expect(dialog.queryByRole('button', { name: 'Record an e-way bill generated elsewhere' })).not.toBeInTheDocument();
  });
});
