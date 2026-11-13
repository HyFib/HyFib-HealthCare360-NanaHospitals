import type * as Core from '@hc360/core';
import { expectNoAxeViolations } from '@hc360/ui/testing';
import { screen, waitFor, within } from '@testing-library/react';
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
});
