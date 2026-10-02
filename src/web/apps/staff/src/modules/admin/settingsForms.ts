import type { SettingDto } from './api';
import type { IntegrationName } from './integrationsApi';

/**
 * Structured forms over the platform settings API (`/api/v1/admin/settings`, permission `admin.settings.manage`).
 *
 * * Non-secret settings are JSON (`integrations.sms` is one object, `ai.enabled` a plain value). The forms below
 *   mirror the server's settings records field by field; keys the form does not know are kept as they are.
 * * Secrets live in separate encrypted keys (`integrations.sms.secret`, `ai.anthropic.secret`) holding one JSON
 *   object. The server never returns them and says only whether the key has a value, so a secret key can only be
 *   replaced as a whole: the form shows "Set" / "Not set" and a Replace switch, never the values.
 * * Rows can apply to all facilities or to one facility (which overrides). Background work uses the global rows.
 *
 * Everything here is pure and unit-tested; `SettingsForm.tsx` renders it.
 */

export type FieldKind =
  'text' | 'url' | 'email' | 'number' | 'switch' | 'select' | 'textarea' | 'list' | 'map' | 'json';

export interface FieldCondition {
  /** Id of the controlling field (see `fieldId`). */
  field: string;
  values: readonly string[];
}

export interface SettingField {
  /** Setting key, e.g. "integrations.sms" or "ai.enabled". */
  key: string;
  /** Dotted path inside the key's JSON object; absent for a plain value. */
  path?: string;
  label: string;
  kind: FieldKind;
  hint?: string;
  placeholder?: string;
  /** Switches and selects: the server's default, shown when nothing is stored and not written unless changed. */
  defaultValue?: string | boolean;
  /** Numbers: the server's default (shown in the hint; empty means default). */
  defaultNumber?: number;
  options?: readonly { value: string; label: string }[];
  min?: number;
  max?: number;
  /** URLs: internet services must use https. */
  https?: boolean;
  /** Lists: whole numbers. */
  numeric?: boolean;
  /** JSON: the expected shape. */
  shape?: 'array' | 'object';
  /** Keep spaces ("Bearer "). */
  keepSpaces?: boolean;
  maxLength?: number;
  pattern?: { regex: RegExp; message: string };
  /** Extra check of the parsed value. */
  check?: (value: unknown) => string | null;
  /** Shown only when another field has one of these values. */
  when?: FieldCondition;
  /** Required when another field has one of these values. */
  requiredWhen?: FieldCondition;
  /** Heading above this field. */
  group?: string;
}

export interface SecretField {
  /** Encrypted setting key, e.g. "integrations.sms.secret". */
  key: string;
  /** Property inside the secret's JSON object. */
  prop: string;
  label: string;
  hint?: string;
}

export interface SettingsSpec {
  id: string;
  title: string;
  intro: string;
  /** Prefix covering every key of the form, for `GET /api/v1/admin/settings?prefix=`. */
  prefix: string;
  fields: readonly SettingField[];
  secrets: readonly SecretField[];
  /** Stored for all facilities only (read without a facility on the server). */
  globalOnly?: boolean;
  /** Background sending (reminders, reports, the Tally day book) uses the settings for all facilities. */
  backgroundUsesGlobal?: boolean;
}

export type FormValue = string | number | boolean | null;
export type FormValues = Record<string, FormValue>;
export type Scope = 'global' | 'facility';
export type SecretStatus = 'set' | 'inherited' | 'not-set';
export type SettingRow = Pick<SettingDto, 'key' | 'facilityId' | 'value' | 'hasValue'>;

// ---------------------------------------------------------------------------------------------------- keys

/**
 * A setting name the server accepts (`PUT /api/v1/admin/settings/{key}`): 1 to 128 letters, digits, dots, dashes or
 * underscores, such as `ui.idleLockMinutes` or `pharmacy.discount.max_percent`.
 */
export function isValidSettingKey(key: string): boolean {
  return /^[A-Za-z0-9._-]{1,128}$/.test(key);
}

/** A feature switch: changed on Hospital setup > Features, which explains what each one turns off. */
export function isFeatureSettingKey(key: string): boolean {
  return key.startsWith('features.');
}

// ---------------------------------------------------------------------------------------------------- ids

const safeId = (text: string) => text.replace(/[^A-Za-z0-9]/g, '_');

export function fieldId(field: { key: string; path?: string }): string {
  return safeId(field.path ? `${field.key}.${field.path}` : field.key);
}

export function secretFieldId(secret: { key: string; prop: string }): string {
  return `secret__${safeId(secret.key)}__${safeId(secret.prop)}`;
}

export function replaceFlagId(secretKey: string): string {
  return `replace__${safeId(secretKey)}`;
}

export function specKeys(spec: SettingsSpec): string[] {
  return [...new Set(spec.fields.map((field) => field.key))];
}

export function specSecretKeys(spec: SettingsSpec): string[] {
  return [...new Set(spec.secrets.map((secret) => secret.key))];
}

// ---------------------------------------------------------------------------------------------------- JSON helpers

