import { formatDateTime } from '@hc360/core';
import { describe, expect, it } from 'vitest';
import {
  answerHint,
  availableCorrectionFields,
  breachClock,
  breachListClock,
  breachPeopleText,
  breachStatusTone,
  breachSteps,
  canAddCorrection,
  clockTone,
  closedText,
  correctionKind,
  correctionProgress,
  durationText,
  elapsedText,
  extensionMinimum,
  identityRequired,
  isMedicalRecordsRule,
  isRecordId,
  itemDatesText,
  keptUntilText,
  latestErasureSummary,
  listText,
  nextBreachStep,
  nomineeSourceText,
  noticeChannelsText,
  officerChoices,
  outcomeBlocker,
  parseUhids,
  phoneTail,
  replyByText,
  requestActions,
  requestStatusTone,
  retentionFixedReason,
  retentionPeriodText,
  retentionRange,
  retentionRunSource,
  retentionValueError,
  suggestedOutcome,
  suggestedReply,
  type BreachFacts,
  type RequestFacts,
  type RetentionRuleFacts,
} from './rules';

/** 12:00 noon IST on 9 Oct 2026. */
const NOW = Date.parse('2026-10-09T06:30:00Z');
const HOUR = 3_600_000;

const request = (patch: Partial<RequestFacts> = {}): RequestFacts => ({
  type: 'Access',
  status: 'InProgress',
  identityVerified: true,
  corrections: [],
  ...patch,
});

describe('reply-by deadline', () => {
  it('counts calendar days in hospital time', () => {
    expect(replyByText('2026-10-12T10:30:00Z', NOW)).toEqual({
      text: 'Reply by 12 Oct (in 3 days)',
      overdue: false,
      soon: true,
    });
    expect(replyByText('2026-10-09T12:30:00Z', NOW).text).toBe('Reply by 9 Oct (today)');
    expect(replyByText('2026-10-10T04:00:00Z', NOW).text).toBe('Reply by 10 Oct (tomorrow)');
    expect(replyByText('2026-10-30T10:30:00Z', NOW)).toEqual({
      text: 'Reply by 30 Oct (in 21 days)',
      overdue: false,
      soon: false,
    });
  });

  it('adds the year when the deadline is next year', () => {
    expect(replyByText('2027-01-05T10:00:00Z', NOW).text).toBe('Reply by 5 Jan 2027 (in 88 days)');
  });

  it('says how long it is overdue, in words', () => {
    expect(replyByText('2026-10-07T06:00:00Z', NOW)).toEqual({ text: 'Overdue by 2 days', overdue: true, soon: false });
    expect(replyByText('2026-10-08T12:00:00Z', NOW).text).toBe('Overdue by 1 day');
    // Due earlier today: hours, not "0 days".
    expect(replyByText('2026-10-09T03:30:00Z', NOW).text).toBe('Overdue by 3 h');
    expect(replyByText('not a date', NOW).text).toBe('—');
  });
});

describe('durations', () => {
  it('gives the time left precisely', () => {
    expect(durationText(20 * 60_000)).toBe('20 min');
    expect(durationText(5 * HOUR + 20 * 60_000)).toBe('5 h 20 min');
    expect(durationText(5 * HOUR)).toBe('5 h');
    expect(durationText(50 * HOUR)).toBe('2 days 2 h');
    expect(durationText(24 * HOUR)).toBe('1 day');
    expect(durationText(10_000)).toBe('less than a minute');
  });

  it('gives the time gone roughly', () => {
    expect(elapsedText(10_000)).toBe('1 min');
    expect(elapsedText(3 * HOUR + 40 * 60_000)).toBe('3 h');
    expect(elapsedText(47 * HOUR)).toBe('47 h');
    expect(elapsedText(80 * HOUR)).toBe('3 days');
  });
});

describe('request actions', () => {
  it('offers nothing but reading without the manage permission', () => {
    expect(requestActions(request({ status: 'Submitted' }), false)).toEqual({
      start: false,
      verify: false,
      assign: false,
      note: false,
      answer: false,
    });
  });

  it('follows the status', () => {
    expect(requestActions(request({ status: 'Submitted', identityVerified: false }), true)).toEqual({
      start: true,
      verify: true,
      assign: true,
      note: true,
      answer: true,
    });
    expect(requestActions(request({ status: 'InProgress' }), true)).toEqual({
      start: false,
      verify: false,
      assign: true,
      note: true,
      answer: true,
    });
    // A closed request only takes staff notes.
    expect(requestActions(request({ status: 'Completed', identityVerified: false }), true)).toEqual({
      start: false,
      verify: false,
      assign: false,
      note: true,
      answer: false,
    });
  });
});

