import { describe, expect, it } from 'vitest';
import {
  AI_SETTINGS,
  buildSecretChanges,
  buildSettingChanges,
  checkInstruments,
  checkTerminals,
  defaultScope,
  fieldId,
  INTEGRATION_SETTINGS,
  isFeatureSettingKey,
  isValidSettingKey,
  parseFieldValue,
  replaceFlagId,
  scopedSettings,
  secretFieldId,
  toFormValues,
  urlProblem,
  validateSettings,
  type SettingRow,
  type SettingsSpec,
} from './settingsForms';

const sms = INTEGRATION_SETTINGS.sms as SettingsSpec;
const email = INTEGRATION_SETTINGS.email as SettingsSpec;
const lab = INTEGRATION_SETTINGS.lab as SettingsSpec;
const FACILITY = '11111111-2222-4333-8444-555555555555';

const id = (key: string, path?: string) => fieldId({ key, path });

const storedSms = {
  'integrations.sms': {
    enabled: true,
    provider: 'msg91',
    senderId: 'NANAHS',
    dltEntityId: '1201159123456789012',
    msg91: {
      baseUrl: 'https://control.msg91.com',
      templateIds: { '1107160000000000001': '64f0c0ffee' },
      shortUrl: true,
    },
    webhookAllowedIps: ['10.0.0.5', '10.0.0.6'],
    somethingNewer: { keep: 'me' },
  },
};

describe('form ids', () => {
  it('are flat and unique per setting, secret and replace switch', () => {
    expect(id('integrations.sms', 'msg91.baseUrl')).toBe('integrations_sms_msg91_baseUrl');
    expect(id('ai.enabled')).toBe('ai_enabled');
    expect(secretFieldId({ key: 'integrations.sms.secret', prop: 'msg91AuthKey' })).toBe(
      'secret__integrations_sms_secret__msg91AuthKey',
    );
    expect(replaceFlagId('integrations.sms.secret')).toBe('replace__integrations_sms_secret');
    for (const spec of [...Object.values(INTEGRATION_SETTINGS), AI_SETTINGS]) {
      const ids = spec!.fields.map(fieldId);
      expect(new Set(ids).size).toBe(ids.length);
    }
  });
});

describe('stored settings → form', () => {
  it('shows stored values, maps as name = value lines and lists one per line', () => {
    const values = toFormValues(sms, storedSms);
    expect(values[id('integrations.sms', 'provider')]).toBe('msg91');
    expect(values[id('integrations.sms', 'msg91.templateIds')]).toBe('1107160000000000001 = 64f0c0ffee');
    expect(values[id('integrations.sms', 'webhookAllowedIps')]).toBe('10.0.0.5\n10.0.0.6');
    expect(values[id('integrations.sms', 'msg91.shortUrl')]).toBe(true);
    expect(values[id('integrations.sms', 'twilio.accountSid')]).toBe('');
  });

  it('starts secrets empty and Replace off: stored credentials are never shown', () => {
    const values = toFormValues(sms, storedSms);
    expect(values[secretFieldId({ key: 'integrations.sms.secret', prop: 'msg91AuthKey' })]).toBe('');
    expect(values[replaceFlagId('integrations.sms.secret')]).toBe(false);
  });

  it('shows the server defaults of switches and selects when nothing is stored', () => {
    const values = toFormValues(email, {});
    expect(values[id('integrations.email', 'useStartTls')]).toBe(true);
    expect(values[id('integrations.email', 'enabled')]).toBe(false);
    expect(values[id('integrations.email', 'port')]).toBeNull();
    expect(toFormValues(AI_SETTINGS, {})[id('ai.speech', 'provider')]).toBe('none');
  });

  it('matches a provider regardless of case', () => {
    expect(toFormValues(sms, { 'integrations.sms': { provider: 'MSG91' } })[id('integrations.sms', 'provider')]).toBe(
      'msg91',
    );
  });
});

