import { describe, expect, it } from 'vitest';
import {
  AI_SETTINGS,
  buildSecretChanges,
  buildSettingChanges,
  checkTerminals,
  checkWorklistModalities,
  defaultScope,
  fieldId,
  INTEGRATION_SETTINGS,
  isFeatureSettingKey,
  isFieldVisible,
  isIpAddress,
  isLoopbackHost,
  isValidSettingKey,
  parseFieldValue,
  replaceFlagId,
  scopedSettings,
  secretFieldId,
  settingWarning,
  toFormValues,
  urlProblem,
  validateSettings,
  viewerLinkProblem,
  type FormValues,
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
    const pos = INTEGRATION_SETTINGS.pos as SettingsSpec;
    const stored = { 'integrations.pos': { enabled: true, provider: 'simulator', terminals: [{ id: 'OPD-1' }] } };
    const values = { ...toFormValues(pos, stored), [id('integrations.pos', 'terminals')]: '[ broken' };
    expect(buildSettingChanges(pos, values, stored)).toEqual([]);
  });

  it('leaves analyser connections to Lab setup instead of a JSON list', () => {
    expect(lab.fields.some((field) => field.key === 'integrations.lab.instruments')).toBe(false);
    expect(lab.intro).toContain('Lab setup');
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

    const terminals = INTEGRATION_SETTINGS.pos!.fields.find((field) => field.path === 'terminals')!;
    expect(parseFieldValue(terminals, '{"id":"OPD-1"}')).toMatchObject({
      ok: false,
      error: expect.stringContaining('square brackets'),
    });
    expect(parseFieldValue(terminals, '[{"id":"OPD-1","name":"OPD counter"}]')).toMatchObject({ ok: true });
  });

  it('keeps the space in an API key prefix', () => {
    const prefix = sms.fields.find((field) => field.path === 'generic.apiKeyPrefix')!;
    expect(parseFieldValue(prefix, 'Bearer ')).toEqual({ ok: true, value: 'Bearer ' });
  });

  it('checks POS terminals', () => {
    expect(checkTerminals([{ id: 'OPD-1' }, { id: 'IPD_2', name: 'IP counter' }])).toBeNull();
    expect(checkTerminals([{ id: 'OPD 1' }])).toContain('Terminal 1');
    expect(checkTerminals([{ id: 'OPD-1' }, { id: 'OPD-1' }])).toContain('Two terminals');
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

  it('warns that Windows Hello unlock is only for computers where each person has their own Windows login', () => {
    const warning = settingWarning('ui.biometricUnlock');
    expect(warning).toMatch(/their own account/);
    expect(warning).toMatch(/share one Windows\s+login/);
    expect(settingWarning('ui.idleLockMinutes')).toBeNull();
  });
});