describe('answering', () => {
  it('needs a checked identity to give out, change or erase data, but never to decline', () => {
    for (const type of ['Access', 'Correction', 'Erasure'] as const) {
      const unchecked = request({ type, identityVerified: false });
      expect(outcomeBlocker(unchecked, 'Completed')).toMatch(/identity/);
      expect(outcomeBlocker(unchecked, 'PartlyCompleted')).toMatch(/identity/);
      expect(outcomeBlocker(unchecked, 'Declined')).toBeNull();
    }
    expect(identityRequired('Grievance')).toBe(false);
    expect(outcomeBlocker(request({ type: 'Grievance', identityVerified: false }), 'Completed')).toBeNull();
  });

  it('needs every requested change decided before completing a correction', () => {
    const pending = request({
      type: 'Correction',
      corrections: [{ decision: 'Accepted' }, { decision: 'Pending' }, { decision: 'Pending' }],
    });
    expect(outcomeBlocker(pending, 'Completed')).toContain('2 still to decide');
    expect(outcomeBlocker(pending, 'Declined')).toBeNull();
    expect(
      outcomeBlocker(request({ type: 'Correction', corrections: [{ decision: 'Declined' }] }), 'PartlyCompleted'),
    ).toBeNull();
    expect(correctionProgress(pending.corrections)).toEqual({ total: 3, pending: 2, accepted: 1, declined: 0 });
  });

  it('cannot answer a closed request', () => {
    expect(outcomeBlocker(request({ status: 'Declined' }), 'Declined')).toBe('This request is already closed.');
    expect(answerHint(request({ status: 'Completed' }))).toBeNull();
    expect(answerHint(request({ identityVerified: false }))).toMatch(/You can still decline/);
    expect(answerHint(request())).toBeNull();
  });

  it('suggests an outcome from the decisions and what the law keeps', () => {
    const decided = (...decisions: ('Accepted' | 'Declined')[]) =>
      request({ type: 'Correction', corrections: decisions.map((decision) => ({ decision })) });
    expect(suggestedOutcome(decided('Accepted', 'Accepted'))).toBe('Completed');
    expect(suggestedOutcome(decided('Accepted', 'Declined'))).toBe('PartlyCompleted');
    expect(suggestedOutcome(decided('Declined'))).toBe('Declined');
    expect(suggestedOutcome(request({ type: 'Erasure' }), { keptByLaw: true })).toBe('PartlyCompleted');
    expect(suggestedOutcome(request({ type: 'Erasure' }), { keptByLaw: false })).toBe('Completed');
    expect(suggestedOutcome(request({ type: 'Grievance' }))).toBe('Completed');
  });

  it('drafts a reply staff can edit', () => {
    const corrections = [
      { fieldLabel: 'Name', decision: 'Accepted' as const, note: null },
      { fieldLabel: 'Address', decision: 'Accepted' as const, note: null },
      { fieldLabel: 'Date of birth', decision: 'Declined' as const, note: 'matches the birth certificate' },
    ];
    expect(suggestedReply({ type: 'Correction', outcome: 'PartlyCompleted', corrections, erasureSummary: null })).toBe(
      'We have made these changes: Name; Address. We did not make these changes: Date of birth (matches the birth certificate).',
    );
    expect(
      suggestedReply({
        type: 'Erasure',
        outcome: 'Completed',
        corrections: [],
        erasureSummary: 'Erased: nominee removed.',
      }),
    ).toBe('Erased: nominee removed.');
    expect(suggestedReply({ type: 'Access', outcome: 'Completed', corrections: [], erasureSummary: null })).toMatch(
      /data summary/,
    );
    // Declining needs the officer's own reason.
    expect(suggestedReply({ type: 'Access', outcome: 'Declined', corrections: [], erasureSummary: null })).toBe('');
    expect(suggestedReply({ type: 'Grievance', outcome: 'Completed', corrections: [], erasureSummary: null })).toBe('');
  });

  it('finds the latest erasure on the request', () => {
    expect(
      latestErasureSummary([
        { kind: 'Received', text: 'Request received at the front desk.' },
        { kind: 'Erased', text: 'Erased: nominee removed.' },
        { kind: 'Note', text: 'Called the patient.' },
        { kind: 'Erased', text: 'Erased: portal account closed.' },
      ]),
    ).toBe('Erased: portal account closed.');
    expect(latestErasureSummary([])).toBeNull();
  });

  it('colours the status, with overdue in red', () => {
    expect(requestStatusTone('Submitted', false)).toBe('warning');
    expect(requestStatusTone('InProgress', true)).toBe('danger');
    expect(requestStatusTone('Completed', true)).toBe('success');
    expect(requestStatusTone('Withdrawn', false)).toBe('informative');
  });
});

