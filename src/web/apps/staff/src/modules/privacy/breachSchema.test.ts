import { describe, expect, it } from 'vitest';
import type { BreachDetail } from './api';
import {
  breachFormSchema,
  formGaps,
  fromBreachDetail,
  newBreachDefaults,
  toSaveBreachRequest,
  type BreachFormValues,
} from './breachSchema';

const values = (patch: Partial<BreachFormValues> = {}): BreachFormValues => ({
  ...newBreachDefaults(),
  title: 'Lab reports e-mailed to the wrong patient',
  description: 'Two reports were sent to the wrong address.',
  awareAt: '2026-09-29T10:15',
  ...patch,
});

function errors(v: BreachFormValues): Record<string, string> {
  const result = breachFormSchema.safeParse(v);
  return result.success
    ? {}
    : Object.fromEntries(result.error.issues.map((issue) => [issue.path.join('.'), issue.message]));
}

const detail: BreachDetail = {
  id: 'b1',
  breachNumber: 'DPB260001',
  title: 'Stolen laptop',
  description: 'A laptop with discharge summaries was stolen.',
  status: 'Open',
  awareAt: '2026-10-01T04:30:00Z',
  occurredFrom: '2026-09-30T18:30:00Z',
  occurredTo: null,
  nature: ['Confidentiality'],
  location: 'Ward 3 nurses’ station',
  dataCategories: ['Identity', 'Health'],
  estimatedAffected: 40,
  consequences: null,
  mitigation: 'Passwords changed.',
  principalSteps: null,
  cause: null,
  findings: 'A contractor left the laptop unlocked.',
  remediation: null,
  principalNotice: null,
  boardInformedAt: null,
  reportDueAt: '2026-10-04T04:30:00Z',
  extendedUntil: null,
  reportSubmittedAt: null,
  boardReference: null,
  closedAt: null,
  closingNote: null,
  notifiedAt: null,
  reportOverdue: false,
  affectedListed: 0,
  notified: 0,
  withoutContact: 0,
  missingForReport: [],
  missingForNotice: [],
  version: 7,
};

describe('breach form', () => {
  it('needs a title, a description and when the hospital became aware', () => {
    const e = errors({ ...newBreachDefaults(), awareAt: '' });
    expect(e.title).toBe('Give the breach a short title.');
    expect(e.description).toBe('Describe what happened.');
    expect(e.awareAt).toMatch(/became aware/);
    expect(errors(values())).toEqual({});
  });

  it('keeps the times in order and out of the future', () => {
    expect(errors(values({ awareAt: '2099-01-01T10:00' })).awareAt).toMatch(/cannot be in the future/);
    expect(errors(values({ occurredFrom: '2026-09-28T10:00', occurredTo: '2026-09-27T10:00' })).occurredTo).toBe(
      'The end must be after the start.',
    );
    expect(errors(values({ occurredFrom: '2026-09-29T11:00' })).occurredFrom).toMatch(/cannot have started after/);
  });

  it('restores drafts saved before findings were asked for', () => {
    const { findings: _dropped, ...older } = values({ cause: 'Unlocked laptop' });
    const parsed = breachFormSchema.parse(older);
    expect(toSaveBreachRequest(parsed, 2).findings).toBeNull();
    expect(errors(values({ findings: 'x'.repeat(4001) })).findings).toBeDefined();
  });

  it('keeps the SMS notice short', () => {
    expect(errors(values({ principalNotice: 'x'.repeat(320) }))).toEqual({});
    expect(errors(values({ principalNotice: 'x'.repeat(321) })).principalNotice).toBe(
      'Keep the SMS notice to 320 characters.',
    );
    expect(errors(values({ estimatedAffected: -1 })).estimatedAffected).toBe('The number cannot be negative.');
  });

  it('sends hospital times with their offset and empty answers as nothing', () => {
    const request = toSaveBreachRequest(
      breachFormSchema.parse(
        values({ occurredFrom: '2026-09-28T22:00', nature: ['Confidentiality', 'Confidentiality'], location: '  ' }),
      ),
      null,
    );
    expect(request.awareAt).toBe('2026-09-29T10:15:00+05:30');
    expect(request.occurredFrom).toBe('2026-09-28T22:00:00+05:30');
    expect(request.occurredTo).toBeNull();
    expect(request.nature).toEqual(['Confidentiality']);
    expect(request.location).toBeNull();
    expect(request.cause).toBeNull();
    expect(request.findings).toBeNull();
    expect(request.version).toBeNull();
  });

  it('edits a recorded breach with its version', () => {
    const form = fromBreachDetail(detail);
    expect(form.awareAt).toBe('2026-10-01T10:00');
    expect(form.occurredFrom).toBe('2026-10-01T00:00');
    expect(form.occurredTo).toBe('');
    expect(form.cause).toBe('');
    expect(form.findings).toBe('A contractor left the laptop unlocked.');
    const request = toSaveBreachRequest(form, detail.version);
    expect(request.findings).toBe('A contractor left the laptop unlocked.');
    expect(request.version).toBe(7);
    expect(request.mitigation).toBe('Passwords changed.');
    expect(request.dataCategories).toEqual(['Identity', 'Health']);
  });

  it('lists what the report and the notice still need, in the server’s words', () => {
    const gaps = formGaps(fromBreachDetail(detail));
    expect(gaps.report).toEqual(['the likely consequences', 'the cause', 'what stops it happening again']);
    expect(gaps.notice).toEqual([
      'the likely consequences',
      'what people can do to protect themselves',
      'the short notice for SMS',
    ]);
  });
});