describe('AI settings', () => {
  const provider = id('ai.provider');
  const baseUrl = id('ai.openai', 'baseUrl');
  const model = id('ai.openai', 'model');
  const field = (key: string, path?: string) =>
    AI_SETTINGS.fields.find((item) => item.key === key && item.path === path)!;
  const visible = (values: FormValues) =>
    AI_SETTINGS.fields.filter((item) => isFieldVisible(item, values)).map(fieldId);
  const withServer = { ...toFormValues(AI_SETTINGS, {}), [provider]: 'openai' };

  it('offers Anthropic, the default, an OpenAI-compatible server or off, as the server does', () => {
    expect(field('ai.provider').options?.map((option) => option.value)).toEqual(['anthropic', 'openai', 'disabled']);
    expect(toFormValues(AI_SETTINGS, {})[provider]).toBe('anthropic');
    expect(toFormValues(AI_SETTINGS, { 'ai.provider': 'OpenAI' })[provider]).toBe('openai');
  });

  it('shows only the chosen provider’s settings', () => {
    const anthropic = visible(toFormValues(AI_SETTINGS, {}));
    expect(anthropic).toEqual(expect.arrayContaining([id('ai.model'), id('ai.timeoutSeconds')]));
    expect(anthropic).not.toContain(baseUrl);

    const server = visible(withServer);
    expect(server).toEqual(expect.arrayContaining([baseUrl, model, id('ai.openai', 'timeoutSeconds')]));
    expect(server).not.toContain(id('ai.model'));
    expect(server).not.toContain(id('ai.timeoutSeconds'));
  });

  it('needs the server’s address and model once it is chosen', () => {
    const errors = validateSettings(AI_SETTINGS, withServer);
    expect(errors[baseUrl]).toBe('Fill this in.');
    expect(errors[model]).toBe('Fill this in.');
    expect(
      validateSettings(AI_SETTINGS, {
        ...withServer,
        [baseUrl]: 'https://llm.hospital.lan/v1',
        [model]: 'local-model',
      }),
    ).toEqual({});
  });

  it.each([
    'https://llm.hospital.lan/v1',
    'https://res.openai.azure.com/openai/v1',
    'http://localhost:8000/v1',
    'http://127.0.0.1:11434/v1',
    'http://[::1]:8080/v1',
  ])('accepts the server address %s, as the server does', (address) => {
    expect(parseFieldValue(field('ai.openai', 'baseUrl'), address)).toEqual({ ok: true, value: address });
  });

  it.each([
    ['http://llm.hospital.lan/v1', 'https://'],
    ['http://192.168.1.20:8000/v1', 'http://localhost'],
    ['https://user:secret@llm.hospital.lan/v1', 'user name or password'],
    ['https://llm.hospital.lan/v1?api-key=secret', '? or #'],
    ['https://llm.hospital.lan/v1#models', '? or #'],
    ['llm.hospital.lan/v1', 'https://'],
  ])('refuses the server address %s, as the server does', (address, problem) => {
    expect(parseFieldValue(field('ai.openai', 'baseUrl'), address)).toMatchObject({
      ok: false,
      error: expect.stringContaining(problem),
    });
  });

  it.each(['local-model', 'Org/Model-7B-Instruct', 'model:8b', 'ward_notes.v2'])(
    'accepts the model name %s',
    (name) => {
      expect(parseFieldValue(field('ai.openai', 'model'), ` ${name} `)).toEqual({ ok: true, value: name });
    },
  );

  it.each(['two words', '-leading-dash', 'a'.repeat(129)])('refuses the model name %j', (name) => {
    expect(parseFieldValue(field('ai.openai', 'model'), name)).toMatchObject({ ok: false });
  });

  it('saves the server as one ai.openai setting, and its key as a secret of its own', () => {
    const values = {
      ...withServer,
      [id('ai.enabled')]: true,
      [baseUrl]: 'http://localhost:8000/v1',
      [model]: 'local-model',
      [id('ai.openai', 'timeoutSeconds')]: 240,
      [replaceFlagId('ai.openai.secret')]: true,
      [secretFieldId({ key: 'ai.openai.secret', prop: 'apiKey' })]: ' server-key ',
    };
    expect(buildSettingChanges(AI_SETTINGS, values, {})).toEqual([
      { key: 'ai.enabled', value: true },
      { key: 'ai.provider', value: 'openai' },
      { key: 'ai.openai', value: { baseUrl: 'http://localhost:8000/v1', model: 'local-model', timeoutSeconds: 240 } },
    ]);
    expect(buildSecretChanges(AI_SETTINGS, values)).toEqual([
      { key: 'ai.openai.secret', value: { apiKey: 'server-key' } },
    ]);
  });

  it('allows plain http only to the computer that makes the call', () => {
    expect(['localhost', 'LOCALHOST', '127.0.0.1', '127.10.20.30', '[::1]'].every(isLoopbackHost)).toBe(true);
    expect(['localhost.example.com', '10.0.0.5', '1270.0.0.1', 'llm.hospital.lan'].some(isLoopbackHost)).toBe(false);
    expect(urlProblem('http://localhost:8000/v1', true)).toContain('https');
    expect(urlProblem('http://localhost:8000/v1', true, true)).toBeNull();
  });

  it('lets the speech server run on the HC360 server itself too', () => {
    const url = field('ai.speech', 'url');
    expect(parseFieldValue(url, 'http://localhost:8000/v1/audio/transcriptions')).toMatchObject({ ok: true });
    expect(parseFieldValue(url, 'http://stt.lan:8000/v1/audio/transcriptions')).toMatchObject({ ok: false });
  });
});

