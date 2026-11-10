import { ApiError } from '@hc360/core';
import { afterEach, describe, expect, it } from 'vitest';
import { createTranslator } from '@/i18n';
import { formattersFor } from '@/i18n/useFormat';
import { NAV_ITEMS, visibleNavItems } from '@/shell/navItems';
import { describeError } from './errors';
import { isPortalFeatureOn, useFeatureRefusals } from './featureSwitches';
import { forgetLinkToken, isWellFormedLinkToken, keepLinkToken, linkTokenFor, TOKEN_PLACEHOLDER } from './linkToken';
import { isGuid } from './ids';
import {
  isValidMobile,
  otpDeadline,
  resendCooldownSeconds,
  sanitizeCode,
  sanitizePhoneInput,
  secondsUntil,
  THROTTLED_COOLDOWN_SECONDS,
  throttledWaitSeconds,
  toE164,
} from './otp';
import {
  clearPendingPayment,
  isTrainingGateway,
  isUsablePaymentLink,
  hasPendingAppointmentPayment,
  peekAnyPendingPayment,
  peekPendingPayment,
  rememberPendingPayment,
} from './payments';
import { ageText, BLOOD_GROUP_LABEL, parseAge, personLine } from './people';
import { otherOwner, resolveActiveProfile } from './profiles';
import { safeRedirect } from './redirect';
import type { PortalProfile } from './types';
import { describeDay, describeWhen } from './when';

const t = createTranslator('en');

describe('sign-in helpers', () => {
  it('keeps the 10 national digits of a mobile number written any common way', () => {
    expect(sanitizePhoneInput('98765 43210')).toBe('9876543210');
    expect(sanitizePhoneInput('+91 98765-43210')).toBe('9876543210');
    expect(sanitizePhoneInput('919876543210')).toBe('9876543210');
    expect(sanitizePhoneInput('09876543210')).toBe('9876543210');
    expect(sanitizePhoneInput('98765432109999')).toBe('9876543210');
  });

  it('accepts only Indian mobile numbers', () => {
    expect(isValidMobile('9876543210')).toBe(true);
    expect(isValidMobile('5876543210')).toBe(false);
    expect(isValidMobile('987654321')).toBe(false);
    expect(toE164('9876543210')).toBe('+919876543210');
  });

  it('takes the code out of whatever was pasted', () => {
    expect(sanitizeCode('Your code is 123456.')).toBe('123456');
    expect(sanitizeCode('12 34 56 78')).toBe('123456');
  });

  it('backs off before sending another code', () => {
    expect([0, 1, 2, 3, 4, 9].map(resendCooldownSeconds)).toEqual([0, 30, 60, 120, 300, 300]);
  });

  it('waits after "too many codes" until the time the server gives, within its 15 minutes', () => {
    expect(throttledWaitSeconds({ retryAfterSeconds: 662 })).toBe(662);
    expect(throttledWaitSeconds({ retryAfterSeconds: 61.2 })).toBe(62);
    expect(throttledWaitSeconds({ retryAfterSeconds: 5000 })).toBe(900);
    for (const data of [undefined, null, {}, { retryAfterSeconds: 0 }, { retryAfterSeconds: 'soon' }]) {
      expect(throttledWaitSeconds(data)).toBe(THROTTLED_COOLDOWN_SECONDS);
    }
  });

  it('trusts the server expiry only when it is plausible on this clock', () => {
    const now = Date.parse('2026-09-28T10:00:00Z');
    expect(otpDeadline('2026-09-28T10:05:00Z', now)).toBe(Date.parse('2026-09-28T10:05:00Z'));
    // The phone's clock is an hour behind: fall back to five minutes from now.
    expect(otpDeadline('2026-09-28T11:05:00Z', now)).toBe(now + 5 * 60_000);
    expect(otpDeadline('2026-09-28T09:00:00Z', now)).toBe(now + 5 * 60_000);
    expect(otpDeadline(null, now)).toBe(now + 5 * 60_000);
    expect(secondsUntil(now + 1500, now)).toBe(2);
    expect(secondsUntil(now - 1, now)).toBe(0);
  });
});

describe('safeRedirect', () => {
  it('keeps same-app paths', () => {
    expect(safeRedirect('/bills/123?show=due')).toBe('/bills/123?show=due');
    expect(safeRedirect('/portal/doctors/abc')).toBe('/doctors/abc');
  });

  it('never leaves the app or loops back to sign-in', () => {
    for (const bad of [
      'https://evil.example',
      '//evil.example',
      '/\\evil.example',
      'javascript:alert(1)',
      'bills',
      '/sign-in',
      '/sign-in?x=1',
      '/portal',
      '/',
      '/a\nb',
      '',
      null,
      undefined,
    ]) {
      expect(safeRedirect(bad), String(bad)).toBe('/home');
    }
  });
});