describe('form → settings to save', () => {
  it('saves nothing when nothing changed, even with keys the form does not know', () => {
    expect(buildSettingChanges(sms, toFormValues(sms, storedSms), storedSms)).toEqual([]);
    expect(buildSettingChanges(email, toFormValues(email, {}), {})).toEqual([]);
    expect(buildSettingChanges(AI_SETTINGS, toFormValues(AI_SETTINGS, {}), {})).toEqual([]);
  });

  it('merges changes into the stored object and keeps unknown keys', () => {
    const values = {
      ...toFormValues(sms, storedSms),
      [id('integrations.sms', 'provider')]: 'twilio',
      [id('integrations.sms', 'twilio.accountSid')]: 'AC0123456789abcdef0123456789abcdef',
      [id('integrations.sms', 'senderId')]: '',
    };
    const [change, ...rest] = buildSettingChanges(sms, values, storedSms);
    expect(rest).toEqual([]);
    expect(change?.key).toBe('integrations.sms');
    expect(change?.value).toEqual({
      enabled: true,
      provider: 'twilio',
      dltEntityId: '1201159123456789012',
      msg91: {
        baseUrl: 'https://control.msg91.com',
        templateIds: { '1107160000000000001': '64f0c0ffee' },
        shortUrl: true,
      },
      twilio: { accountSid: 'AC0123456789abcdef0123456789abcdef' },
      webhookAllowedIps: ['10.0.0.5', '10.0.0.6'],
      somethingNewer: { keep: 'me' },
    });
  });

  it('writes an explicit change away from a default, but not the default itself', () => {
    const off = {
      ...toFormValues(email, {}),
      [id('integrations.email', 'useStartTls')]: false,
      [id('integrations.email', 'port')]: 25,
    };
    expect(buildSettingChanges(email, off, {})).toEqual([
      { key: 'integrations.email', value: { useStartTls: false, port: 25 } },
    ]);
  });

  it('writes plain AI settings as plain values, and clears them with null', () => {
    const on = { ...toFormValues(AI_SETTINGS, {}), [id('ai.enabled')]: true, [id('ai.limits', 'perUserPerHour')]: 30 };
    expect(buildSettingChanges(AI_SETTINGS, on, {})).toEqual([
      { key: 'ai.enabled', value: true },
      { key: 'ai.limits', value: { perUserPerHour: 30 } },
    ]);
    const stored = { 'ai.model': 'claude-opus-5-5' };
    const cleared = { ...toFormValues(AI_SETTINGS, stored), [id('ai.model')]: '' };
    expect(buildSettingChanges(AI_SETTINGS, cleared, stored)).toEqual([{ key: 'ai.model', value: null }]);
  });

  it('keeps a stored value it cannot parse instead of dropping it', () => {
    const stored = { 'integrations.lab.instruments': [{ code: 'XN550', port: 5100 }] };
    const values = { ...toFormValues(lab, stored), [id('integrations.lab.instruments')]: '[ broken' };
    expect(buildSettingChanges(lab, values, stored)).toEqual([]);
  });
});

describe('checks', () => {
  it('accepts only safe addresses', () => {
    expect(urlProblem('https://control.msg91.com', true)).toBeNull();
    expect(urlProblem('http://192.168.1.20:9000', false)).toBeNull();
    expect(urlProblem('http://api.example.com', true)).toContain('https');
    expect(urlProblem('https://user:pass@api.example.com', true)).toContain('user name or password');
    expect(urlProblem('ftp://files.example.com', false)).toContain('http');
    expect(urlProblem('control.msg91.com', true)).toContain('https://');
  });

  it('parses numbers, lists, maps and JSON with plain messages', () => {
    const port = email.fields.find((field) => field.path === 'port')!;
    expect(parseFieldValue(port, 587)).toEqual({ ok: true, value: 587 });
    expect(parseFieldValue(port, 465)).toMatchObject({ ok: false, error: expect.stringContaining('587') });
    expect(parseFieldValue(port, 70_000)).toMatchObject({ ok: false });
    expect(parseFieldValue(port, null)).toEqual({ ok: true, value: undefined });

    const codes = INTEGRATION_SETTINGS.pos!.fields.find((field) => field.path === 'pendingResponseCodes')!;
    expect(parseFieldValue(codes, '1001\n1002, 1003')).toEqual({ ok: true, value: [1001, 1002, 1003] });
    expect(parseFieldValue(codes, '1001\nabc')).toMatchObject({ ok: false });

    const templates = sms.fields.find((field) => field.path === 'msg91.templateIds')!;
    expect(parseFieldValue(templates, 'a = b\n c=d ')).toEqual({ ok: true, value: { a: 'b', c: 'd' } });
    expect(parseFieldValue(templates, 'just text')).toMatchObject({
      ok: false,
      error: expect.stringContaining('name = value'),
    });

    const instruments = lab.fields.find((field) => field.key === 'integrations.lab.instruments')!;
    expect(parseFieldValue(instruments, '{"code":"A"}')).toMatchObject({
      ok: false,
      error: expect.stringContaining('square brackets'),
    });
    expect(parseFieldValue(instruments, '[{"code":"XN550","protocol":"Hl7v2","port":5100}]')).toMatchObject({
      ok: true,
    });
  });

  it('keeps the space in an API key prefix', () => {
    const prefix = sms.fields.find((field) => field.path === 'generic.apiKeyPrefix')!;
    expect(parseFieldValue(prefix, 'Bearer ')).toEqual({ ok: true, value: 'Bearer ' });
  });

  it('checks POS terminals and analysers', () => {
    expect(checkTerminals([{ id: 'OPD-1' }, { id: 'IPD_2', name: 'IP counter' }])).toBeNull();
    expect(checkTerminals([{ id: 'OPD 1' }])).toContain('Terminal 1');
    expect(checkTerminals([{ id: 'OPD-1' }, { id: 'OPD-1' }])).toContain('Two terminals');
    expect(
      checkInstruments([
        { code: 'XN550', protocol: 'Hl7v2', port: 5100 },
        { code: 'C311', protocol: 'astm', port: 5101 },
      ]),
    ).toBeNull();
    expect(checkInstruments([{ code: 'XN550', port: 0 }])).toContain('port');
    expect(checkInstruments([{ code: 'XN550', port: 5100, protocol: 'FTP' }])).toContain('Hl7v2 or Astm');
    expect(
      checkInstruments([
        { code: 'A', port: 5100 },
        { code: 'B', port: 5100 },
      ]),
    ).toContain('port 5100');
  });

  it('checks only the fields on screen, and needs a provider when switched on', () => {
    const values = {
      ...toFormValues(sms, {}),
      [id('integrations.sms', 'enabled')]: true,
      [id('integrations.sms', 'twilio.baseUrl')]: 'not a url',
    };
    const errors = validateSettings(sms, values);
    expect(errors[id('integrations.sms', 'provider')]).toBe('Choose one.');
    // Twilio settings are hidden while no Twilio provider is chosen.
    expect(errors[id('integrations.sms', 'twilio.baseUrl')]).toBeUndefined();
    expect(
      validateSettings(sms, { ...values, [id('integrations.sms', 'provider')]: 'twilio' })[
        id('integrations.sms', 'twilio.baseUrl')
      ],
    ).toBeDefined();
  });
});

