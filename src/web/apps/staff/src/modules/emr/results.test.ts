import { describe, expect, it } from 'vitest';
import type { OrderItemDto } from './ordersApi';
import { orderLineResult, RESULT_READERS, timelineResult } from './results';
import type { TimelineEntry } from './timeline';

const REPORT = '0192c0de-0000-7000-8000-00000000a001';

function entry(partial: Partial<TimelineEntry>): TimelineEntry {
  return {
    type: 'Result',
    id: REPORT,
    occurredAt: '2026-10-02T05:00:00Z',
    title: 'X-ray chest PA',
    summary: 'Radiology',
    status: 'Reviewed',
    encounterId: null,
    isCritical: false,
    reportKind: 'Radiology',
    ...partial,
  };
}

function line(partial: Partial<OrderItemDto>): OrderItemDto {
  return {
    id: 'i1',
    serviceItemId: 's1',
    serviceCode: 'LAB-CBC',
    serviceName: 'Complete blood count',
    category: 'Laboratory',
    quantity: 1,
    status: 'Completed',
    notes: null,
    statusChangedAt: '2026-10-02T05:00:00Z',
    completedAt: '2026-10-02T05:00:00Z',
    resultReference: REPORT,
    cancelReason: null,
    ...partial,
  };
}

describe('timelineResult', () => {
  it('opens a result by its report and the department that made it', () => {
    expect(timelineResult(entry({ isCritical: true }))).toEqual({
      kind: 'Radiology',
      reportId: REPORT,
      title: 'X-ray chest PA',
      hasCriticalFindings: true,
      releasedAt: '2026-10-02T05:00:00Z',
    });
  });

  it('opens nothing for other entries, or for a result that does not say which report it is', () => {
    expect(timelineResult(entry({ type: 'Order', reportKind: null }))).toBeNull();
    expect(timelineResult(entry({ reportKind: null }))).toBeNull();
  });
});

describe('orderLineResult', () => {
  it('opens the report a department completed the line with', () => {
    expect(orderLineResult(line({}))).toEqual({
      kind: 'Laboratory',
      reportId: REPORT,
      title: 'Complete blood count',
      hasCriticalFindings: false,
      releasedAt: '2026-10-02T05:00:00Z',
    });
    expect(orderLineResult(line({ category: 'Cardiology', resultReference: REPORT.toUpperCase() }))?.reportId).toBe(
      REPORT,
    );
  });

  it('opens nothing for a pending line, another kind of service or a reference typed by hand', () => {
    expect(orderLineResult(line({ status: 'InProgress' }))).toBeNull();
    expect(orderLineResult(line({ category: 'Procedure' }))).toBeNull();
    expect(orderLineResult(line({ resultReference: 'Lab register page 12' }))).toBeNull();
    expect(orderLineResult(line({ resultReference: null }))).toBeNull();
  });
});

describe('RESULT_READERS', () => {
  it('asks for the permissions the departments check', () => {
    expect(RESULT_READERS.Laboratory).toBe('lab.orders.read');
    // An imaging report opens by its study: only these open both (the worklist alone does not open the report).
    expect(RESULT_READERS.Radiology).toEqual(['radiology.studies.read', 'radiology.reports.write']);
    expect(RESULT_READERS.Cardiology).toContain('cardiology.studies.read');
  });
});