describe('people', () => {
  it('reads the server age', () => {
    expect(parseAge('34Y 2M')).toEqual({ years: 34, months: 2, days: 0 });
    expect(parseAge('0D')).toEqual({ years: 0, months: 0, days: 0 });
    expect(parseAge('')).toBeNull();
    expect(ageText('34Y 2M', t)).toBe('34 yrs');
    expect(ageText('5M 12D', t)).toBe('5 months');
    expect(ageText('9D', t)).toBe('9 days');
  });

  it('describes a family member', () => {
    expect(personLine({ age: '34Y', gender: 'Female' }, t)).toBe('34 yrs · Female');
    expect(personLine({ age: null, gender: 'Unknown' }, t)).toBe('');
  });

  it('writes blood groups the same way in every language', () => {
    expect(BLOOD_GROUP_LABEL.ONegative).toBe('O−');
    expect(BLOOD_GROUP_LABEL.Unknown).toBeNull();
  });
});

describe('profiles', () => {
  const person = (id: string): PortalProfile => ({
    id,
    uhid: `U-${id}`,
    fullName: `Person ${id}`,
    gender: 'Unknown',
    dateOfBirth: null,
    isDateOfBirthEstimated: false,
    age: null,
    phone: null,
    bloodGroup: 'Unknown',
    category: 'General',
    isVip: false,
    isDeceased: false,
    photoFileId: null,
  });

  it('keeps the chosen family member, or picks the only one', () => {
    const family = [person('a'), person('b')];
    expect(resolveActiveProfile(family, 'b')?.id).toBe('b');
    expect(resolveActiveProfile(family, 'gone')).toBeNull();
    expect(resolveActiveProfile([person('a')], null)?.id).toBe('a');
    expect(resolveActiveProfile([], 'a')).toBeNull();
  });

  it('names the owner of a record that belongs to someone else in the family', () => {
    const family = [person('a'), person('b')];
    expect(otherOwner(family, 'a', 'b')?.id).toBe('b');
    expect(otherOwner(family, 'a', 'a')).toBeNull();
    expect(otherOwner(family, 'a', 'stranger')).toBeNull();
    expect(otherOwner(family, 'a', null)).toBeNull();
  });
});

describe('when', () => {
  const f = formattersFor('en');
  const now = Date.parse('2026-09-28T06:00:00Z'); // 11:30 am IST, Monday

  it('says today and tomorrow in words', () => {
    expect(describeDay('2026-09-28', f, t, now)).toBe('Today');
    expect(describeDay('2026-09-29', f, t, now)).toBe('Tomorrow');
    expect(describeDay('2026-10-02', f, t, now)).toBe('Fri, 2 Oct');
  });

  it('uses the full date outside the coming week', () => {
    expect(describeWhen('2026-09-28T08:30:00Z', f, t, now).toLowerCase()).toMatch(/^today, 2:00\spm$/);
    expect(describeWhen('2026-10-20T08:30:00Z', f, t, now)).toContain('2026');
  });
});

describe('ids', () => {
  it('accepts only GUIDs from the address bar', () => {
    expect(isGuid('3f2504e0-4f89-11d3-9a0c-0305e82c3301')).toBe(true);
    expect(isGuid('3f2504e0')).toBe(false);
    expect(isGuid('../etc/passwd')).toBe(false);
  });
});

describe('link tokens', () => {
  afterEach(() => {
    forgetLinkToken('feedback');
    sessionStorage.clear();
  });

  it('accepts only the server token shape', () => {
    expect(isWellFormedLinkToken('abcDEF0123456789_-')).toBe(true);
    expect(isWellFormedLinkToken('short')).toBe(false);
    expect(isWellFormedLinkToken('has space in the token here')).toBe(false);
    expect(isWellFormedLinkToken('a'.repeat(65))).toBe(false);
  });

  it('keeps the token for the tab once it leaves the address bar', () => {
    const token = 'Zx9_Zx9-Zx9_Zx9-Zx9_Zx';
    expect(keepLinkToken('feedback', token)).toBe(true);
    expect(linkTokenFor('feedback', TOKEN_PLACEHOLDER)).toBe(token);
    expect(sessionStorage.getItem('hc360:portal:link:feedback')).toBe(token);
    forgetLinkToken('feedback');
    expect(linkTokenFor('feedback', TOKEN_PLACEHOLDER)).toBeNull();
  });

  it('never keeps something that is not a token', () => {
    expect(keepLinkToken('feedback', '<script>')).toBe(false);
    expect(linkTokenFor('feedback', '<script>')).toBeNull();
    expect(linkTokenFor('unsubscribe', TOKEN_PLACEHOLDER)).toBeNull();
  });
});

