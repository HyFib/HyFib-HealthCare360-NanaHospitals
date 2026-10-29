import { describe, expect, it } from 'vitest';
import {
  foundText,
  sweepStatus,
  whenText,
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
  examples: ['order_simABC'],
  guidance: 'No money was received.',
};

const swept = (total: number): SimulatorRecordsView['latest'] => ({
  sweptAt: '2026-10-29T04:30:00Z',
  sweptBy: 'IT Officer',
  total,
  newest: total ? payments.last : null,
  findings: total ? [payments] : [],
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

  it('only informs on a training server, which may use the simulators', () => {
    expect(sweepStatus({ latest: swept(3), signedOff: null, outstanding: true, liveServer: false }).intent).toBe(
      'info',
    );
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