describe('modality worklist and PACS settings', () => {
  const mwl = INTEGRATION_SETTINGS.mwl as SettingsSpec;
  const ct = { aeTitle: 'CT1', name: 'CT scanner', allowedIps: ['10.0.5.31'], facility: 'NH', modalities: ['CT'] };

  it('is one form for all facilities, off until turned on', () => {
    expect(mwl.globalOnly).toBe(true);
    expect(mwl.prefix).toBe('integrations.mwl');
    expect(mwl.secrets).toEqual([]);
    expect(toFormValues(mwl, {})[id('integrations.mwl', 'enabled')]).toBe(false);
  });

  it('accepts modalities with their address and kinds of study', () => {
    expect(
      checkWorklistModalities([
        ct,
        { aeTitle: 'DR_ROOM 1', allowedIps: ['10.0.5.40', 'fe80::1'], modalities: ['dx', 'XR'] },
      ]),
    ).toBeNull();
  });

  it('says what a modality entry lacks', () => {
    expect(checkWorklistModalities([{ ...ct, aeTitle: 'THIS_TITLE_IS_TOO_LONG' }])).toContain('aeTitle');
    expect(checkWorklistModalities([{ ...ct, allowedIps: [] }])).toContain('allowedIps');
    expect(checkWorklistModalities([{ ...ct, allowedIps: ['scanner.local'] }])).toContain('allowedIps');
    expect(checkWorklistModalities([{ ...ct, modalities: ['NM'] }])).toContain('CR or DX');
    expect(checkWorklistModalities([{ ...ct, facility: 7 }])).toContain('facility code');
    expect(checkWorklistModalities([ct, { ...ct, aeTitle: 'ct1' }])).toContain('Two modalities');
  });

  it('checks the listen address and the server AE title', () => {
    expect(isIpAddress('0.0.0.0')).toBe(true);
    expect(isIpAddress('10.0.5.256')).toBe(false);
    expect(isIpAddress('::')).toBe(true);
    expect(isIpAddress('hc360-srv')).toBe(false);
    const values = { ...toFormValues(mwl, {}), [id('integrations.mwl', 'aeTitle')]: 'HC360 MWL\\X' };
    expect(validateSettings(mwl, values)[id('integrations.mwl', 'aeTitle')]).toContain('AE title');
  });

  it('wants a viewer link that names the study', () => {
    expect(viewerLinkProblem('https://pacs.local/ohif/viewer?StudyInstanceUIDs={studyUid}')).toBeNull();
    expect(viewerLinkProblem('https://pacs.local/view?acc={accession}')).toBeNull();
    expect(viewerLinkProblem('https://pacs.local/home')).toContain('{studyUid} or {accession}');
    expect(viewerLinkProblem('javascript:alert(1)//{studyUid}')).toContain('http');
  });

  it('keeps the image and sending addresses with the DICOMweb settings', () => {
    const pacs = INTEGRATION_SETTINGS.pacs as SettingsSpec;
    const wado = pacs.fields.find((field) => field.path === 'wadoUrl');
    expect(wado?.kind).toBe('url');
    expect(parseFieldValue(wado!, 'http://pacs.local/wado-rs')).toEqual({
      ok: true,
      value: 'http://pacs.local/wado-rs',
    });
    const stow = pacs.fields.find((field) => field.path === 'stowUrl');
    expect(stow?.kind).toBe('url');
    expect(stow?.when).toBeDefined();
    expect(parseFieldValue(stow!, 'http://dcm4chee:8080/dcm4chee-arc/aets/DCM4CHEE/rs')).toEqual({
      ok: true,
      value: 'http://dcm4chee:8080/dcm4chee-arc/aets/DCM4CHEE/rs',
    });
  });
});