describe('correction intake', () => {
  it('offers each detail once', () => {
    const offered = availableCorrectionFields(['name', 'address'], 'address');
    expect(offered).toContain('address');
    expect(offered).not.toContain('name');
    expect(offered).toContain('dateOfBirth');
    expect(canAddCorrection(11)).toBe(true);
    expect(canAddCorrection(12)).toBe(false);
  });

  it('says where each kind of change is made', () => {
    expect(correctionKind('mobile')).toBe('registration');
    expect(correctionKind('healthRecord')).toBe('health');
    expect(correctionKind('other')).toBe('other');
    expect(correctionKind('unknown')).toBe('other');
  });
});

describe('the 72-hour clock', () => {
  const at = (ms: number) => new Date(ms).toISOString();
  const open = { status: 'Open' as const, reportSubmittedAt: null };

  it('counts down to the report', () => {
    expect(breachClock({ ...open, reportDueAt: at(NOW + 5 * HOUR + 20 * 60_000) }, NOW)).toEqual({
      state: 'urgent',
      text: 'Report due in 5 h 20 min',
    });
    expect(breachClock({ ...open, reportDueAt: at(NOW + 50 * HOUR) }, NOW)).toEqual({
      state: 'running',
      text: 'Report due in 2 days 2 h',
    });
  });

  it('says how late the report is', () => {
    expect(breachClock({ ...open, reportDueAt: at(NOW - 3 * HOUR - 10 * 60_000) }, NOW)).toEqual({
      state: 'overdue',
      text: 'Report overdue by 3 h',
    });
    expect(
      breachClock({ status: 'BoardInformed', reportSubmittedAt: null, reportDueAt: at(NOW - 80 * HOUR) }, NOW).text,
    ).toBe('Report overdue by 3 days');
  });

  it('stops once the report is sent or the breach is closed', () => {
    const sent = '2026-10-08T10:00:00Z';
    expect(
      breachClock({ status: 'ReportSubmitted', reportSubmittedAt: sent, reportDueAt: at(NOW - HOUR) }, NOW),
    ).toEqual({
      state: 'sent',
      text: `Report sent on ${formatDateTime(sent)}`,
    });
    expect(breachClock({ status: 'Closed', reportSubmittedAt: null, reportDueAt: at(NOW - HOUR) }, NOW).state).toBe(
      'closed',
    );
    expect(clockTone('overdue')).toBe('danger');
    expect(clockTone('urgent')).toBe('warning');
    expect(breachStatusTone('BoardInformed', true)).toBe('danger');
    expect(breachStatusTone('ReportSubmitted', false)).toBe('success');
  });

  it('gives the list clock in fewer words', () => {
    const sent = { reportDueAt: at(NOW - HOUR), reportSubmittedAt: '2026-10-03T06:00:00Z' };
    expect(breachListClock({ status: 'ReportSubmitted', ...sent }, NOW)).toEqual({
      state: 'sent',
      text: 'Report sent 3 Oct',
    });
    // Closed after the report: still says when it went.
    expect(breachListClock({ status: 'Closed', ...sent }, NOW).text).toBe('Report sent 3 Oct');
    expect(breachListClock({ status: 'Closed', reportDueAt: at(NOW - HOUR), reportSubmittedAt: null }, NOW)).toEqual({
      state: 'closed',
      text: 'Closed without a report',
    });
    expect(breachListClock({ status: 'Open', reportDueAt: at(NOW - HOUR), reportSubmittedAt: null }, NOW).text).toBe(
      'Report overdue by 1 h',
    );
  });

  it('allows an extension only beyond the usual 72 hours', () => {
    expect(extensionMinimum('2026-10-01T04:30:00Z')).toBe('2026-10-04T04:30:00.000Z');
  });
});

