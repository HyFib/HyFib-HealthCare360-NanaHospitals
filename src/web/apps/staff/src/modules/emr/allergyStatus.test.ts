import { ApiError } from '@hc360/core';
import { describe, expect, it } from 'vitest';
import type { AllergyRecordingStatus } from './api';
import {
  bannerAllergyStatus,
  canRecordNoKnownAllergies,
  flagsSummaryNote,
  noKnownAllergiesRefusal,
  recordedByText,
} from './allergyStatus';

describe('banner allergy flag', () => {
  it('maps the chart status to the banner flag', () => {
    expect(bannerAllergyStatus('Recorded')).toBe('recorded');
    expect(bannerAllergyStatus('NoKnownAllergies')).toBe('none');
    expect(bannerAllergyStatus('NotRecorded')).toBe('unknown');
  });

  it('never claims "no known allergies" for a missing or unknown status', () => {
    expect(bannerAllergyStatus(undefined as unknown as AllergyRecordingStatus)).toBe('unknown');
    expect(bannerAllergyStatus('Something' as AllergyRecordingStatus)).toBe('unknown');
  });
});

describe('chart summary note', () => {
  it('says what is known when no allergy is listed', () => {
    expect(flagsSummaryNote('NoKnownAllergies', 0, 0)).toBe('No active alerts. No known allergies.');
    expect(flagsSummaryNote('NotRecorded', 0, 0)).toBe('No active alerts. Allergies have not been recorded yet.');
  });

  it('still states the allergy status next to active alerts', () => {
    expect(flagsSummaryNote('NotRecorded', 2, 0)).toBe('Allergies have not been recorded yet.');
    expect(flagsSummaryNote('NoKnownAllergies', 1, 0)).toBe('No known allergies.');
  });

  it('adds nothing while allergies are listed', () => {
    expect(flagsSummaryNote('Recorded', 0, 2)).toBeNull();
    expect(flagsSummaryNote('Recorded', 1, 0)).toBeNull();
  });
});

describe('"No known allergies" action', () => {
  it('is offered only to allergy managers while nothing is recorded', () => {
    expect(canRecordNoKnownAllergies('NotRecorded', true, 0)).toBe(true);
    expect(canRecordNoKnownAllergies('NotRecorded', false, 0)).toBe(false);
    expect(canRecordNoKnownAllergies('Recorded', true, 0)).toBe(false);
    expect(canRecordNoKnownAllergies('NoKnownAllergies', true, 0)).toBe(false);
    expect(canRecordNoKnownAllergies(undefined, true, 0)).toBe(false);
  });

  it('is never offered next to a listed active allergy', () => {
    expect(canRecordNoKnownAllergies('NotRecorded', true, 1)).toBe(false);
  });

  it('names who recorded the status and when', () => {
    expect(
      recordedByText(
        { recordedAt: '2026-09-28T05:00:00Z', recordedByName: 'Dr. Asha Rao' },
        () => '28 Sept 2026, 10:30 am',
      ),
    ).toBe('Recorded by Dr. Asha Rao on 28 Sept 2026, 10:30 am.');
    expect(recordedByText({ recordedAt: null, recordedByName: '  ' })).toBe('Recorded by a clinician.');
  });

  it('explains the refusal while active allergies are on the chart', () => {
    const refusal = (data: unknown) =>
      noKnownAllergiesRefusal(new ApiError({ status: 409, title: 'Conflict', code: 'allergy.active_exist', data }));
    expect(refusal({ activeAllergies: 1 })).toBe(
      'This patient has an active allergy on the chart, so "No known allergies" was not recorded. If it was recorded by mistake or no longer applies, open it and mark it entered in error, inactive or resolved first.',
    );
    expect(refusal({ activeAllergies: 3 })).toContain('has 3 active allergies on the chart');
    expect(refusal({ activeAllergies: 3 })).toContain('open them and mark them');
    expect(refusal(undefined)).toContain('has active allergies on the chart');
  });

  it('leaves other errors to the usual message', () => {
    expect(noKnownAllergiesRefusal(new ApiError({ status: 409, title: 'Conflict', code: 'concurrency' }))).toBeNull();
    expect(noKnownAllergiesRefusal(new Error('offline'))).toBeNull();
  });
});