describe('Aadhaar authentication settings', () => {
  const aadhaar = INTEGRATION_SETTINGS.aadhaar as SettingsSpec;
  const uidai = (patch: Record<string, string>) => ({
    ...toFormValues(aadhaar, {}),
    [id('integrations.aadhaar', 'enabled')]: true,
    [id('integrations.aadhaar', 'provider')]: 'uidai',
    ...Object.fromEntries(Object.entries(patch).map(([path, value]) => [id('integrations.aadhaar', path), value])),
  });

  it('is off until turned on, and keeps the licence keys and the signing certificate secret', () => {
    expect(toFormValues(aadhaar, {})[id('integrations.aadhaar', 'enabled')]).toBe(false);
    expect(aadhaar.secrets.map((secret) => secret.prop)).toEqual([
      'licenceKey',
      'asaLicenceKey',
      'signingCertificate',
      'signingCertificatePassword',
    ]);
    expect(aadhaar.secrets.every((secret) => secret.key === 'integrations.aadhaar.secret')).toBe(true);
  });

  it('wants the ASA’s https address and both AUA codes for UIDAI, as the server does', () => {
    const problems = validateSettings(aadhaar, uidai({}));
    expect(problems[id('integrations.aadhaar', 'authUrl')]).toBeDefined();
    expect(problems[id('integrations.aadhaar', 'auaCode')]).toBeDefined();
    expect(problems[id('integrations.aadhaar', 'subAuaCode')]).toBeDefined();

    const plain = validateSettings(
      aadhaar,
      uidai({
        authUrl: 'http://asa.example.in/2.5/{ac}/{uid0}/{uid1}/{asalk}',
        auaCode: 'HC00 01',
        subAuaCode: 'NANA01',
      }),
    );
    expect(plain[id('integrations.aadhaar', 'authUrl')]).toBeDefined();
    expect(plain[id('integrations.aadhaar', 'auaCode')]).toBe('Up to 10 letters or digits, as the ASA gives it.');

    const ready = validateSettings(
      aadhaar,
      uidai({
        authUrl: 'https://asa.example.in/2.5/{ac}/{uid0}/{uid1}/{asalk}',
        auaCode: 'HC0000001',
        subAuaCode: 'NANA01',
      }),
    );
    expect(ready).toEqual({});
  });

  it('asks the readers only for what UIDAI takes', () => {
    const environment = aadhaar.fields.find((field) => field.path === 'environment')!;
    expect(environment.options?.map((option) => option.value)).toEqual(['P', 'PP', 'S']);
    const data = aadhaar.fields.find((field) => field.path === 'fingerData')!;
    expect(data.options?.map((option) => option.value)).toEqual(['FMR,FIR', 'FMR', 'FIR']);
    const fingers = aadhaar.fields.find((field) => field.path === 'fingerCount')!;
    expect(parseFieldValue(fingers, '3').ok).toBe(false);
    const wait = aadhaar.fields.find((field) => field.path === 'captureTimeoutSeconds')!;
    expect(parseFieldValue(wait, '90').ok).toBe(false);
    const tid = aadhaar.fields.find((field) => field.path === 'registeredDeviceTid')!;
    expect(parseFieldValue(tid, 'registered').ok).toBe(true);
    expect(parseFieldValue(tid, 'reg-1').ok).toBe(false);
  });

  it('takes UIDAI’s certificate only as a whole PEM certificate', () => {
    const certificate = aadhaar.fields.find((field) => field.path === 'uidaiCertificate')!;
    expect(parseFieldValue(certificate, '-----BEGIN CERTIFICATE-----\nMIIB\n-----END CERTIFICATE-----\n').ok).toBe(
      true,
    );
    expect(parseFieldValue(certificate, 'MIIB').ok).toBe(false);
  });
});
