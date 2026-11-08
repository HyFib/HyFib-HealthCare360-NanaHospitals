import { describe, expect, it } from 'vitest';
import {
  appendFinding,
  applyTemplate,
  formatConfidence,
  isReportable,
  isReportEditable,
  isReportSigned,
  nextStudyAction,
  pregnancyScanPerformed,
  studyActions,
} from './workflow';

describe('imaging study actions', () => {
  it('offers only what the state machine allows', () => {
    expect(studyActions('Ordered')).toEqual(['schedule', 'arrive', 'start', 'cancel', 'reconcile']);
    expect(studyActions('InProgress')).toContain('acquire');
    expect(studyActions('InProgress')).toContain('repeat');
    expect(studyActions('Acquired')).not.toContain('start');
    expect(studyActions('Reported')).not.toContain('cancel');
    expect(studyActions('Cancelled')).toEqual([]);
    // A pregnancy ultrasound once scanned is not cancelled: its Form F is completed (slice 9).
    expect(studyActions('InProgress', true)).not.toContain('cancel');
    expect(studyActions('Acquired', true)).toEqual(['report', 'repeat', 'reconcile']);
    expect(pregnancyScanPerformed({ requiresFormF: true, startedAt: null, acquiredAt: null })).toBe(false);
    expect(pregnancyScanPerformed({ requiresFormF: true, startedAt: '2026-11-08T05:00:00Z', acquiredAt: null })).toBe(
      true,
    );
    expect(pregnancyScanPerformed({ requiresFormF: false, startedAt: '2026-11-08T05:00:00Z', acquiredAt: null })).toBe(
      false,
    );
  });

  it('suggests the next step', () => {
    expect(nextStudyAction('Scheduled')).toBe('arrive');
    expect(nextStudyAction('Arrived')).toBe('start');
    expect(nextStudyAction('InProgress')).toBe('acquire');
    expect(nextStudyAction('Acquired')).toBeNull();
  });

  it('knows when a report can be written and edited', () => {
    expect(isReportable('Arrived')).toBe(false);
    expect(isReportable('Acquired')).toBe(true);
    expect(isReportEditable(null)).toBe(true);
    expect(isReportEditable('Reported')).toBe(true);
    expect(isReportEditable('Verified')).toBe(false);
    expect(isReportSigned('Released')).toBe(true);
    expect(isReportSigned('Draft')).toBe(false);
  });
});

describe('report helpers', () => {
  it('fills empty fields from a template and appends to typed text', () => {
    const template = { technique: 'PA view.', findings: 'Lungs are clear.', impression: 'Normal study.' };
    expect(applyTemplate({ technique: '', findings: '', impression: '' }, template)).toEqual({
      technique: 'PA view.',
      findings: 'Lungs are clear.',
      impression: 'Normal study.',
    });
    expect(
      applyTemplate({ technique: '', findings: 'Cardiomegaly.', impression: '' }, { ...template, technique: null })
        .findings,
    ).toBe('Cardiomegaly.\n\nLungs are clear.');
  });

  it('adds an AI finding once', () => {
    expect(appendFinding('', 'Right lower lobe opacity.')).toBe('Right lower lobe opacity.');
    expect(appendFinding('Heart normal.', 'Right lower lobe opacity.')).toBe(
      'Heart normal.\n\nRight lower lobe opacity.',
    );
    expect(appendFinding('Heart normal.\n\nRight lower lobe opacity.', 'Right lower lobe opacity.')).toBe(
      'Heart normal.\n\nRight lower lobe opacity.',
    );
  });

  it('formats AI confidence', () => {
    expect(formatConfidence(0.873)).toBe('87%');
    expect(formatConfidence(1.2)).toBe('100%');
    expect(formatConfidence(null)).toBeNull();
  });
});