describe('breach steps', () => {
  const facts = (patch: Partial<BreachFacts> = {}): BreachFacts => ({
    status: 'Open',
    missingForReport: [],
    missingForNotice: [],
    affectedListed: 0,
    notified: 0,
    ...patch,
  });

  it('asks for the Board to be told before the report', () => {
    const steps = breachSteps(facts());
    expect(steps.boardInformed.visible).toBe(true);
    expect(steps.reportSubmitted.blocker).toMatch(/Board was first told/);
    expect(steps.close.noteRequired).toBe(true);
    expect(nextBreachStep('Open')).toBe('boardInformed');
  });

  it('lists what the report still needs', () => {
    const steps = breachSteps(
      facts({ status: 'BoardInformed', missingForReport: ['the cause', 'what stops it happening again'] }),
    );
    expect(steps.boardInformed.visible).toBe(false);
    expect(steps.reportSubmitted.blocker).toBe('Fill in the cause and what stops it happening again first.');
    expect(breachSteps(facts({ status: 'BoardInformed' })).reportSubmitted.blocker).toBeNull();
    expect(nextBreachStep('BoardInformed')).toBe('reportSubmitted');
  });

  it('closes with or without a note', () => {
    const reported = breachSteps(facts({ status: 'ReportSubmitted' }));
    expect(reported.reportSubmitted.visible).toBe(false);
    expect(reported.extension.visible).toBe(false);
    expect(reported.close).toEqual({ visible: true, blocker: null, noteRequired: false });
    const closed = breachSteps(facts({ status: 'Closed', affectedListed: 5, notified: 2 }));
    expect(closed.close.visible).toBe(false);
    expect(closed.edit.visible).toBe(false);
    // The server refuses notices for a closed breach, so they are not offered.
    expect(closed.notify.visible).toBe(false);
    expect(closed.addPeople.blocker).toBe('A closed breach cannot be changed.');
    expect(nextBreachStep('Closed')).toBeNull();
  });

  it('sends notices only when the notice is complete and someone is waiting for it', () => {
    expect(breachSteps(facts({ missingForNotice: ['the short notice for SMS'] })).notify.blocker).toBe(
      'Fill in the short notice for SMS first.',
    );
    expect(breachSteps(facts()).notify.blocker).toBe('Add the people affected first.');
    expect(breachSteps(facts({ affectedListed: 4, notified: 4 })).notify.blocker).toMatch(/Everyone/);
    const ready = breachSteps(facts({ affectedListed: 10, notified: 4 })).notify;
    expect(ready.blocker).toBeNull();
    expect(ready.pending).toBe(6);
  });

  it('describes the people affected', () => {
    expect(breachPeopleText({ estimatedAffected: 1200, affectedListed: 0, notified: 0 })).toBe(
      'About 1,200, not listed yet',
    );
    expect(breachPeopleText({ estimatedAffected: null, affectedListed: 0, notified: 0 })).toBe('Nobody listed yet');
    expect(breachPeopleText({ estimatedAffected: 5, affectedListed: 120, notified: 118 })).toBe(
      '120 listed · 118 told',
    );
    expect(noticeChannelsText('sms,email')).toBe('SMS and e-mail');
    expect(noticeChannelsText('email')).toBe('E-mail');
    expect(noticeChannelsText('none')).toBe('No contact details');
    expect(noticeChannelsText(null)).toBe('');
  });
});

describe('pasted UHIDs and masked phones', () => {
  it('reads one per line or comma-separated, each once', () => {
    expect(parseUhids(' nh0001\nNH0002, nh0001;NH0003\n\n  ')).toEqual(['NH0001', 'NH0002', 'NH0003']);
    expect(parseUhids('')).toEqual([]);
  });

  it('keeps only the last four digits of a masked number', () => {
    expect(phoneTail('+91******3210')).toBe('3210');
    expect(phoneTail(null)).toBeNull();
    expect(phoneTail('+9')).toBeNull();
  });

  it('joins lists in words', () => {
    expect(listText([])).toBe('');
    expect(listText(['a'])).toBe('a');
    expect(listText(['a', 'b'])).toBe('a and b');
    expect(listText(['a', 'b', 'c'])).toBe('a, b and c');
  });
});

