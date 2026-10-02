import { ApiError } from '@hc360/core';
import type { PatientBannerData } from '@hc360/ui';
import { describe, expect, it } from 'vitest';
import type { AlertSeverity, PatientFlags } from './api';
import {
  alertBadge,
  allergyText,
  flagFields,
  flagsView,
  mergeAlerts,
  NO_RECORD,
  unavailableNote,
  withFlags,
} from './flags';

const patient: PatientBannerData = { id: 'p1', uhid: 'NH0001', fullName: 'Asha Rao' };

const flags: PatientFlags = {
  allergyStatus: 'Recorded',
  allergies: [
    { id: 'a1', allergen: 'Penicillin', type: 'Drug', severity: 'Anaphylaxis', reaction: 'Throat swelling' },
    { id: 'a2', allergen: 'Peanuts', type: 'Food', severity: 'Moderate', reaction: null },
  ],
  alerts: [
    {
      id: 'x1',
      type: 'InfectionControl',
      title: 'Infection control',
      detail: 'MRSA',
      severity: 'Critical',
      expiresOn: null,
    },
    { id: 'x2', type: 'FallRisk', title: 'Fall risk', detail: null, severity: 'Warning', expiresOn: null },
  ],
};

describe('banner flag text', () => {
  it('names the severity of the reactions that matter most', () => {
    expect(allergyText({ allergen: 'Penicillin', severity: 'Anaphylaxis' })).toBe('Penicillin (anaphylaxis)');
    expect(allergyText({ allergen: 'Sulfa drugs', severity: 'Severe' })).toBe('Sulfa drugs (severe)');
    expect(allergyText({ allergen: 'Peanuts', severity: 'Moderate' })).toBe('Peanuts');
  });

  it('tones alerts by severity and adds their detail', () => {
    expect(alertBadge({ title: 'Infection control', detail: 'MRSA', severity: 'Critical' })).toEqual({
      label: 'Infection control: MRSA',
      tone: 'danger',
    });
    expect(alertBadge({ title: 'Fall risk', detail: null, severity: 'Warning' })).toEqual({
      label: 'Fall risk',
      tone: 'warning',
    });
    expect(alertBadge({ title: 'Diabetic', detail: null, severity: 'Info' }).tone).toBe('informative');
  });

  it('never plays down an alert of an unknown severity', () => {
    expect(alertBadge({ title: 'New kind', detail: null, severity: 'Urgent' as AlertSeverity }).tone).toBe('warning');
  });

  it('fills the banner fields in the server order (most severe first)', () => {
    expect(flagFields(flags)).toEqual({
      allergies: ['Penicillin (anaphylaxis)', 'Peanuts'],
      allergyStatus: 'recorded',
      alerts: [
        { label: 'Infection control: MRSA', tone: 'danger' },
        { label: 'Fall risk', tone: 'warning' },
      ],
    });
  });

  it('claims "no known allergies" only when it was recorded', () => {
    expect(flagFields({ ...flags, allergyStatus: 'NoKnownAllergies', allergies: [] }).allergyStatus).toBe('none');
    expect(flagFields(NO_RECORD).allergyStatus).toBe('unknown');
  });
});

describe('screen banner with flags', () => {
  it('shows identity only to staff who may not see clinical flags', () => {
    const own = { ...patient, alerts: ['Emergency blood request'] };
    expect(withFlags(own, { kind: 'hidden' })).toBe(own);
  });

  it('says it is checking while the flags load, keeping the screen alerts', () => {
    expect(withFlags({ ...patient, alerts: ['MLC'] }, { kind: 'loading' })).toEqual({
      ...patient,
      alerts: ['MLC'],
      allergyStatus: 'checking',
    });
  });

  it('says why the flags are missing instead of showing nothing', () => {
    const restricted = new ApiError({ status: 403, title: 'Forbidden', code: 'patient.restricted' });
    expect(withFlags(patient, { kind: 'failed', error: restricted })).toMatchObject({
      allergyStatus: 'unavailable',
      allergyNote: 'Allergies and alerts hidden: restricted record',
    });
    expect(withFlags(patient, { kind: 'failed', error: new TypeError('Failed to fetch') })).toMatchObject({
      allergyStatus: 'unavailable',
      allergyNote: 'Allergies and alerts could not be loaded',
    });
  });

  it('puts the clinical alerts before the screen alerts', () => {
    const merged = withFlags(
      { ...patient, alerts: [{ label: 'Medicine overdue', tone: 'danger' }, 'Emergency surgery'] },
      { kind: 'ready', flags },
    );
    expect(merged.allergies).toEqual(['Penicillin (anaphylaxis)', 'Peanuts']);
    expect(merged.alerts).toEqual([
      { label: 'Infection control: MRSA', tone: 'danger' },
      { label: 'Fall risk', tone: 'warning' },
      { label: 'Medicine overdue', tone: 'danger' },
      'Emergency surgery',
    ]);
  });
});

describe('flags on their own (dialogs and counters)', () => {
  it('shows nothing to staff who may not see clinical flags', () => {
    expect(flagsView({ kind: 'hidden' })).toBeNull();
  });

  it('says what it is doing and what it found', () => {
    expect(flagsView({ kind: 'loading' })).toEqual({ allergyStatus: 'checking' });
    expect(flagsView({ kind: 'ready', flags: NO_RECORD })).toEqual({
      allergies: [],
      allergyStatus: 'unknown',
      alerts: [],
    });
    expect(flagsView({ kind: 'failed', error: new TypeError('Failed to fetch') })).toEqual({
      allergyStatus: 'unavailable',
      allergyNote: 'Allergies and alerts could not be loaded',
    });
  });
});

describe('alert merging', () => {
  it('shows a flag once when the chart and the ward both raise it', () => {
    expect(
      mergeAlerts([{ label: 'Fall risk', tone: 'warning' }], [{ label: 'fall risk ', tone: 'danger' }, 'MLC']),
    ).toEqual([{ label: 'Fall risk', tone: 'warning' }, 'MLC']);
  });

  it('keeps distinct alerts in order', () => {
    expect(mergeAlerts([], ['A', 'B'])).toEqual(['A', 'B']);
    expect(mergeAlerts(['A'], [])).toEqual(['A']);
  });
});

describe('unavailable note', () => {
  it('tells a restricted record from a failed load', () => {
    expect(unavailableNote(new ApiError({ status: 403, title: 'Forbidden', code: 'patient.restricted' }))).toBe(
      'Allergies and alerts hidden: restricted record',
    );
    expect(unavailableNote(new ApiError({ status: 500, title: 'Server error', code: 'server' }))).toBe(
      'Allergies and alerts could not be loaded',
    );
  });
});
