import type { HistoryRecordType, RecordHistoryDto } from '@/modules/history/api';
import type { FakeAnswers } from '../fakeApi';
import { RECORD_ID } from '../roleAccessIds';

// A record's history from the audit trail: who registered it, and who changed which field from what to what.

const now = Date.now();
const hoursAgo = (hours: number) => new Date(now - hours * 3_600_000).toISOString();

const history = (entityType: HistoryRecordType, title: string): RecordHistoryDto => ({
  entityType,
  entityId: RECORD_ID,
  title,
  entries: {
    items: [
      {
        id: 2,
        at: hoursAgo(1),
        by: 'Anita Rao',
        action: `${entityType}.update`,
        part: null,
        what: 'Changed',
        changes: [{ field: 'Notes', from: null, to: 'Called about the bill' }],
      },
      {
        id: 1,
        at: hoursAgo(5),
        by: 'Ravi Kumar',
        action: `${entityType}.create`,
        part: null,
        what: 'Created',
        changes: [],
      },
    ],
    page: 1,
    pageSize: 25,
    totalCount: 2,
  },
});

const TYPES: [HistoryRecordType, string][] = [
  ['Patient', 'patient NH26000001'],
  ['Visit', 'visit NHV26000001'],
  ['Bill', 'bill NHOPB2600012'],
  ['LabOrder', 'lab order NHLB26000001'],
  ['LabReport', 'lab report NHLB26000001'],
  ['Admission', 'admission NHIP26000001'],
];

export const ANSWERS: FakeAnswers = Object.fromEntries(
  TYPES.map(([type, title]) => [`GET /api/v1/history/${type}/${RECORD_ID}`, history(type, title)]),
);