describe('retention', () => {
  const rule = (patch: Partial<RetentionRuleFacts> = {}): RetentionRuleFacts => ({
    key: 'messages.outbox',
    unit: 'Days',
    defaultValue: 30,
    minimumValue: 7,
    maximumValue: 365,
    editable: true,
    ...patch,
  });

  it('says the period in words', () => {
    expect(retentionPeriodText(30, 'Days')).toBe('30 days');
    expect(retentionPeriodText(1, 'Days')).toBe('1 day');
    expect(retentionPeriodText(365, 'Days')).toBe('365 days (1 year)');
    expect(retentionPeriodText(3650, 'Days')).toBe('3650 days (10 years)');
    expect(retentionPeriodText(5, 'Years')).toBe('5 years');
    expect(retentionPeriodText(1, 'Years')).toBe('1 year');
    expect(retentionPeriodText(null, 'Years')).toBe('Permanently');
  });

  it('keeps a change within the allowed range', () => {
    expect(retentionRange(rule())).toEqual({ min: 7, max: 365, text: 'Between 7 and 365 days' });
    expect(retentionRange(rule({ minimumValue: null, unit: 'Years', maximumValue: 99 })).text).toBe(
      'Between 1 and 99 years',
    );
    expect(retentionValueError(rule(), 6)).toBe('Choose between 7 and 365 days.');
    expect(retentionValueError(rule(), 366)).toBe('Choose between 7 and 365 days.');
    expect(retentionValueError(rule(), 7.5)).toBe('Enter a whole number.');
    expect(retentionValueError(rule(), null)).toBe('Enter a whole number.');
    expect(retentionValueError(rule(), 30)).toBeNull();
  });

  it('explains why a period is fixed', () => {
    expect(retentionFixedReason(rule())).toBeNull();
    expect(retentionFixedReason(rule({ key: 'records.mlc', defaultValue: null, editable: false }))).toMatch(
      /permanently/,
    );
    expect(retentionFixedReason(rule({ key: 'backups', editable: false }))).toMatch(/backup settings/);
    expect(retentionFixedReason(rule({ key: 'audit', editable: false }))).toMatch(/cannot be changed/);
    expect(retentionRunSource('Scheduled', 'Meera Iyer')).toBe('Nightly');
    expect(retentionRunSource('Manual', 'Meera Iyer')).toBe('Started by Meera Iyer');
    expect(retentionRunSource('Manual', null)).toBe('Started by an administrator');
    expect(isMedicalRecordsRule('records.inpatient')).toBe(true);
    expect(isMedicalRecordsRule('records.mlc')).toBe(false);
  });

  it('describes holds and record dates', () => {
    expect(keptUntilText('2031-03-12', false)).toBe('Until 12 Mar 2031');
    expect(keptUntilText(null, true)).toBe('Permanently');
    expect(keptUntilText(null, false)).toBe('—');
    expect(itemDatesText(null, null)).toBe('');
    expect(itemDatesText('2026-10-02T05:00:00Z', '2026-10-02T09:00:00Z')).toBe('02 Oct 2026');
    expect(itemDatesText('2021-01-03T05:00:00Z', '2026-10-02T09:00:00Z')).toBe('03 Jan 2021 – 02 Oct 2026');
  });
});

describe('queue and patient links', () => {
  it('says when a request was closed', () => {
    expect(closedText('2026-10-02T09:00:00Z', NOW)).toBe('Closed 2 Oct');
    expect(closedText('2025-12-30T09:00:00Z', NOW)).toBe('Closed 30 Dec 2025');
    expect(closedText(null, NOW)).toBeNull();
  });

  it('offers the officers with the signed-in one first, and keeps the current owner', () => {
    const officers = [
      { id: 'b', displayName: 'Zoya Khan' },
      { id: 'a', displayName: 'Arun Nair' },
      { id: 'me', displayName: 'Meera Iyer' },
    ];
    expect(officerChoices(officers, 'me', null)).toEqual([
      { id: 'me', label: 'Meera Iyer (you)' },
      { id: 'a', label: 'Arun Nair' },
      { id: 'b', label: 'Zoya Khan' },
    ]);
    expect(officerChoices(officers, 'me', { id: 'gone', name: 'Old Owner' }).at(-1)).toEqual({
      id: 'gone',
      label: 'Old Owner (current owner)',
    });
    expect(officerChoices(officers, 'me', { id: 'a', name: 'Arun Nair' })).toHaveLength(3);
  });

  it('says where the nominee came from', () => {
    expect(nomineeSourceText('Portal', '2026-10-01T05:00:00Z')).toBe(
      'Named by the patient in the patient portal, 01 Oct 2026',
    );
    expect(nomineeSourceText('FrontDesk', '2026-10-01T05:00:00Z')).toBe('Recorded at the front desk, 01 Oct 2026');
  });

  it('accepts only record ids from links', () => {
    expect(isRecordId('7c1f0e4e-1a2b-4c3d-8e9f-000000000001')).toBe(true);
    expect(isRecordId('NH0000123')).toBe(false);
    expect(isRecordId(42)).toBe(false);
  });
});
