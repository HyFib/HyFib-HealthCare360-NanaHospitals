import { describe, expect, it } from 'vitest';
import {
  EMPTY_TEST_FORM,
  formatDuration,
  healthDisplay,
  INTEGRATION_NAMES,
  isIsoDay,
  istDayEnd,
  istDayStart,
  keywordActionDisplay,
  pageFromUrl,
  providerLabel,
  TEST_DESCRIPTIONS,
  testFormProblems,
  testFormSchema,
  testInputFor,
  toTestRequest,
  transactionStatusDisplay,
} from './integrationsApi';

describe('dashboard display', () => {
  it('describes every health word in plain language', () => {
    expect(healthDisplay('healthy')).toMatchObject({ label: 'Working', tone: 'success' });
    expect(healthDisplay('degraded')).toMatchObject({ label: 'Some failures', tone: 'warning' });
    expect(healthDisplay('failing')).toMatchObject({ label: 'Failing', tone: 'danger' });
    expect(healthDisplay('idle').label).toBe('No activity yet');
    expect(healthDisplay('disabled').label).toBe('Turned off');
    expect(healthDisplay('not_configured').label).toBe('Not set up');
    expect(healthDisplay('mystery')).toMatchObject({ label: 'mystery', tone: 'informative' });
  });

  it('names providers, including the simulator', () => {
    expect(providerLabel('simulator')).toBe('Simulator (training)');
    expect(providerLabel('MSG91')).toBe('MSG91');
    expect(providerLabel('zpl/tspl')).toBe('ZPL / TSPL printers');
    expect(providerLabel('zkteco')).toBe('ZKTeco / eSSL terminals');
    expect(providerLabel('push')).toBe('Terminals send their punches');
    expect(providerLabel(null)).toBe('—');
    expect(providerLabel('newprovider')).toBe('newprovider');
  });

  it('explains every test before it runs', () => {
    for (const name of INTEGRATION_NAMES) {
      expect(TEST_DESCRIPTIONS[name].length).toBeGreaterThan(20);
    }
  });

  it('shows simulated payments and durations plainly', () => {
    expect(transactionStatusDisplay('Captured')).toEqual({ label: 'Paid', tone: 'success' });
    expect(transactionStatusDisplay('AwaitingCustomer').tone).toBe('warning');
    expect(formatDuration(850)).toBe('850 ms');
    expect(formatDuration(1234)).toBe('1.2 s');
    expect(formatDuration(null)).toBe('—');
  });
});

describe('WhatsApp inbox', () => {
  it('says what a STOP or START did', () => {
    expect(keywordActionDisplay('OptOut', 'OptedOut')).toMatchObject({ label: 'Messages stopped', tone: 'danger' });
    expect(keywordActionDisplay('OptOut', 'OptedOut')?.detail).toContain('except sign-in codes');
    expect(keywordActionDisplay('OptIn', 'OptedIn')).toMatchObject({ label: 'Messages resumed', tone: 'success' });
    expect(keywordActionDisplay('OptOut', 'Superseded')).toMatchObject({ label: 'No change', tone: 'subtle' });
    expect(keywordActionDisplay('OptOut', 'None')?.label).toBe('Not applied');
    expect(keywordActionDisplay('OptIn', null)?.label).toBe('Waiting');
  });

  it('shows nothing for ordinary messages', () => {
    expect(keywordActionDisplay('None', 'None')).toBeNull();
    expect(keywordActionDisplay('None', null)).toBeNull();
  });
});

describe('log filters', () => {
  it('accepts only real dates and page numbers from the URL', () => {
    expect(isIsoDay('2026-09-28')).toBe(true);
    expect(isIsoDay('2026-02-30')).toBe(false);
    expect(isIsoDay('28-09-2026')).toBe(false);
    expect(isIsoDay(20260928)).toBe(false);
    expect(pageFromUrl(3)).toBe(3);
    expect(pageFromUrl(1)).toBeUndefined();
    expect(pageFromUrl('3')).toBeUndefined();
    expect(pageFromUrl(2.5)).toBeUndefined();
  });

  it('turns IST calendar days into the server’s from/to range (to is exclusive)', () => {
    expect(istDayStart('2026-09-28')).toBe('2026-09-28T00:00:00+05:30');
    expect(istDayEnd('2026-09-28')).toBe('2026-09-29T00:00:00+05:30');
    expect(istDayEnd('2026-12-31')).toBe('2027-01-01T00:00:00+05:30');
  });
});

describe('send a test', () => {
  it('asks for a recipient only where one is needed', () => {
    expect(testInputFor('sms')).toBe('mobile');
    expect(testInputFor('whatsapp')).toBe('mobile');
    expect(testInputFor('email')).toBe('email');
    expect(testInputFor('tally')).toBeNull();
    expect(testFormProblems('sms', EMPTY_TEST_FORM).to).toContain('mobile number');
    expect(testFormProblems('email', { ...EMPTY_TEST_FORM, to: 'not-an-address' }).to).toContain('e-mail');
    expect(testFormProblems('payment', EMPTY_TEST_FORM)).toEqual({});
  });

  it('sends only the fields that apply, with the mobile number in international form', () => {
    const values = {
      to: '98765 43210',
      message: ' Hello ',
      dltTemplateId: '1107160000000000001',
      templateName: 'hello_world',
      languageCode: 'en_US',
    };
    expect(toTestRequest('sms', values)).toEqual({
      to: '+919876543210',
      message: 'Hello',
      dltTemplateId: '1107160000000000001',
      templateName: null,
      languageCode: null,
    });
    expect(toTestRequest('whatsapp', values)).toEqual({
      to: '+919876543210',
      message: null,
      dltTemplateId: null,
      templateName: 'hello_world',
      languageCode: 'en_US',
    });
    expect(toTestRequest('email', { ...values, to: ' admin@nanahospitals.in ' }).to).toBe('admin@nanahospitals.in');
    expect(toTestRequest('payment', values)).toEqual({
      to: null,
      message: null,
      dltTemplateId: null,
      templateName: null,
      languageCode: null,
    });
  });

  it('checks DLT ids, template names and language codes like the server', () => {
    expect(testFormSchema.safeParse({ ...EMPTY_TEST_FORM, dltTemplateId: '12345' }).success).toBe(false);
    expect(testFormSchema.safeParse({ ...EMPTY_TEST_FORM, templateName: 'Hello World' }).success).toBe(false);
    expect(testFormSchema.safeParse({ ...EMPTY_TEST_FORM, languageCode: 'english' }).success).toBe(false);
    expect(
      testFormSchema.safeParse({
        ...EMPTY_TEST_FORM,
        dltTemplateId: '1107160000000000001',
        templateName: 'hello_world',
        languageCode: 'en_US',
      }).success,
    ).toBe(true);
  });
});