describe('payments', () => {
  afterEach(() => clearPendingPayment());

  it('opens only real https payment pages', () => {
    expect(isUsablePaymentLink('https://rzp.io/i/abc')).toBe(true);
    expect(isUsablePaymentLink('http://rzp.io/i/abc')).toBe(false);
    expect(isUsablePaymentLink('https://pay.simulator.invalid/x')).toBe(false);
    expect(isUsablePaymentLink('javascript:alert(1)')).toBe(false);
    expect(isUsablePaymentLink(null)).toBe(false);
  });

  it('recognises the training gateway', () => {
    expect(isTrainingGateway({ provider: 'Simulator' })).toBe(true);
    expect(isTrainingGateway({ provider: 'razorpay' })).toBe(false);
  });

  it('remembers a hosted-page payment for its own bill only, until cleared', () => {
    rememberPendingPayment({ kind: 'bill', billId: 'bill-1', orderRowId: 'order-1' });
    expect(peekPendingPayment('bill-2')).toBeNull();
    expect(peekPendingPayment('bill-1')).toEqual({ kind: 'bill', billId: 'bill-1', orderRowId: 'order-1' });
    // Reading does not consume it (safe to read twice, as React may).
    expect(peekPendingPayment('bill-1')).not.toBeNull();
    clearPendingPayment();
    expect(peekPendingPayment('bill-1')).toBeNull();
  });

  it('remembers a hosted-page payment for its own appointment only, never as a bill', () => {
    rememberPendingPayment({ kind: 'appointment', appointmentId: 'appt-1' });
    expect(hasPendingAppointmentPayment('appt-2')).toBe(false);
    expect(hasPendingAppointmentPayment('appt-1')).toBe(true);
    expect(peekPendingPayment('appt-1')).toBeNull();
    clearPendingPayment();
    expect(hasPendingAppointmentPayment('appt-1')).toBe(false);
  });

  it('tells the return page what this tab was paying for', () => {
    expect(peekAnyPendingPayment()).toBeNull();
    rememberPendingPayment({ kind: 'appointment', appointmentId: 'appt-1' });
    expect(peekAnyPendingPayment()).toEqual({ kind: 'appointment', appointmentId: 'appt-1' });
    rememberPendingPayment({ kind: 'bill', billId: 'bill-1', orderRowId: 'order-1' });
    expect(peekAnyPendingPayment()).toEqual({ kind: 'bill', billId: 'bill-1', orderRowId: 'order-1' });
  });

  it('does not take a bill payment for an appointment one', () => {
    rememberPendingPayment({ kind: 'bill', billId: 'appt-1', orderRowId: 'order-1' });
    expect(hasPendingAppointmentPayment('appt-1')).toBe(false);
  });
});

describe('describeError', () => {
  it('explains transport problems in plain words', () => {
    expect(describeError(new ApiError({ status: 0, title: 'x' }), t).message).toBe(t('errors.network'));
    expect(describeError(new ApiError({ status: 429, title: 'x' }), t).message).toBe(t('errors.tooMany'));
    expect(describeError(new ApiError({ status: 503, title: 'x' }), t).retryable).toBe(true);
    expect(describeError(new Error('boom'), t).message).toBe(t('errors.generic'));
  });

  it('shows a business rule as the server wrote it', () => {
    const error = new ApiError({ status: 422, title: 'Rule', detail: 'This slot was just taken.' });
    expect(describeError(error, t)).toMatchObject({ message: 'This slot was just taken.', retryable: false });
  });
});

describe('feature switches', () => {
  afterEach(() => useFeatureRefusals.getState().clear());

  it('counts a feature as on unless the server says it is off', () => {
    const on = { features: { patientPortal: true, telehealth: true } };
    expect(isPortalFeatureOn(on, {}, 'patientPortal')).toBe(true);
    expect(isPortalFeatureOn({ features: { patientPortal: false, telehealth: true } }, {}, 'patientPortal')).toBe(
      false,
    );
    expect(isPortalFeatureOn({ features: { patientPortal: true, telehealth: false } }, {}, 'telehealth')).toBe(false);
    // An older server, or the answer still loading.
    expect(isPortalFeatureOn({}, {}, 'telehealth')).toBe(true);
    expect(isPortalFeatureOn(undefined, {}, 'patientPortal')).toBe(true);
    // Refused since the page loaded, whatever the server said before.
    expect(isPortalFeatureOn(on, { telehealth: true }, 'telehealth')).toBe(false);
    expect(isPortalFeatureOn(on, { telehealth: true }, 'patientPortal')).toBe(true);
  });

  it('remembers which feature the server refused; an answer that names none is the portal', () => {
    useFeatureRefusals.getState().refuse('telehealth');
    expect(useFeatureRefusals.getState().refused).toEqual({ telehealth: true });
    useFeatureRefusals.getState().refuse(undefined);
    expect(useFeatureRefusals.getState().refused).toEqual({ telehealth: true, patientPortal: true });
    useFeatureRefusals.getState().clear();
    expect(useFeatureRefusals.getState().refused).toEqual({});
  });

  it('leaves video consultations out of the menu while they are off', () => {
    const withoutVideo = visibleNavItems((feature) => feature !== 'telehealth');
    expect(withoutVideo.map((item) => item.to)).not.toContain('/video');
    expect(withoutVideo).toHaveLength(NAV_ITEMS.length - 1);
    expect(visibleNavItems(() => true)).toEqual(NAV_ITEMS);
  });
});