describe('secrets', () => {
  const key = 'integrations.sms.secret';

  it('replaces a secret key only when Replace is on, with what was typed', () => {
    const values = {
      ...toFormValues(sms, {}),
      [replaceFlagId(key)]: true,
      [secretFieldId({ key, prop: 'msg91AuthKey' })]: '  auth-123  ',
      [secretFieldId({ key, prop: 'webhookToken' })]: 'token-9',
    };
    expect(buildSecretChanges(sms, values)).toEqual([
      { key, value: { msg91AuthKey: 'auth-123', webhookToken: 'token-9' } },
    ]);
    expect(buildSecretChanges(sms, { ...values, [replaceFlagId(key)]: false })).toEqual([]);
  });

  it('asks for at least one credential when replacing', () => {
    const values = { ...toFormValues(sms, {}), [replaceFlagId(key)]: true };
    expect(validateSettings(sms, values)[secretFieldId({ key, prop: 'msg91AuthKey' })]).toContain(
      'at least one credential',
    );
  });
});

describe('scope', () => {
  const rows: SettingRow[] = [
    { key: 'integrations.sms', facilityId: null, value: { enabled: true, provider: 'msg91' }, hasValue: true },
    { key: 'integrations.sms.secret', facilityId: null, value: null, hasValue: true },
    { key: 'integrations.email', facilityId: FACILITY, value: { enabled: true, provider: 'smtp' }, hasValue: true },
  ];

  it('reads all-facility rows, or a facility’s own rows falling back to them', () => {
    expect(scopedSettings(rows, sms, 'global', FACILITY)).toEqual({
      stored: { 'integrations.sms': { enabled: true, provider: 'msg91' } },
      inherited: [],
      secretStatus: { 'integrations.sms.secret': 'set' },
    });
    expect(scopedSettings(rows, sms, 'facility', FACILITY)).toEqual({
      stored: { 'integrations.sms': { enabled: true, provider: 'msg91' } },
      inherited: ['integrations.sms'],
      secretStatus: { 'integrations.sms.secret': 'inherited' },
    });
    expect(scopedSettings(rows, email, 'global', FACILITY).stored).toEqual({});
    expect(scopedSettings(rows, email, 'facility', FACILITY).secretStatus).toEqual({
      'integrations.email.secret': 'not-set',
    });
  });

  it('opens on the facility only when it has its own settings', () => {
    expect(defaultScope(rows, sms, FACILITY)).toBe('global');
    expect(defaultScope(rows, email, FACILITY)).toBe('facility');
    expect(defaultScope(rows, email, null)).toBe('global');
    expect(
      defaultScope([{ key: 'integrations.lab', facilityId: FACILITY, value: {}, hasValue: true }], lab, FACILITY),
    ).toBe('global');
  });
});

describe('setting names', () => {
  it.each(['ui.idleLockMinutes', 'pharmacy.discount.max_percent', 'integrations.sms-backup', 'ai.enabled', 'portal'])(
    'accepts %s, as the server does',
    (key) => {
      expect(isValidSettingKey(key)).toBe(true);
    },
  );

  it.each(['', 'ui idle', 'ui/idle', 'ui.idle:x', 'राज्य.name', 'a'.repeat(129)])('refuses %j', (key) => {
    expect(isValidSettingKey(key)).toBe(false);
  });

  it('knows a feature switch by its prefix', () => {
    expect(isFeatureSettingKey('features.telehealth')).toBe(true);
    expect(isFeatureSettingKey('telehealth.earlyJoinMinutes')).toBe(false);
  });
});
