import { readdirSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { ApiError } from '@hc360/core';
import { describe, expect, it } from 'vitest';
import {
  documentLabel,
  ewayBillProblems,
  irnAnswer,
  irnAnswerCodes,
  irnProblems,
  localToIso,
  type IrnAction,
} from './einvoiceApi';

describe('irnProblems', () => {
  it('accepts the acknowledgement number and a signed QR code as the portal gives them', () => {
    expect(irnProblems('112610012345678', 'eyJhbGciOiJSUzI1NiJ9.eyJkYXRhIjoie30ifQ.c2ln')).toEqual([]);
  });

  it('asks for the digits and for the whole code', () => {
    expect(irnProblems('11261-001', 'not a code')).toEqual([
      'Enter the acknowledgement number: the 10 to 20 digits the portal gave.',
      'Paste the signed QR code exactly as the portal gave it (three parts separated by dots).',
    ]);
  });
});

describe('ewayBillProblems', () => {
  // The browser's local times, as datetime-local gives them; the invoice of 10 Nov 2026, checked on 14 Nov 2026.
  const now = new Date('2026-11-14T09:00');
  const problems = (ewbNo: string, generatedAt: string, validUntil = '', invoiceDate = '2026-11-10', at = now) =>
    ewayBillProblems(ewbNo, generatedAt, validUntil, invoiceDate, at);

  it('accepts an e-way bill the portal could have generated for the invoice, as its print shows it', () => {
    expect(problems('331000123456', '2026-11-12T10:15', '2026-11-14T23:59')).toEqual([]);
    expect(problems('3310 0012 3456', '2026-11-12T10:15')).toEqual([]);
  });

  it('asks for 12 digits, or 14 for one generated from 1 January 2027', () => {
    const digits =
      'Enter the e-way bill number: the 12 digits on its print (14 for one generated from 1 January 2027).';
    expect(problems('33100012345', '2026-11-12T10:15')).toEqual([digits]);
    expect(problems('031000123456', '2026-11-12T10:15')).toEqual([digits]);
    expect(problems('33100012345678', '2026-11-12T10:15')).toEqual([
      'E-way bill numbers have 14 digits only from 1 January 2027.',
    ]);
    expect(problems('33100012345678', '2027-01-01T00:30', '', '2026-12-31', new Date('2027-01-02T09:00'))).toEqual([]);
  });

  it('asks for when it was generated: not before the invoice, not in the future, not 180 days after it', () => {
    expect(problems('331000123456', '')).toEqual(['Enter when the e-way bill was generated, as its print shows.']);
    expect(problems('331000123456', '2026-11-09T23:00')).toEqual([
      'The e-way bill is dated before the invoice it names.',
    ]);
    expect(problems('331000123456', '2026-11-14T10:00')).toEqual(['The e-way bill is dated in the future.']);
    expect(problems('331000123456', '2026-10-28T11:00', '', '2026-05-01')).toEqual([]);
    expect(problems('331000123456', '2026-10-29T11:00', '', '2026-05-01')).toEqual([
      'The portal generates an e-way bill only for an invoice at most 180 days old.',
    ]);
  });

  it('takes its validity blank while only Part-A is filled, else after its generation and within 360 days', () => {
    expect(problems('331000123456', '2026-11-12T10:15', '2026-11-12T10:15')).toEqual([
      'The e-way bill must be valid until a time after it was generated.',
    ]);
    expect(problems('331000123456', '2026-11-12T10:15', '2027-11-07T10:15')).toEqual([]);
    expect(problems('331000123456', '2026-11-12T10:15', '2027-11-07T10:16')).toEqual([
      'The portal extends an e-way bill to at most 360 days after its generation.',
    ]);
    expect(problems('331000123456', '2026-11-12T10:15', '2026-11-14')).toEqual([
      'Enter the end of its validity in full, or leave it blank while only Part-A is filled.',
    ]);
  });

  it('sends the local times as instants', () => {
    expect(localToIso('2026-11-12T10:15')).toBe(new Date('2026-11-12T10:15').toISOString());
    expect(localToIso('2026-11-12')).toBeNull();
  });
});

describe('documentLabel', () => {
  it('names an invoice by where it comes from', () => {
    expect(documentLabel({ number: 'NHOB260000123', source: 'bill', type: 'INV', against: null })).toBe(
      'NHOB260000123 (Bill)',
    );
  });

  it('names a credit note with the invoice it reduces', () => {
    expect(documentLabel({ number: 'NHCN26000007', source: 'creditnote', type: 'CRN', against: 'NHOB260000123' })).toBe(
      'NHCN26000007 (Credit note against NHOB260000123)',
    );
    expect(
      documentLabel({ number: 'NHPHR26000004', source: 'pharmacyreturn', type: 'CRN', against: 'NHPH260000456' }),
    ).toBe('NHPHR26000004 (Pharmacy return against NHPH260000456)');
  });
});

/** A refusal as the server sends it: a business rule is a 422 with its code and its own sentence. */
const refusal = (code: string, detail: string, status = 422) =>
  new ApiError({ status, title: 'Cannot complete this action', code, detail });

const IRN = '5a1c'.repeat(16);

describe('irnAnswer', () => {
  // The server's own sentences (EInvoiceService), which the heading sits over.
  it.each<[IrnAction, string, string, 'warning' | 'error', string]>([
    [
      'record',
      'einvoice.irp_unconfirmed',
      `The portal does not confirm IRN ${IRN} for this invoice. The portal's acknowledgement number for it is 112610012345679, not 112610012345678. Nothing was recorded.`,
      'error',
      'Not recorded: compare what was entered with the portal',
    ],
    [
      'record',
      'einvoice.irp_unreachable',
      'The e-invoice portal did not answer. The IRN could not be checked with the portal, and nothing was recorded. Try again once the portal answers.',
      'warning',
      'Not recorded yet: the portal could not be asked',
    ],
    [
      'generate',
      'einvoice.irp_unreachable',
      'The e-invoice portal did not answer. Nothing was recorded; try again.',
      'warning',
      'No IRN yet: the portal did not answer',
    ],
    [
      'generate',
      'einvoice.irp_cancelled',
      `The portal holds this invoice's number under IRN ${IRN}, cancelled there, and registers a number only once: nothing is recorded. Cancel the invoice here and issue it again under a new number.`,
      'error',
      'Cancelled on the portal: it needs a new number',
    ],
    [
      'cancel',
      'einvoice.irp_cancel_unknown',
      `The e-invoice portal did not answer. Whether the portal cancelled IRN ${IRN} is not known: it did not say how it holds it. Try again once the portal answers: HC360 then asks it first, and records the cancellation if it was made.`,
      'warning',
      'Not known whether the portal cancelled the IRN',
    ],
    [
      'cancel',
      'einvoice.irp_unreachable',
      `The e-invoice portal did not answer. The portal still holds IRN ${IRN} as active, so nothing was cancelled. Try again.`,
      'warning',
      'Not cancelled: the IRN is still active on the portal',
    ],
    // Past the 24 hours, asking whether a cancellation made in time went through.
    [
      'askCancelled',
      'einvoice.cancel_window',
      'The portal cancels an IRN only within 24 hours of its generation. Issue a credit note against the invoice instead.',
      'error',
      'Not cancelled on the portal: the IRN stays',
    ],
    [
      'askCancelled',
      'einvoice.irp_cancel_unknown',
      `The portal cancels an IRN only within 24 hours of its generation, and did not say whether it cancelled IRN ${IRN} in time (2283: IRN details are not found). Check the IRN on the portal: while it is active there, issue a credit note against the invoice instead.`,
      'warning',
      'Not known whether the portal cancelled the IRN',
    ],
    [
      'askCancelled',
      'einvoice.irp_cancel_unknown',
      `The e-invoice portal did not answer. Whether the portal cancelled IRN ${IRN} is not known: it did not say how it holds it. Try again once the portal answers: HC360 then asks it first, and records the cancellation if it was made.`,
      'warning',
      'Not known whether the portal cancelled the IRN',
    ],
  ])("%s answered %s: a heading over the server's sentence", (action, code, detail, intent, title) => {
    expect(irnAnswer(action, refusal(code, detail))).toEqual({ intent, title, detail });
  });

  it("gives any other refusal the call's own heading over the server's sentence", () => {
    expect(
      irnAnswer('generate', refusal('einvoice.irp_refused', 'The e-invoice portal refused it (2172): Invalid GSTIN.')),
    ).toEqual({
      intent: 'error',
      title: 'The portal gave no IRN',
      detail: 'The e-invoice portal refused it (2172): Invalid GSTIN.',
    });
    expect(
      irnAnswer(
        'cancel',
        refusal(
          'einvoice.cancel_window',
          'The portal cancels an IRN only within 24 hours of its generation. Issue a credit note against the invoice instead.',
        ),
      ),
    ).toMatchObject({ intent: 'error', title: 'The cancellation was not recorded' });
    // A signed QR code naming another document is a field error: its message is the server's sentence.
    const wrongCode = new ApiError({
      status: 400,
      title: 'One or more validation errors occurred.',
      errors: {
        signedQrCode: ["This signed QR code is not this invoice's: The code's buyer GSTIN is 33AAACH7409R1Z8."],
      },
    });
    expect(irnAnswer('record', wrongCode)).toEqual({
      intent: 'error',
      title: 'The IRN was not recorded',
      detail: "This signed QR code is not this invoice's: The code's buyer GSTIN is 33AAACH7409R1Z8.",
    });
  });

  it('tells an answer whose outcome is open, or done already, from a refusal', () => {
    const offline = new ApiError({
      status: 0,
      title: 'Cannot reach the HealthCare360 server',
      detail:
        'Check the WiFi connection, then try again. Saving again with the same details will not save it twice; if you change anything, first check whether it was saved.',
      code: 'network',
    });
    expect(irnAnswer('record', offline)).toEqual({
      intent: 'warning',
      title: 'Cannot reach the HealthCare360 server',
      detail:
        'Check the WiFi connection, then try again. Saving again with the same details will not save it twice; if you change anything, first check whether it was saved.',
    });
    const stale = new ApiError({
      status: 409,
      code: 'concurrency',
      detail: 'This record was changed by someone else. Reload it and try again.',
    });
    expect(irnAnswer('cancel', stale)).toMatchObject({
      intent: 'warning',
      title: 'Changed elsewhere: the latest is shown, try again',
    });
    // An earlier attempt whose answer was lost on the way here registered it.
    const done = refusal(
      'einvoice.registered',
      'This invoice already has an IRN. Cancel it first to record another.',
      409,
    );
    expect(irnAnswer('record', done)).toMatchObject({ intent: 'warning', title: 'Already registered' });
    expect(irnAnswer('generate', done)).toMatchObject({ intent: 'warning', title: 'Already registered' });
  });

  it("falls back to the call's own heading for an error that is not the server's", () => {
    expect(irnAnswer('cancel', new TypeError('Failed to read'))).toEqual({
      intent: 'error',
      title: 'The cancellation was not recorded',
      detail: '',
    });
    expect(irnAnswer('askCancelled', new TypeError('Failed to read'))).toMatchObject({
      title: 'No cancellation was recorded',
    });
  });
});

describe("the server's codes", () => {
  // Each e-invoice code the dialog has words for must be one Billing sends: a code renamed or misspelled on either side
  // would otherwise leave staff with the call's plain "not done" where the outcome is open.
  const billing = join(
    import.meta.dirname,
    ...Array<string>(7).fill('..'),
    'src',
    'server',
    'Modules',
    'HyFib.HealthCare360.Modules.Billing',
  );
  const source = readdirSync(billing, { withFileTypes: true, recursive: true })
    .filter((entry) => entry.isFile() && entry.name.endsWith('.cs'))
    .map((entry) => readFileSync(join(entry.parentPath, entry.name), 'utf8'))
    .join('\n');

  it.each<IrnAction>(['record', 'generate', 'cancel', 'askCancelled', 'recordEwayBill'])(
    '%s: every e-invoice code it explains is sent',
    (action) => {
      const codes = irnAnswerCodes(action).filter((code) => code.startsWith('einvoice.'));
      expect(codes.length).toBeGreaterThan(0);
      expect(codes.filter((code) => !source.includes(`"${code}"`))).toEqual([]);
    },
  );
});
