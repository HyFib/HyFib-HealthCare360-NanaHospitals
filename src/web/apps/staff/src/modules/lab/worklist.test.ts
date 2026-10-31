import { describe, expect, it } from 'vitest';
import type { ResultSheet, ResultSheetLine, WorklistItem } from './api';
import { criticalTubes, planBulkVerification, tubeProgress, worklistCounts } from './worklist';

function item(partial: Partial<WorklistItem>): WorklistItem {
  return {
    sampleId: 's',
    barcode: '000000001',
    labOrderId: 'o',
    accessionNumber: 'NHLB260000001',
    patientId: 'p',
    uhid: 'NH26000001',
    patientName: 'Asha Rao',
    age: '31Y',
    gender: 'Female',
    patientClass: 'OutPatient',
    priority: 'Routine',
    section: 'Biochemistry',
    status: 'Received',
    receivedAt: null,
    pending: 0,
    entered: 0,
    verified: 0,
    critical: 0,
    tests: ['KFT'],
    ...partial,
  };
}

describe('worklist helpers', () => {
  const items = [
    item({ sampleId: 'a', pending: 3, priority: 'Stat' }),
    item({ sampleId: 'b', entered: 2, critical: 1 }),
    item({ sampleId: 'c', entered: 1, pending: 1 }),
    item({ sampleId: 'd', verified: 4, critical: 1 }),
  ];

  it('finds critical values that still need verification', () => {
    expect(criticalTubes(items).map((i) => i.sampleId)).toEqual(['b']);
  });

  it('counts the bench workload', () => {
    expect(worklistCounts(items, 40)).toEqual({
      inLab: 40,
      awaitingEntry: 2,
      awaitingVerification: 2,
      critical: 1,
      stat: 1,
    });
  });

  it('describes progress in words', () => {
    expect(tubeProgress({ pending: 3, entered: 2, verified: 0 })).toBe('3 to enter · 2 to verify');
    expect(tubeProgress({ pending: 0, entered: 0, verified: 5 })).toBe('all verified');
    expect(tubeProgress({ pending: 0, entered: 0, verified: 0 })).toBe('no results yet');
  });
});

function line(partial: Partial<ResultSheetLine>): ResultSheetLine {
  return {
    resultId: 'r',
    labOrderItemId: 'i',
    orderedTest: 'KFT',
    labTestId: 't',
    testCode: 'CREA',
    testName: 'Creatinine',
    groupName: null,
    displayOrder: 1,
    resultType: 'Numeric',
    status: 'Entered',
    value: '0.9',
    unit: 'mg/dL',
    decimals: 2,
    options: [],
    flag: 'Normal',
    isCritical: false,
    referenceText: '0.7 - 1.3',
    previousValue: null,
    previousResultAt: null,
    deltaPercent: null,
    deltaExceeded: false,
    autoValidated: false,
    instrumentCode: null,
    instrumentUnit: null,
    instrumentFlags: null,
    instrumentStatus: null,
    instrumentComment: null,
    heldReasons: [],
    method: null,
    remarks: null,
    organism: null,
    colonyCount: null,
    susceptibilities: [],
    enteredAt: null,
    verifiedAt: null,
    hasCriticalCall: false,
    version: 1,
    ...partial,
  };
}

describe('bulk verification plan', () => {
  it('verifies only clean normal results and holds the rest', () => {
    const sheet: ResultSheet = {
      sampleId: 's1',
      barcode: '000000101',
      sampleStatus: 'InProcess',
      labOrderId: 'o1',
      accessionNumber: 'NHLB260000101',
      patientId: 'p1',
      uhid: 'NH26000001',
      patientName: 'Asha Rao',
      ageSex: '31Y / F',
      clinicalNotes: null,
      lines: [
        line({ resultId: 'ok' }),
        line({ resultId: 'high', flag: 'High' }),
        line({ resultId: 'crit', flag: 'CriticalHigh', isCritical: true }),
        line({ resultId: 'delta', deltaExceeded: true, deltaPercent: 80 }),
        line({ resultId: 'remark', remarks: 'Haemolysed, repeat advised' }),
        line({ resultId: 'pending', status: 'Pending', value: null, flag: null }),
        line({ resultId: 'done', status: 'Verified' }),
      ],
    };
    expect(planBulkVerification(sheet)).toMatchObject({ ready: ['ok'], held: 4 });
  });
});