export function isPlainObject(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function getPath(root: unknown, path: string): unknown {
  let current: unknown = root;
  for (const part of path.split('.')) {
    if (!isPlainObject(current)) {
      return undefined;
    }
    current = current[part];
  }
  return current;
}

function setPath(root: Record<string, unknown>, path: string, value: unknown): void {
  const parts = path.split('.');
  let current = root;
  for (const part of parts.slice(0, -1)) {
    const next = current[part];
    if (!isPlainObject(next)) {
      current[part] = {};
    }
    current = current[part] as Record<string, unknown>;
  }
  current[parts[parts.length - 1] as string] = value;
}

function deletePath(root: Record<string, unknown>, path: string): void {
  const parts = path.split('.');
  const parent = parts.length > 1 ? getPath(root, parts.slice(0, -1).join('.')) : root;
  if (isPlainObject(parent)) {
    delete parent[parts[parts.length - 1] as string];
  }
}

function cloneJson<T>(value: T): T {
  return value === undefined ? value : (JSON.parse(JSON.stringify(value)) as T);
}

function canonical(value: unknown): string {
  if (value === undefined || value === null) {
    return 'null';
  }
  if (Array.isArray(value)) {
    return `[${value.map(canonical).join(',')}]`;
  }
  if (isPlainObject(value)) {
    return `{${Object.keys(value)
      .filter((key) => value[key] !== undefined)
      .sort()
      .map((key) => `${JSON.stringify(key)}:${canonical(value[key])}`)
      .join(',')}}`;
  }
  return JSON.stringify(value);
}

export function sameJson(a: unknown, b: unknown): boolean {
  return canonical(a) === canonical(b);
}

// ---------------------------------------------------------------------------------------------------- stored ↔ form

function readField(stored: Record<string, unknown>, field: SettingField): unknown {
  const root = stored[field.key];
  return field.path ? getPath(root, field.path) : root;
}

function toFieldValue(field: SettingField, raw: unknown): FormValue {
  switch (field.kind) {
    case 'switch':
      return typeof raw === 'boolean' ? raw : typeof field.defaultValue === 'boolean' ? field.defaultValue : false;
    case 'number':
      if (typeof raw === 'number' && Number.isFinite(raw)) {
        return raw;
      }
      return typeof raw === 'string' && raw.trim() && Number.isFinite(Number(raw)) ? Number(raw) : null;
    case 'list':
      return Array.isArray(raw)
        ? raw
            .filter((item) => item !== null && item !== undefined)
            .map(String)
            .join('\n')
        : typeof raw === 'string'
          ? raw
          : '';
    case 'map':
      return isPlainObject(raw)
        ? Object.entries(raw)
            .map(([name, value]) => `${name} = ${typeof value === 'string' ? value : JSON.stringify(value)}`)
            .join('\n')
        : '';
    case 'json':
      return raw === undefined || raw === null ? '' : JSON.stringify(raw, null, 2);
    case 'select': {
      const text = typeof raw === 'string' ? raw.trim() : '';
      const option = field.options?.find((item) => item.value.toLowerCase() === text.toLowerCase());
      return option?.value ?? (text || (typeof field.defaultValue === 'string' ? field.defaultValue : ''));
    }
    default:
      if (typeof raw === 'string') {
        return raw;
      }
      return raw === undefined || raw === null ? '' : typeof raw === 'object' ? JSON.stringify(raw) : String(raw);
  }
}

/** Form values for the stored settings of one scope. Secret inputs always start empty. */
export function toFormValues(spec: SettingsSpec, stored: Record<string, unknown>): FormValues {
  const values: FormValues = {};
  for (const field of spec.fields) {
    values[fieldId(field)] = toFieldValue(field, readField(stored, field));
  }
  for (const secret of spec.secrets) {
    values[secretFieldId(secret)] = '';
  }
  for (const key of specSecretKeys(spec)) {
    values[replaceFlagId(key)] = false;
  }
  return values;
}

type Parsed = { ok: true; value: unknown } | { ok: false; error: string };

const ok = (value: unknown): Parsed => ({ ok: true, value });
const fail = (error: string): Parsed => ({ ok: false, error });
const EMAIL = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;

/** Why an address cannot be used, or null: absolute http(s), no credentials in it, https for internet services. */
export function urlProblem(text: string, httpsOnly: boolean): string | null {
  let url: URL;
  try {
    url = new URL(text);
  } catch {
    return httpsOnly
      ? 'Enter a full address starting with https://'
      : 'Enter a full address starting with http:// or https://';
  }
  if (url.protocol !== 'https:' && url.protocol !== 'http:') {
    return 'Use an http:// or https:// address.';
  }
  if (url.username || url.password) {
    return 'Do not put a user name or password in the address. Use the credentials section.';
  }
  if (httpsOnly && url.protocol !== 'https:') {
    return 'Internet services must use a secure https:// address.';
  }
  return null;
}

/** Parses one form value; `value: undefined` means "not set" (the server default applies). */
export function parseFieldValue(field: SettingField, raw: FormValue | undefined): Parsed {
  switch (field.kind) {
    case 'switch':
      return ok(Boolean(raw));

    case 'number': {
      if (raw === null || raw === undefined || raw === '') {
        return ok(undefined);
      }
      const number = typeof raw === 'number' ? raw : Number(raw);
      if (!Number.isInteger(number)) {
        return fail('Enter a whole number.');
      }
      if ((field.min !== undefined && number < field.min) || (field.max !== undefined && number > field.max)) {
        return fail(`Enter a number from ${field.min ?? 0} to ${field.max ?? 'any'}.`);
      }
      const problem = field.check?.(number);
      return problem ? fail(problem) : ok(number);
    }

    case 'list': {
      const items = String(raw ?? '')
        .split(/[\n,]/)
        .map((item) => item.trim())
        .filter(Boolean);
      if (items.length === 0) {
        return ok(undefined);
      }
      if (field.numeric) {
        const numbers = items.map(Number);
        return numbers.some((number) => !Number.isInteger(number))
          ? fail('Use whole numbers, one per line.')
          : ok(numbers);
      }
      return ok(items);
    }

    case 'map': {
      const lines = String(raw ?? '')
        .split('\n')
        .map((line) => line.trim())
        .filter(Boolean);
      if (lines.length === 0) {
        return ok(undefined);
      }
      const map: Record<string, string> = {};
      for (const line of lines) {
        const at = line.indexOf('=');
        const name = at > 0 ? line.slice(0, at).trim() : '';
        const value = at > 0 ? line.slice(at + 1).trim() : '';
        if (!name || !value) {
          return fail(`Write one per line as name = value. Check “${line.slice(0, 40)}”.`);
        }
        map[name] = value;
      }
      return ok(map);
    }

    case 'json': {
      const text = String(raw ?? '').trim();
      if (!text) {
        return ok(undefined);
      }
      let parsed: unknown;
      try {
        parsed = JSON.parse(text);
      } catch {
        return fail('This is not valid JSON. Check the brackets, quotes and commas.');
      }
      if (field.shape === 'array' && !Array.isArray(parsed)) {
        return fail('Enter a list in square brackets: [ … ].');
      }
      if (field.shape === 'object' && !isPlainObject(parsed)) {
        return fail('Enter an object in curly brackets: { … }.');
      }
      const problem = field.check?.(parsed);
      return problem ? fail(problem) : ok(parsed);
    }

    default: {
      const text =
        typeof raw === 'string'
          ? field.keepSpaces
            ? raw
            : raw.trim()
          : raw === null || raw === undefined
            ? ''
            : String(raw);
      if (!text.trim()) {
        return ok(undefined);
      }
      if (field.maxLength !== undefined && text.length > field.maxLength) {
        return fail(`Keep it to ${field.maxLength} characters.`);
      }
      if (field.kind === 'url') {
        const problem = urlProblem(text, field.https ?? false);
        if (problem) {
          return fail(problem);
        }
      }
      if (field.kind === 'email' && !EMAIL.test(text)) {
        return fail('Enter a valid e-mail address.');
      }
      if (field.kind === 'select' && field.options && !field.options.some((option) => option.value === text)) {
        return fail('Choose one of the options.');
      }
      if (field.pattern && !field.pattern.regex.test(text)) {
        return fail(field.pattern.message);
      }
      const problem = field.check?.(text);
      return problem ? fail(problem) : ok(text);
    }
  }
}

function conditionMet(condition: FieldCondition, values: FormValues): boolean {
  return condition.values.includes(String(values[condition.field] ?? ''));
}

export function isFieldVisible(field: SettingField, values: FormValues): boolean {
  return !field.when || conditionMet(field.when, values);
}

function isBlank(value: FormValue | undefined): boolean {
  return value === null || value === undefined || (typeof value === 'string' && !value.trim());
}

/** Problems keyed by form field id (only fields on screen are checked). */
export function validateSettings(spec: SettingsSpec, values: FormValues): Record<string, string> {
  const errors: Record<string, string> = {};
  for (const field of spec.fields) {
    if (!isFieldVisible(field, values)) {
      continue;
    }
    const id = fieldId(field);
    const parsed = parseFieldValue(field, values[id]);
    if (!parsed.ok) {
      errors[id] = parsed.error;
    } else if (field.requiredWhen && conditionMet(field.requiredWhen, values) && isBlank(values[id])) {
      errors[id] = field.kind === 'select' ? 'Choose one.' : 'Fill this in.';
    }
  }
  for (const key of specSecretKeys(spec)) {
    if (values[replaceFlagId(key)] !== true) {
      continue;
    }
    const fields = spec.secrets.filter((secret) => secret.key === key);
    const [first] = fields;
    if (first && fields.every((secret) => isBlank(values[secretFieldId(secret)]))) {
      errors[secretFieldId(first)] = 'Enter at least one credential, or turn off Replace.';
    }
  }
  return errors;
}

/**
 * The settings to save: for each key of the form, the stored JSON with the form's fields applied, only when it
 * changed. Fields that are hidden or cannot be parsed keep their stored value; server defaults that were never stored
 * are not written.
 */
export function buildSettingChanges(
  spec: SettingsSpec,
  values: FormValues,
  stored: Record<string, unknown>,
): { key: string; value: unknown }[] {
  const next = new Map<string, unknown>(specKeys(spec).map((key) => [key, cloneJson(stored[key])]));
  for (const field of spec.fields) {
    const parsed = parseFieldValue(field, values[fieldId(field)]);
    if (!parsed.ok) {
      continue;
    }
    const before = readField(stored, field);
    let value = parsed.value;
    if (before === undefined && value === (field.defaultValue ?? (field.kind === 'switch' ? false : undefined))) {
      value = undefined;
    }

    if (!field.path) {
      if (value !== undefined || before !== undefined) {
        next.set(field.key, value ?? null);
      }
      continue;
    }

    const root = next.get(field.key);
    if (value === undefined) {
      if (isPlainObject(root)) {
        deletePath(root, field.path);
      }
      continue;
    }
    const target = isPlainObject(root) ? root : {};
    setPath(target, field.path, value);
    next.set(field.key, target);
  }
  return specKeys(spec)
    .filter((key) => !sameJson(next.get(key), stored[key]))
    .map((key) => ({ key, value: next.get(key) ?? null }));
}

/** Secrets to replace: each secret key whose Replace switch is on, with the credentials that were typed. */
export function buildSecretChanges(
  spec: SettingsSpec,
  values: FormValues,
): { key: string; value: Record<string, string> }[] {
  return specSecretKeys(spec)
    .filter((key) => values[replaceFlagId(key)] === true)
    .map((key) => {
      const value: Record<string, string> = {};
      for (const secret of spec.secrets.filter((item) => item.key === key)) {
        const typed = String(values[secretFieldId(secret)] ?? '').trim();
        if (typed) {
          value[secret.prop] = typed;
        }
      }
      return { key, value };
    })
    .filter((change) => Object.keys(change.value).length > 0);
}

// ---------------------------------------------------------------------------------------------------- scope

export interface ScopedSettings {
  /** What the form starts from: this scope's rows, or for a facility without its own, the rows for all facilities. */
  stored: Record<string, unknown>;
  /** Keys shown from the rows for all facilities because this facility has none of its own. */
  inherited: string[];
  secretStatus: Record<string, SecretStatus>;
}

export function scopedSettings(
  rows: readonly SettingRow[],
  spec: SettingsSpec,
  scope: Scope,
  facilityId: string | null,
): ScopedSettings {
  const target = scope === 'facility' ? facilityId : null;
  const own = (key: string) => rows.find((row) => row.key === key && (row.facilityId ?? null) === target);
  const global = (key: string) => rows.find((row) => row.key === key && row.facilityId === null);
  const stored: Record<string, unknown> = {};
  const inherited: string[] = [];
  for (const key of specKeys(spec)) {
    const row = own(key);
    if (row) {
      stored[key] = row.value ?? undefined;
      continue;
    }
    const fallback = scope === 'facility' ? global(key) : undefined;
    if (fallback) {
      stored[key] = fallback.value ?? undefined;
      inherited.push(key);
    }
  }
  const secretStatus: Record<string, SecretStatus> = {};
  for (const key of specSecretKeys(spec)) {
    secretStatus[key] = own(key)?.hasValue
      ? 'set'
      : scope === 'facility' && global(key)?.hasValue
        ? 'inherited'
        : 'not-set';
  }
  return { stored, inherited, secretStatus };
}

/** The scope to show first: this facility when it has its own rows, otherwise all facilities. */
export function defaultScope(rows: readonly SettingRow[], spec: SettingsSpec, facilityId: string | null): Scope {
  if (spec.globalOnly || !facilityId) {
    return 'global';
  }
  const keys = new Set([...specKeys(spec), ...specSecretKeys(spec)]);
  return rows.some((row) => keys.has(row.key) && row.facilityId === facilityId) ? 'facility' : 'global';
}

// ---------------------------------------------------------------------------------------------------- the forms

const when = (key: string, path: string | undefined, ...values: string[]): FieldCondition => ({
  field: fieldId({ key, path }),
  values,
});
const switchedOn = (key: string): FieldCondition => when(key, 'enabled', 'true');

const IDENTIFIER = /^[A-Za-z0-9_-]{1,32}$/;

/** POS terminals: `[{ id, name, imei, storePosCode }]`, ids as billing counters use them. */
export function checkTerminals(value: unknown): string | null {
  if (!Array.isArray(value)) {
    return null;
  }
  const seen = new Set<string>();
  for (const [index, item] of value.entries()) {
    if (!isPlainObject(item) || typeof item.id !== 'string' || !IDENTIFIER.test(item.id)) {
      return `Terminal ${index + 1} needs an "id" of 1–32 letters, digits, - or _.`;
    }
    if (seen.has(item.id)) {
      return `Two terminals have the id ${item.id}.`;
    }
    seen.add(item.id);
  }
  return null;
}

/** Analysers: `[{ code, name, protocol (Hl7v2 | Astm), port, enabled, allowedIps[], encoding, sampleIdFields[], hostQuery }]`. */
export function checkInstruments(value: unknown): string | null {
  if (!Array.isArray(value)) {
    return null;
  }
  const codes = new Set<string>();
  const ports = new Set<number>();
  for (const [index, item] of value.entries()) {
    if (!isPlainObject(item) || typeof item.code !== 'string' || !IDENTIFIER.test(item.code)) {
      return `Analyser ${index + 1} needs a "code" of 1–32 letters, digits, - or _.`;
    }
    const { code, port, protocol } = item;
    if (typeof port !== 'number' || !Number.isInteger(port) || port < 1 || port > 65535) {
      return `Analyser ${code}: "port" must be a number from 1 to 65535.`;
    }
    if (
      protocol !== undefined &&
      !(typeof protocol === 'string' && /^(hl7v2|astm)$/i.test(protocol)) &&
      protocol !== 0 &&
      protocol !== 1
    ) {
      return `Analyser ${code}: "protocol" must be Hl7v2 or Astm.`;
    }
    if (codes.has(code)) {
      return `Two analysers have the code ${code}.`;
    }
    if (ports.has(port)) {
      return `Two analysers use port ${port}.`;
    }
    codes.add(code);
    ports.add(port);
  }
  return null;
}

const SMS = 'integrations.sms';
const SMS_SECRET = 'integrations.sms.secret';
const smsBy = (...providers: string[]) => when(SMS, 'provider', ...providers);

const smsSpec: SettingsSpec = {
  id: 'sms',
  title: 'SMS',
  intro:
    'Reminders, OTPs, bills and reports by SMS. Indian routes need TRAI DLT registration: each message template carries its approved DLT template id.',
  prefix: SMS,
  backgroundUsesGlobal: true,
  fields: [
    {
      key: SMS,
      path: 'enabled',
      label: 'Send SMS',
      kind: 'switch',
      hint: 'Turn off to stop all SMS without losing these settings.',
    },
    {
      key: SMS,
      path: 'provider',
      label: 'Provider',
      kind: 'select',
      options: [
        { value: 'msg91', label: 'MSG91' },
        { value: 'twilio', label: 'Twilio' },
        { value: 'generic', label: 'Other gateway (HTTP)' },
        { value: 'simulator', label: 'Simulator (training: nothing is sent)' },
      ],
      requiredWhen: switchedOn(SMS),
    },
    {
      key: SMS,
      path: 'senderId',
      label: 'Sender id (header)',
      kind: 'text',
      placeholder: 'NANAHS',
      maxLength: 11,
      hint: 'The TRAI-registered header patients see.',
    },
    {
      key: SMS,
      path: 'dltEntityId',
      label: 'DLT entity id',
      kind: 'text',
      maxLength: 30,
      hint: 'Your principal entity id from the DLT portal.',
    },
    {
      key: SMS,
      path: 'msg91.baseUrl',
      label: 'MSG91 address',
      kind: 'url',
      https: true,
      placeholder: 'https://control.msg91.com',
      when: smsBy('msg91'),
      group: 'MSG91',
    },
    {
      key: SMS,
      path: 'msg91.templateIds',
      label: 'Template ids (DLT id = MSG91 id)',
      kind: 'map',
      when: smsBy('msg91'),
      hint: 'Only where MSG91’s template id differs from the DLT id. One per line.',
    },
    {
      key: SMS,
      path: 'msg91.textVariable',
      label: 'Whole-text variable',
      kind: 'text',
      when: smsBy('msg91'),
      hint: 'When a message cannot be matched to its template, send the whole text in this MSG91 variable. Leave empty to refuse instead.',
    },
    { key: SMS, path: 'msg91.shortUrl', label: 'Shorten links with MSG91', kind: 'switch', when: smsBy('msg91') },
    {
      key: SMS,
      path: 'twilio.baseUrl',
      label: 'Twilio address',
      kind: 'url',
      https: true,
      placeholder: 'https://api.twilio.com',
      when: smsBy('twilio'),
      group: 'Twilio',
    },
    {
      key: SMS,
      path: 'twilio.accountSid',
      label: 'Account SID',
      kind: 'text',
      when: smsBy('twilio'),
      pattern: {
        regex: /^AC[0-9a-fA-F]{32}$/,
        message: 'A Twilio account SID starts with AC followed by 32 letters and digits.',
      },
    },
    {
      key: SMS,
      path: 'twilio.from',
      label: 'From number',
      kind: 'text',
      placeholder: '+14155550100',
      when: smsBy('twilio'),
      hint: 'In international format. Or use a messaging service below.',
    },
    {
      key: SMS,
      path: 'twilio.messagingServiceSid',
      label: 'Messaging service SID',
      kind: 'text',
      when: smsBy('twilio'),
    },
    {
      key: SMS,
      path: 'twilio.statusCallbackUrl',
      label: 'Delivery report address',
      kind: 'url',
      https: true,
      when: smsBy('twilio'),
      hint: 'HC360’s public Twilio webhook exactly as Twilio calls it, ending in /api/v1/integrations/webhooks/sms/twilio.',
    },
    {
      key: SMS,
      path: 'generic.url',
      label: 'Gateway address',
      kind: 'url',
      when: smsBy('generic'),
      group: 'Other gateway',
      hint: 'May contain {{to}}, {{toDigits}}, {{to10}}, {{text}}, {{senderId}}, {{dltTemplateId}}, {{dltEntityId}}, {{reference}} and {{apiKey}}.',
    },
    {
      key: SMS,
      path: 'generic.method',
      label: 'Method',
      kind: 'select',
      defaultValue: 'POST',
      options: [
        { value: 'POST', label: 'POST' },
        { value: 'GET', label: 'GET' },
      ],
      when: smsBy('generic'),
    },
    {
      key: SMS,
      path: 'generic.contentType',
      label: 'Content type',
      kind: 'text',
      placeholder: 'application/json',
      when: smsBy('generic'),
    },
    {
      key: SMS,
      path: 'generic.bodyTemplate',
      label: 'Request body',
      kind: 'textarea',
      when: smsBy('generic'),
      hint: 'Uses the same placeholders as the address.',
    },
    {
      key: SMS,
      path: 'generic.headers',
      label: 'Extra headers (name = value)',
      kind: 'map',
      when: smsBy('generic'),
      hint: 'Not for keys: the API key goes in the credentials.',
    },
    {
      key: SMS,
      path: 'generic.apiKeyHeader',
      label: 'API key header',
      kind: 'text',
      placeholder: 'Authorization',
      when: smsBy('generic'),
    },
    {
      key: SMS,
      path: 'generic.apiKeyPrefix',
      label: 'API key prefix',
      kind: 'text',
      placeholder: 'Bearer ',
      keepSpaces: true,
      when: smsBy('generic'),
      hint: 'Include the space, e.g. “Bearer ”.',
    },
    {
      key: SMS,
      path: 'generic.messageIdPath',
      label: 'Message id in the reply',
      kind: 'text',
      placeholder: 'data.id',
      when: smsBy('generic'),
    },
    {
      key: SMS,
      path: 'generic.successContains',
      label: 'A successful reply contains',
      kind: 'text',
      when: smsBy('generic'),
      hint: 'For gateways that answer OK even when sending failed.',
    },
    {
      key: SMS,
      path: 'webhookAllowedIps',
      label: 'Delivery reports only from these IP addresses',
      kind: 'list',
      hint: 'Optional. One address per line.',
      group: 'Delivery reports',
    },
  ],
  secrets: [
    { key: SMS_SECRET, prop: 'msg91AuthKey', label: 'MSG91 auth key', hint: 'Used by MSG91.' },
    { key: SMS_SECRET, prop: 'twilioAuthToken', label: 'Twilio auth token', hint: 'Used by Twilio.' },
    { key: SMS_SECRET, prop: 'genericApiKey', label: 'Gateway API key', hint: 'Used by other gateways ({{apiKey}}).' },
    {
      key: SMS_SECRET,
      prop: 'webhookToken',
      label: 'Delivery report token',
      hint: 'MSG91 and other gateways add ?token=… to their delivery report calls.',
    },
  ],
};

const WA = 'integrations.whatsapp';
const WA_SECRET = 'integrations.whatsapp.secret';
const waBy = (...providers: string[]) => when(WA, 'provider', ...providers);

const whatsAppSpec: SettingsSpec = {
  id: 'whatsapp',
  title: 'WhatsApp',
  intro:
    'Template messages and documents (reports, bills) through the Meta WhatsApp Cloud API. Patients’ replies appear in the WhatsApp inbox.',
  prefix: WA,
  backgroundUsesGlobal: true,
  fields: [
    { key: WA, path: 'enabled', label: 'Send WhatsApp messages', kind: 'switch' },
    {
      key: WA,
      path: 'provider',
      label: 'Provider',
      kind: 'select',
      options: [
        { value: 'meta', label: 'Meta WhatsApp Cloud API' },
        { value: 'simulator', label: 'Simulator (training: nothing is sent)' },
      ],
      requiredWhen: switchedOn(WA),
    },
    {
      key: WA,
      path: 'graphBaseUrl',
      label: 'Graph API address',
      kind: 'url',
      https: true,
      placeholder: 'https://graph.facebook.com',
      when: waBy('meta'),
      group: 'Meta',
    },
    {
      key: WA,
      path: 'apiVersion',
      label: 'API version',
      kind: 'text',
      placeholder: 'v21.0',
      when: waBy('meta'),
      pattern: { regex: /^v\d{1,3}\.\d{1,2}$/, message: 'Use a version such as v21.0.' },
    },
    {
      key: WA,
      path: 'phoneNumberId',
      label: 'Phone number id',
      kind: 'text',
      when: waBy('meta'),
      hint: 'The id from WhatsApp Manager, not the phone number itself.',
      pattern: { regex: /^\d{5,25}$/, message: 'The phone number id is a long number.' },
    },
    { key: WA, path: 'businessAccountId', label: 'Business account id', kind: 'text', when: waBy('meta') },
    {
      key: WA,
      path: 'languageCodes',
      label: 'Template languages (HC360 = WhatsApp)',
      kind: 'map',
      hint: 'For example en = en_US and hi = hi.',
    },
    {
      key: WA,
      path: 'maxDocumentMegabytes',
      label: 'Largest document (MB)',
      kind: 'number',
      defaultNumber: 20,
      min: 1,
      max: 100,
    },
    {
      key: WA,
      path: 'testTemplateName',
      label: 'Template for “Send test”',
      kind: 'text',
      placeholder: 'hello_world',
      pattern: {
        regex: /^[a-z0-9_]{1,512}$/,
        message: 'Template names use lower-case letters, digits and underscores.',
      },
    },
    {
      key: WA,
      path: 'testTemplateLanguage',
      label: 'Its language',
      kind: 'text',
      placeholder: 'en_US',
      pattern: { regex: /^[a-z]{2,3}(_[A-Z]{2})?$/, message: 'Use a code such as en or en_US.' },
    },
  ],
  secrets: [
    {
      key: WA_SECRET,
      prop: 'accessToken',
      label: 'Access token',
      hint: 'A system-user token with whatsapp_business_messaging permission.',
    },
    { key: WA_SECRET, prop: 'appSecret', label: 'App secret', hint: 'Checks the signature on incoming webhooks.' },
    {
      key: WA_SECRET,
      prop: 'verifyToken',
      label: 'Webhook verify token',
      hint: 'The token you typed when subscribing the webhook in Meta.',
    },
  ],
};

const EMAIL_KEY = 'integrations.email';
const emailBy = (...providers: string[]) => when(EMAIL_KEY, 'provider', ...providers);

const emailSpec: SettingsSpec = {
  id: 'email',
  title: 'E-mail',
  intro: 'Reports, bills and notices by e-mail through your mail server (SMTP with STARTTLS, usually port 587).',
  prefix: EMAIL_KEY,
  backgroundUsesGlobal: true,
  fields: [
    { key: EMAIL_KEY, path: 'enabled', label: 'Send e-mail', kind: 'switch' },
    {
      key: EMAIL_KEY,
      path: 'provider',
      label: 'Provider',
      kind: 'select',
      options: [
        { value: 'smtp', label: 'Mail server (SMTP)' },
        { value: 'simulator', label: 'Simulator (training: nothing is sent)' },
      ],
      requiredWhen: switchedOn(EMAIL_KEY),
    },
    {
      key: EMAIL_KEY,
      path: 'host',
      label: 'Mail server',
      kind: 'text',
      placeholder: 'smtp.office365.com',
      when: emailBy('smtp'),
      group: 'Mail server',
    },
    {
      key: EMAIL_KEY,
      path: 'port',
      label: 'Port',
      kind: 'number',
      defaultNumber: 587,
      min: 1,
      max: 65535,
      when: emailBy('smtp'),
      check: (port) => (port === 465 ? 'Port 465 (implicit TLS) is not supported. Use 587 with STARTTLS.' : null),
    },
    {
      key: EMAIL_KEY,
      path: 'useStartTls',
      label: 'Encrypt the connection (STARTTLS)',
      kind: 'switch',
      defaultValue: true,
      when: emailBy('smtp'),
      hint: 'Keep this on for any server outside the hospital network.',
    },
    {
      key: EMAIL_KEY,
      path: 'userName',
      label: 'User name',
      kind: 'text',
      when: emailBy('smtp'),
      hint: 'Or keep it with the password in the credentials.',
    },
    {
      key: EMAIL_KEY,
      path: 'fromAddress',
      label: 'From address',
      kind: 'email',
      placeholder: 'noreply@nanahospitals.in',
    },
    { key: EMAIL_KEY, path: 'fromName', label: 'From name', kind: 'text', placeholder: 'Nana Hospitals' },
    {
      key: EMAIL_KEY,
      path: 'timeoutSeconds',
      label: 'Time limit (seconds)',
      kind: 'number',
      defaultNumber: 20,
      min: 5,
      max: 300,
    },
    {
      key: EMAIL_KEY,
      path: 'maxAttachmentMegabytes',
      label: 'Largest attachment (MB)',
      kind: 'number',
      defaultNumber: 15,
      min: 1,
      max: 50,
    },
  ],
  secrets: [
    { key: 'integrations.email.secret', prop: 'userName', label: 'User name', hint: 'Used by the mail server.' },
    { key: 'integrations.email.secret', prop: 'password', label: 'Password', hint: 'Used by the mail server.' },
  ],
};

const PAY = 'integrations.payment';
const PAY_SECRET = 'integrations.payment.secret';
const payBy = (...providers: string[]) => when(PAY, 'provider', ...providers);

const paymentSpec: SettingsSpec = {
  id: 'payment',
  title: 'Payment gateway',
  intro: 'Online payment links and portal prepayments through Razorpay. Amounts from ₹1 to ₹1,00,00,000.',
  prefix: PAY,
  fields: [
    { key: PAY, path: 'enabled', label: 'Take online payments', kind: 'switch' },
    {
      key: PAY,
      path: 'provider',
      label: 'Provider',
      kind: 'select',
      options: [
        { value: 'razorpay', label: 'Razorpay' },
        { value: 'simulator', label: 'Simulator (training: no money moves)' },
      ],
      requiredWhen: switchedOn(PAY),
    },
    {
      key: PAY,
      path: 'baseUrl',
      label: 'Razorpay address',
      kind: 'url',
      https: true,
      placeholder: 'https://api.razorpay.com',
      when: payBy('razorpay'),
      group: 'Razorpay',
    },
    {
      key: PAY,
      path: 'keyId',
      label: 'Key id',
      kind: 'text',
      when: payBy('razorpay'),
      hint: 'rzp_live_… or rzp_test_…. It is public by design; the key secret goes in the credentials.',
      pattern: {
        regex: /^rzp_(live|test)_[A-Za-z0-9]+$/,
        message: 'A Razorpay key id starts with rzp_live_ or rzp_test_.',
      },
    },
    {
      key: PAY,
      path: 'paymentLinkCallbackUrl',
      label: 'Page after paying a link',
      kind: 'url',
      https: true,
      when: payBy('razorpay'),
      hint: 'Optional.',
    },
    {
      key: PAY,
      path: 'notifyByRazorpay',
      label: 'Let Razorpay also send payment links by SMS or e-mail',
      kind: 'switch',
      when: payBy('razorpay'),
      hint: 'Off: HC360 sends links with its own templates.',
    },
    {
      key: PAY,
      path: 'paymentLinkExpiryHours',
      label: 'Payment links expire after (hours)',
      kind: 'number',
      defaultNumber: 72,
      min: 1,
      max: 2160,
    },
    {
      key: PAY,
      path: 'simulatorSettlementDays',
      label: 'Simulator pays the bank after (days)',
      kind: 'number',
      defaultNumber: 1,
      min: 0,
      max: 7,
      when: payBy('simulator'),
      hint: '1 settles at 10:00 the next day, like Razorpay. 0 settles at once when settlements are read, for training.',
    },
  ],
  secrets: [
    {
      key: PAY_SECRET,
      prop: 'keySecret',
      label: 'Key secret',
      hint: 'Used by Razorpay (API and checkout signatures).',
    },
    { key: PAY_SECRET, prop: 'webhookSecret', label: 'Webhook secret', hint: 'From Razorpay Dashboard → Webhooks.' },
    {
      key: PAY_SECRET,
      prop: 'simulatorSecret',
      label: 'Simulator signing key',
      hint: 'Optional, simulator only: for test tools that sign simulated payments.',
    },
  ],
};

const POS = 'integrations.pos';
const posBy = (...providers: string[]) => when(POS, 'provider', ...providers);

const posSpec: SettingsSpec = {
  id: 'pos',
  title: 'Card/UPI POS',
  intro:
    'Pushes the bill amount to the card/UPI terminal at the counter (Pine Labs Plutus Cloud) and reads the result.',
  prefix: POS,
  fields: [
    { key: POS, path: 'enabled', label: 'Use POS terminals', kind: 'switch' },
    {
      key: POS,
      path: 'provider',
      label: 'Provider',
      kind: 'select',
      options: [
        { value: 'pinelabs', label: 'Pine Labs' },
        { value: 'simulator', label: 'Simulator (training: no card is charged)' },
      ],
      requiredWhen: switchedOn(POS),
    },
    {
      key: POS,
      path: 'baseUrl',
      label: 'Pine Labs address',
      kind: 'url',
      https: true,
      placeholder: 'https://www.plutuscloudserviceuat.in:8201',
      when: posBy('pinelabs'),
      group: 'Pine Labs',
    },
    { key: POS, path: 'merchantId', label: 'Merchant id', kind: 'text', when: posBy('pinelabs') },
    { key: POS, path: 'userId', label: 'User id', kind: 'text', when: posBy('pinelabs') },
    {
      key: POS,
      path: 'uploadPath',
      label: 'Upload path',
      kind: 'text',
      placeholder: '/API/CloudBasedIntegration/V1/UploadBilledTransaction',
      when: posBy('pinelabs'),
    },
    {
      key: POS,
      path: 'statusPath',
      label: 'Status path',
      kind: 'text',
      placeholder: '/API/CloudBasedIntegration/V1/GetCloudBasedTxnStatus',
      when: posBy('pinelabs'),
    },
    {
      key: POS,
      path: 'cancelPath',
      label: 'Cancel path',
      kind: 'text',
      placeholder: '/API/CloudBasedIntegration/V1/CancelTransaction',
      when: posBy('pinelabs'),
    },
    {
      key: POS,
      path: 'paymentModes',
      label: 'Payment mode codes (mode = code)',
      kind: 'map',
      when: posBy('pinelabs'),
      hint: 'Defaults: all = 0, card = 1, upi = 10.',
    },
    {
      key: POS,
      path: 'pendingResponseCodes',
      label: 'Codes that mean “still waiting”',
      kind: 'list',
      numeric: true,
      when: posBy('pinelabs'),
      hint: 'Default 1001. One per line.',
    },
    {
      key: POS,
      path: 'autoCancelMinutes',
      label: 'Terminal cancels an unpaid sale after (minutes)',
      kind: 'number',
      defaultNumber: 5,
      min: 1,
      max: 60,
    },
    {
      key: POS,
      path: 'terminals',
      label: 'Terminals',
      kind: 'json',
      shape: 'array',
      check: checkTerminals,
      group: 'Terminals',
      hint: 'One entry per counter, e.g. [{ "id": "OPD-1", "name": "OPD counter 1", "imei": "…", "storePosCode": "…" }]. Billing counters use the id.',
    },
    {
      key: POS,
      path: 'simulatorApproveAfterSeconds',
      label: 'Simulator approves a sale after (seconds)',
      kind: 'number',
      defaultNumber: 8,
      min: 0,
      max: 120,
      when: posBy('simulator'),
    },
  ],
  secrets: [
    { key: 'integrations.pos.secret', prop: 'securityToken', label: 'Security token', hint: 'Used by Pine Labs.' },
  ],
};

const TALLY = 'integrations.tally';

const tallySpec: SettingsSpec = {
  id: 'tally',
  title: 'Tally',
  intro:
    'Posts the billing day book to Tally Prime over its XML port (Tally → F1 Help → Settings → Connectivity). Tally’s port has no password.',
  prefix: TALLY,
  backgroundUsesGlobal: true,
  fields: [
    { key: TALLY, path: 'enabled', label: 'Post to Tally', kind: 'switch' },
    {
      key: TALLY,
      path: 'provider',
      label: 'Provider',
      kind: 'select',
      options: [
        { value: 'tally', label: 'Tally Prime' },
        { value: 'simulator', label: 'Simulator (training: nothing is posted)' },
      ],
      requiredWhen: switchedOn(TALLY),
    },
    {
      key: TALLY,
      path: 'url',
      label: 'Tally address',
      kind: 'url',
      placeholder: 'http://localhost:9000',
      when: when(TALLY, 'provider', 'tally'),
      hint: 'Usually the accounts computer, for example http://192.168.1.20:9000.',
    },
    {
      key: TALLY,
      path: 'companyName',
      label: 'Company',
      kind: 'text',
      hint: 'Must be open in Tally. Leave empty for the active company.',
    },
    {
      key: TALLY,
      path: 'ledgerMap',
      label: 'Ledger names (HC360 = Tally)',
      kind: 'map',
      hint: 'Only where the Tally ledger has a different name.',
    },
    { key: TALLY, path: 'costCategory', label: 'Cost category', kind: 'text', placeholder: 'Primary Cost Category' },
    {
      key: TALLY,
      path: 'batchSize',
      label: 'Vouchers per import',
      kind: 'number',
      defaultNumber: 50,
      min: 1,
      max: 500,
    },
  ],
  secrets: [],
};

const PACS = 'integrations.pacs';
const PACS_SECRET = 'integrations.pacs.secret';
const pacsBy = (...providers: string[]) => when(PACS, 'provider', ...providers);

const pacsSpec: SettingsSpec = {
  id: 'pacs',
  title: 'PACS',
  intro:
    'Finds radiology studies on the PACS (DICOMweb QIDO-RS, e.g. Orthanc or dcm4chee) and opens them in the viewer.',
  prefix: PACS,
  fields: [
    { key: PACS, path: 'enabled', label: 'Look up studies on the PACS', kind: 'switch' },
    {
      key: PACS,
      path: 'provider',
      label: 'Provider',
      kind: 'select',
      options: [
        { value: 'dicomweb', label: 'DICOMweb' },
        { value: 'simulator', label: 'Simulator (training)' },
      ],
      requiredWhen: switchedOn(PACS),
    },
    {
      key: PACS,
      path: 'qidoUrl',
      label: 'DICOMweb address',
      kind: 'url',
      placeholder: 'http://orthanc:8042/dicom-web',
      when: pacsBy('dicomweb'),
      group: 'DICOMweb',
    },
    {
      key: PACS,
      path: 'authType',
      label: 'Sign-in',
      kind: 'select',
      defaultValue: 'none',
      options: [
        { value: 'none', label: 'None' },
        { value: 'basic', label: 'User name and password' },
        { value: 'bearer', label: 'Token' },
      ],
      when: pacsBy('dicomweb'),
    },
    {
      key: PACS,
      path: 'userName',
      label: 'User name',
      kind: 'text',
      when: pacsBy('dicomweb'),
      hint: 'For sign-in with a user name and password.',
    },
    {
      key: PACS,
      path: 'viewerUrlTemplate',
      label: 'Viewer link',
      kind: 'text',
      hint: 'With {studyUid} and {accession}, e.g. https://pacs.local/ohif/viewer?StudyInstanceUIDs={studyUid}.',
      check: (text) =>
        typeof text === 'string' && !/^https?:\/\//i.test(text)
          ? 'The viewer link must start with http:// or https://.'
          : null,
    },
    { key: PACS, path: 'limit', label: 'Studies per search', kind: 'number', defaultNumber: 50, min: 1, max: 500 },
    { key: PACS, path: 'timeZone', label: 'PACS time zone', kind: 'text', placeholder: 'Asia/Kolkata' },
  ],
  secrets: [
    { key: PACS_SECRET, prop: 'password', label: 'Password', hint: 'For sign-in with a user name and password.' },
    { key: PACS_SECRET, prop: 'bearerToken', label: 'Token', hint: 'For token sign-in.' },
  ],
};

const ICD = 'integrations.icd';
const icdBy = (...providers: string[]) => when(ICD, 'provider', ...providers);

const icdSpec: SettingsSpec = {
  id: 'icd',
  title: 'ICD lookup',
  intro: 'Online ICD-11 search and ICD-10 code lookup with the WHO ICD-API. The local ICD-10 list always works.',
  prefix: ICD,
  fields: [
    { key: ICD, path: 'enabled', label: 'Search ICD online', kind: 'switch' },
    {
      key: ICD,
      path: 'provider',
      label: 'Provider',
      kind: 'select',
      options: [
        { value: 'who', label: 'WHO ICD-API' },
        { value: 'disabled', label: 'Off (local ICD-10 list only)' },
      ],
      requiredWhen: switchedOn(ICD),
    },
    {
      key: ICD,
      path: 'tokenUrl',
      label: 'Sign-in address',
      kind: 'url',
      https: true,
      placeholder: 'https://icdaccessmanagement.who.int/connect/token',
      when: icdBy('who'),
      group: 'WHO ICD-API',
    },
    {
      key: ICD,
      path: 'apiBaseUrl',
      label: 'API address',
      kind: 'url',
      https: true,
      placeholder: 'https://id.who.int',
      when: icdBy('who'),
    },
    {
      key: ICD,
      path: 'icd11Release',
      label: 'ICD-11 release',
      kind: 'text',
      placeholder: '2025-01',
      when: icdBy('who'),
    },
    { key: ICD, path: 'icd10Release', label: 'ICD-10 release', kind: 'text', placeholder: '2019', when: icdBy('who') },
    {
      key: ICD,
      path: 'language',
      label: 'Language',
      kind: 'text',
      placeholder: 'en',
      when: icdBy('who'),
      pattern: { regex: /^[a-z]{2}(-[A-Za-z]{2})?$/, message: 'Use a code such as en.' },
    },
    {
      key: ICD,
      path: 'cacheHours',
      label: 'Keep answers for (hours)',
      kind: 'number',
      defaultNumber: 24,
      min: 1,
      max: 720,
      when: icdBy('who'),
    },
  ],
  secrets: [
    {
      key: 'integrations.icd.secret',
      prop: 'clientId',
      label: 'Client id',
      hint: 'From the WHO ICD-API access portal.',
    },
    { key: 'integrations.icd.secret', prop: 'clientSecret', label: 'Client secret' },
  ],
};

const LAB = 'integrations.lab';

const labSpec: SettingsSpec = {
  id: 'lab',
  title: 'Laboratory equipment',
  intro:
    'Receives results from analysers over the lab network (HL7 v2 over MLLP, or ASTM). One listening port per analyser; the server checks every minute.',
  prefix: LAB,
  globalOnly: true,
  fields: [
    { key: LAB, path: 'enabled', label: 'Receive results from analysers', kind: 'switch' },
    {
      key: LAB,
      path: 'bindAddress',
      label: 'Listen on address',
      kind: 'text',
      placeholder: '0.0.0.0',
      hint: 'Use the lab network address to keep analyser ports off other networks.',
    },
    {
      key: LAB,
      path: 'maxMessageKilobytes',
      label: 'Largest message (KB)',
      kind: 'number',
      defaultNumber: 1024,
      min: 16,
      max: 16_384,
    },
    {
      key: LAB,
      path: 'idleTimeoutSeconds',
      label: 'Close idle connections after (seconds)',
      kind: 'number',
      defaultNumber: 300,
      min: 30,
      max: 86_400,
    },
    {
      key: LAB,
      path: 'maxConnectionsPerInstrument',
      label: 'Connections per analyser',
      kind: 'number',
      defaultNumber: 4,
      min: 1,
      max: 32,
    },
    { key: LAB, path: 'timeZone', label: 'Analyser time zone', kind: 'text', placeholder: 'Asia/Kolkata' },
    {
      key: 'integrations.lab.instruments',
      label: 'Analysers',
      kind: 'json',
      shape: 'array',
      check: checkInstruments,
      group: 'Analysers',
      hint: 'One entry per analyser, e.g. [{ "code": "XN550", "name": "Sysmex XN-550", "protocol": "Hl7v2", "port": 5100, "allowedIps": ["192.168.10.21"] }]. Protocol is Hl7v2 or Astm; ASTM analysers can also set "hostQuery".',
    },
  ],
  secrets: [],
};

/** Integrations with settings (barcode labels need none). */
export const INTEGRATION_SETTINGS: Partial<Record<IntegrationName, SettingsSpec>> = {
  sms: smsSpec,
  whatsapp: whatsAppSpec,
  email: emailSpec,
  payment: paymentSpec,
  pos: posSpec,
  tally: tallySpec,
  pacs: pacsSpec,
  lab: labSpec,
  icd: icdSpec,
};

const SPEECH = 'ai.speech';
const whisper = when(SPEECH, 'provider', 'whisper');

/** AI settings (docs/modules/ai.md): per facility, falling back to all facilities. */
export const AI_SETTINGS: SettingsSpec = {
  id: 'ai',
  title: 'AI assistant',
  intro:
    'AI is off until you turn it on. Everyday voice and typed commands work without it; AI only helps with commands the built-in rules do not understand, and with structuring dictated notes.',
  prefix: 'ai.',
  fields: [
    {
      key: 'ai.enabled',
      label: 'Allow AI help',
      kind: 'switch',
      hint: 'Text is stripped of names, UHIDs and phone numbers before it leaves the hospital.',
    },
    {
      key: 'ai.provider',
      label: 'AI provider',
      kind: 'select',
      defaultValue: 'anthropic',
      options: [
        { value: 'anthropic', label: 'Anthropic Claude' },
        { value: 'disabled', label: 'Off' },
      ],
    },
    {
      key: 'ai.model',
      label: 'Model',
      kind: 'text',
      placeholder: 'claude-opus-5-5',
      pattern: { regex: /^[a-z0-9][a-z0-9.-]{1,63}$/, message: 'Use a model id such as claude-opus-5-5.' },
    },
    { key: 'ai.timeoutSeconds', label: 'Time limit (seconds)', kind: 'number', defaultNumber: 90, min: 10, max: 300 },
    {
      key: SPEECH,
      path: 'provider',
      label: 'Speech-to-text for browsers',
      kind: 'select',
      defaultValue: 'none',
      group: 'Voice in browsers',
      options: [
        { value: 'none', label: 'Off (HC360 Desktop dictates offline)' },
        { value: 'whisper', label: 'Whisper-compatible server' },
      ],
      hint: 'HC360 Desktop always dictates with offline Windows speech. Browsers and tablets need a speech server.',
    },
    {
      key: SPEECH,
      path: 'url',
      label: 'Transcription address',
      kind: 'url',
      placeholder: 'http://stt.lan:8000/v1/audio/transcriptions',
      when: whisper,
      requiredWhen: whisper,
      hint: 'An OpenAI-compatible /audio/transcriptions address, for example an on-premises faster-whisper server.',
    },
    { key: SPEECH, path: 'model', label: 'Speech model', kind: 'text', placeholder: 'whisper-1', when: whisper },
    {
      key: SPEECH,
      path: 'language',
      label: 'Spoken language',
      kind: 'text',
      placeholder: 'en',
      when: whisper,
      pattern: { regex: /^[a-z]{2}$/, message: 'Use a two-letter code such as en or hi.' },
    },
    {
      key: SPEECH,
      path: 'timeoutSeconds',
      label: 'Speech time limit (seconds)',
      kind: 'number',
      defaultNumber: 120,
      min: 10,
      max: 600,
      when: whisper,
    },
    {
      key: 'ai.limits',
      path: 'perUserPerHour',
      label: 'AI requests per person per hour',
      kind: 'number',
      defaultNumber: 60,
      min: 1,
      max: 10_000,
      group: 'Limits',
    },
    {
      key: 'ai.limits',
      path: 'perUserPerDay',
      label: 'AI requests per person per day',
      kind: 'number',
      defaultNumber: 300,
      min: 1,
      max: 100_000,
    },
  ],
  secrets: [
    { key: 'ai.anthropic.secret', prop: 'apiKey', label: 'Anthropic API key', hint: 'Read only while AI is on.' },
    {
      key: 'ai.speech.secret',
      prop: 'apiKey',
      label: 'Speech server key',
      hint: 'Optional bearer key for the speech server.',
    },
  ],
};
