import { describe, expect, it } from 'vitest';
import {
  foundText,
  openRecordLink,
  openText,
  sweepStatus,
  whenText,
  type SimulatorOpenRecord,
  type SimulatorRecordFinding,
  type SimulatorRecordsView,
} from './simulatorRecordsApi';

const payments: SimulatorRecordFinding = {
  code: 'payments',
  title: 'Online payments the payment simulator took',
  severity: 'critical',
  count: 1234,
  amount: 56789,
  first: '2026-09-01T04:30:00Z',
  last: '2026-09-03T04:30:00Z',
  examples: ['NHRC260000123'],
  guidance: 'No money was received.',
  open: 0,
  openAmount: null,
};

const swept = (total: number, criticalOpen = 0): SimulatorRecordsView['latest'] => ({
  sweptAt: '2026-10-29T04:30:00Z',
  sweptBy: 'IT Officer',
  total,
  newest: total ? payments.last : null,
  findings: total ? [{ ...payments, open: criticalOpen }] : [],
  criticalOpen,
});

describe('sweepStatus', () => {
  it('asks for a sweep when there has been none', () => {
    expect(sweepStatus({ latest: null, signedOff: null, outstanding: false, liveServer: true })).toMatchObject({
      intent: 'info',
      title: 'Not swept yet',
    });
  });

  it('is clear when the sweep found nothing', () => {
    expect(sweepStatus({ latest: swept(0), signedOff: null, outstanding: false, liveServer: true })).toMatchObject({
      intent: 'success',
      title: 'No records made by simulators',
    });
  });

  it('warns on a live server until the records are signed off, and says who signed them off', () => {
    const live = sweepStatus({ latest: swept(1234), signedOff: null, outstanding: true, liveServer: true });
    expect(live.intent).toBe('warning');
    expect(live.title).toBe('1,234 records made by simulators, not signed off');

    const signed = sweepStatus({
      latest: swept(1234),
      signedOff: {
        signedOffAt: '2026-10-29T05:00:00Z',
        signedOffBy: 'IT Officer',
        sweptAt: '2026-10-29T04:30:00Z',
        coversUntil: payments.last,
        total: 1234,
        note: 'Receipts cancelled.',
      },
      outstanding: false,
      liveServer: true,
    });
    expect(signed.intent).toBe('success');
    expect(signed.text).toContain('IT Officer signed them off');
    expect(signed.text).toContain('Receipts cancelled.');
  });

  it('says how many critical records are not corrected yet, whatever was signed off, and that no sign-off is taken before', () => {
    const open = sweepStatus({ latest: swept(1234, 7), signedOff: null, outstanding: true, liveServer: true });
    expect(open.intent).toBe('warning');
    expect(open.title).toBe('7 records made by simulators not corrected yet');
    expect(open.text).toContain('No sign-off is taken before.');
    expect(sweepStatus({ latest: swept(1, 1), signedOff: null, outstanding: true, liveServer: true }).title).toBe(
      '1 record made by simulators not corrected yet',
    );
  });

  it('only informs on a training server, which may use the simulators', () => {
    expect(sweepStatus({ latest: swept(3), signedOff: null, outstanding: true, liveServer: false }).intent).toBe(
      'info',
    );
  });
});

describe('openText and openRecordLink', () => {
  it('shows what is still open, and a dash for the kinds the IT officer acknowledges', () => {
    expect(openText({ ...payments, open: 1200, openAmount: 4500 })).toBe('1,200, ₹4,500.00');
    expect(openText({ ...payments, open: 0, openAmount: null })).toBe('0');
    expect(openText({ ...payments, open: null })).toBe('—');
  });

  it('opens each record where it is corrected: a receipt on its own day in the hospital, a refund, a patient, a study', () => {
    const record = (
      entity: SimulatorOpenRecord['entity'],
      at: string | null = '2026-09-01T20:00:00Z',
    ): SimulatorOpenRecord => ({
      reference: 'NHRC260000123',
      at,
      amount: 500,
      entity,
      entityId: 'e-1',
      patientId: 'p-1',
    });
    // 20:00 UTC is 01:30 the next day in India: the receipts list of that day.
    expect(openRecordLink(record('Receipt'))).toMatchObject({
      to: '/billing/receipts',
      search: { q: 'NHRC260000123', from: '2026-09-02', to: '2026-09-02' },
    });
    expect(openRecordLink(record('Receipt', null)).search).toEqual({ q: 'NHRC260000123' });
    expect(openRecordLink(record('Refund')).to).toBe('/billing/refunds/e-1');
    expect(openRecordLink(record('Patient')).to).toBe('/patients/e-1');
    expect(openRecordLink(record('ImagingStudy')).to).toBe('/radiology/studies/e-1');
    expect(openRecordLink(record('LabOrder')).to).toBe('/lab/orders/e-1');
    expect(openRecordLink(record('UnmatchedLabResult'))).toMatchObject({
      to: '/lab/unmatched',
      search: { search: 'NHRC260000123' },
    });
    expect(openRecordLink(record('EInvoice'))).toMatchObject({
      to: '/billing/einvoices',
      search: { from: '2026-09-02', to: '2026-09-02' },
    });
    expect(openRecordLink(record('EInvoice', null)).search).toBeUndefined();
  });
});

describe('foundText and whenText', () => {
  it('gives the count with the money, and a range of dates', () => {
    expect(foundText(payments)).toBe('1,234, ₹56,789.00');
    expect(foundText({ ...payments, amount: null })).toBe('1,234');
    expect(whenText(payments)).toContain(' to ');
    expect(whenText({ ...payments, first: null, last: null })).toBe('—');
  });
});
