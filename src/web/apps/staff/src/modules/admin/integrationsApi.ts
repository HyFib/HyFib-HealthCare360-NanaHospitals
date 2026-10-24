import { normalizePhone, type PagedResult } from '@hc360/core';
import { keepPreviousData, queryOptions } from '@tanstack/react-query';
import { z } from 'zod';
import { api } from '@/lib/api';

/**
 * Integrations administration (src/server/HyFib.HealthCare360.Integrations/Features/Admin): status dashboard,
 * "send a test", the integration log and the simulator inboxes. Permission `admin.integrations.manage`. PHI in logs is
 * masked by the server; secrets are never returned.
 */

/** IntegrationNames.All on the server. */
export const INTEGRATION_NAMES = [
  'sms',
  'whatsapp',
  'email',
  'payment',
  'pos',
  'tally',
  'pacs',
  'mwl',
  'lab',
  'labels',
  'icd',
  'biometric',
  'aadhaar',
] as const;
export type IntegrationName = (typeof INTEGRATION_NAMES)[number];

export const INTEGRATION_LABELS: Record<IntegrationName, string> = {
  sms: 'SMS',
  whatsapp: 'WhatsApp',
  email: 'E-mail',
  payment: 'Payment gateway',
  pos: 'Card/UPI POS',
  tally: 'Tally',
  pacs: 'PACS',
  mwl: 'Modality worklist',
  lab: 'Laboratory equipment',
  labels: 'Barcode labels',
  icd: 'ICD lookup',
  biometric: 'Biometric attendance',
  aadhaar: 'Aadhaar authentication',
};

export function isIntegrationName(value: unknown): value is IntegrationName {
  return typeof value === 'string' && (INTEGRATION_NAMES as readonly string[]).includes(value);
}

export type IntegrationHealth = 'healthy' | 'degraded' | 'failing' | 'idle' | 'disabled' | 'not_configured';

/**
 * LabListenerStatus: one analyser connection. A network analyser (`transport` tcp) is starting, listening or failed; a
 * serial analyser (serial) is waiting for its lab agent, connected, or has a port_error; the training analyser
 * (simulator) is training. The modality worklist row
 * reports its DICOM listener in the same shape: `instrumentCode` is its AE title, the messages are queries answered
 * and associations refused.
 */
export interface LabListenerStatus {
  instrumentCode: string;
  protocol: string;
  port: number;
  state: string;
  activeConnections: number;
  messagesAccepted: number;
  messagesRefused: number;
  lastMessageAt: string | null;
  lastErrorAt: string | null;
  lastError: string | null;
  transport: 'tcp' | 'serial' | 'simulator';
  agent: string | null;
  serialPort: string | null;
  connectedSince: string | null;
}

/** IntegrationStatusDto: one dashboard row. */
export interface IntegrationStatusDto {
  name: string;
  displayName: string;
  provider: string | null;
  enabled: boolean;
  configured: boolean;
  health: IntegrationHealth | (string & {});
  lastSuccessAt: string | null;
  lastFailureAt: string | null;
  lastError: string | null;
  failuresLast24Hours: number;
  listeners: LabListenerStatus[] | null;
}

/** TestIntegrationRequest: `to` for sms/whatsapp/email, `dltTemplateId` for Indian SMS, template for WhatsApp. */
export interface TestIntegrationRequest {
  to: string | null;
  message: string | null;
  dltTemplateId: string | null;
  templateName: string | null;
  languageCode: string | null;
}

export interface IntegrationTestResult {
  success: boolean;
  message: string;
  details: Record<string, string | null> | null;
}

export type IntegrationDirection = 'Outbound' | 'Inbound';

export interface IntegrationLogDto {
  id: string;
  createdAt: string;
  integration: string;
  provider: string;
  operation: string;
  direction: IntegrationDirection;
  success: boolean;
  durationMs: number | null;
  reference: string | null;
  request: string | null;
  response: string | null;
  error: string | null;
  correlationId: string | null;
}

export const SIMULATOR_CHANNELS = ['Sms', 'WhatsApp', 'Email', 'Accounting', 'Labels'] as const;
export type SimulatorChannel = (typeof SIMULATOR_CHANNELS)[number];

export interface SimulatorMessageDto {
  id: string;
  createdAt: string;
  channel: SimulatorChannel;
  recipient: string | null;
  subject: string | null;
  body: string;
  details: string | null;
  reference: string | null;
  providerMessageId: string;
}

export const SIMULATOR_TRANSACTION_KINDS = [
  'PaymentOrder',
  'PaymentLink',
  'PosSale',
  'PaymentRefund',
  'TerminalPunch',
] as const;
export type SimulatorTransactionKind = (typeof SIMULATOR_TRANSACTION_KINDS)[number];
export type SimulatorTransactionStatus =
  'Created' | 'AwaitingCustomer' | 'Captured' | 'Approved' | 'Cancelled' | 'Refunded';

export interface SimulatorTransactionDto {
  id: string;
  createdAt: string;
  completedAt: string | null;
  kind: SimulatorTransactionKind;
  externalId: string;
  amount: number;
  currency: string;
  status: SimulatorTransactionStatus;
  reference: string | null;
  terminalId: string | null;
  paymentId: string | null;
  refundedAmount: number;
}

export const WHATSAPP_KEYWORDS = ['None', 'OptIn', 'OptOut'] as const;
export type WhatsAppKeyword = (typeof WHATSAPP_KEYWORDS)[number];

/** WhatsAppKeywordAction: what HC360 did about a STOP or START (null: received before HC360 acted on them). */
export type WhatsAppKeywordAction = 'None' | 'OptedOut' | 'OptedIn' | 'Superseded';

export interface InboundWhatsAppMessageDto {
  id: string;
  receivedAt: string;
  sentAt: string | null;
  fromPhoneMasked: string;
  profileName: string | null;
  messageType: string;
  text: string | null;
  keyword: WhatsAppKeyword;
  keywordAction: WhatsAppKeywordAction | null;
}

export interface IntegrationLogFilters {
  integration?: IntegrationName;
  success?: boolean;
  from?: string;
  to?: string;
  search?: string;
  page: number;
}

export const LOG_PAGE_SIZE = 50;
export const INBOX_PAGE_SIZE = 25;

/** A real calendar date written as yyyy-MM-dd (URL filters). */
export function isIsoDay(value: unknown): value is string {
  if (typeof value !== 'string' || !/^\d{4}-\d{2}-\d{2}$/.test(value)) {
    return false;
  }
  const date = new Date(`${value}T00:00:00Z`);
  return !Number.isNaN(date.getTime()) && date.toISOString().startsWith(value);
}

/** A page number from the URL (pages start at 1; page 1 is left out of the URL). */
export function pageFromUrl(value: unknown): number | undefined {
  return typeof value === 'number' && Number.isInteger(value) && value > 1 && value < 100_000 ? value : undefined;
}

/** A calendar date (yyyy-MM-dd, IST) as the start of that day, and the exclusive end of a "to" date. */
export function istDayStart(date: string): string {
  return `${date}T00:00:00+05:30`;
}

export function istDayEnd(date: string): string {
  const next = new Date(`${date}T00:00:00Z`);
  next.setUTCDate(next.getUTCDate() + 1);
  return `${next.toISOString().slice(0, 10)}T00:00:00+05:30`;
}

/**
 * The ABDM gateway set-up (`GET /api/v1/abdm/gateway`, `AbdmGatewayStatus`; only while ABDM health records are switched
 * on). Never the client secret: only whether the client id and secret are saved. `live` is false until the gateway client
 * is built and certified, as `remaining` lists.
 */
export interface AbdmGatewayStatus {
  enabled: boolean;
  /** Base URL, HIP id, consent manager id, client id and secret are all set. */
  configured: boolean;
  live: boolean;
  baseUrl: string | null;
  hipId: string | null;
  cmId: string | null;
  hasClientId: boolean;
  hasClientSecret: boolean;
  /** Why the set-up is incomplete or wrong. */
  problems: string[];
  /** What has to be built and certified before records are sent. */
  remaining: string[];
}

export const integrationKeys = {
  all: ['admin', 'integrations'] as const,
  status: ['admin', 'integrations', 'status'] as const,
  abdmGateway: ['admin', 'integrations', 'abdm-gateway'] as const,
};

export const integrationQueries = {
  status: () =>
    queryOptions({
      queryKey: integrationKeys.status,
      queryFn: ({ signal }) => api.get<IntegrationStatusDto[]>('/api/v1/admin/integrations', { signal }),
      refetchInterval: 60_000,
    }),
  abdmGateway: () =>
    queryOptions({
      queryKey: integrationKeys.abdmGateway,
      queryFn: ({ signal }) => api.get<AbdmGatewayStatus>('/api/v1/abdm/gateway', { signal }),
      staleTime: 60_000,
    }),
  logs: (filters: IntegrationLogFilters) =>
    queryOptions({
      queryKey: [...integrationKeys.all, 'logs', filters],
      queryFn: ({ signal }) =>
        api.get<PagedResult<IntegrationLogDto>>('/api/v1/admin/integrations/logs', {
          query: {
            integration: filters.integration,
            success: filters.success,
            from: filters.from ? istDayStart(filters.from) : undefined,
            to: filters.to ? istDayEnd(filters.to) : undefined,
            search: filters.search,
            page: filters.page,
            pageSize: LOG_PAGE_SIZE,
          },
          signal,
        }),
      placeholderData: keepPreviousData,
    }),
  simulatorMessages: (filters: { channel?: SimulatorChannel; search?: string; page: number }) =>
    queryOptions({
      queryKey: [...integrationKeys.all, 'simulator-messages', filters],
      queryFn: ({ signal }) =>
        api.get<PagedResult<SimulatorMessageDto>>('/api/v1/admin/integrations/simulator/messages', {
          query: { channel: filters.channel, search: filters.search, page: filters.page, pageSize: INBOX_PAGE_SIZE },
          signal,
        }),
      placeholderData: keepPreviousData,
      refetchInterval: 30_000,
    }),
  simulatorTransactions: (filters: { kind?: SimulatorTransactionKind; page: number }) =>
    queryOptions({
      queryKey: [...integrationKeys.all, 'simulator-transactions', filters],
      queryFn: ({ signal }) =>
        api.get<PagedResult<SimulatorTransactionDto>>('/api/v1/admin/integrations/simulator/transactions', {
          query: { kind: filters.kind, page: filters.page, pageSize: INBOX_PAGE_SIZE },
          signal,
        }),
      placeholderData: keepPreviousData,
      refetchInterval: 30_000,
    }),
  inbound: (filters: { keyword?: WhatsAppKeyword; page: number }) =>
    queryOptions({
      queryKey: [...integrationKeys.all, 'whatsapp-inbound', filters],
      queryFn: ({ signal }) =>
        api.get<PagedResult<InboundWhatsAppMessageDto>>('/api/v1/admin/integrations/whatsapp/inbound', {
          query: { keyword: filters.keyword, page: filters.page, pageSize: INBOX_PAGE_SIZE },
          signal,
        }),
      placeholderData: keepPreviousData,
      refetchInterval: 60_000,
    }),
};

export function runIntegrationTest(
  name: IntegrationName,
  request: TestIntegrationRequest,
): Promise<IntegrationTestResult> {
  return api.post<IntegrationTestResult>(`/api/v1/admin/integrations/${name}/test`, request, { timeoutMs: 90_000 });
}

// ---------------------------------------------------------------------------------------------------- display

export type Tone = 'success' | 'warning' | 'danger' | 'informative' | 'subtle' | 'brand';

/** Health word → plain label, badge colour and what to do about it. */
export function healthDisplay(health: string): { label: string; tone: Tone; hint: string } {
  switch (health) {
    case 'healthy':
      return { label: 'Working', tone: 'success', hint: 'The latest calls succeeded.' };
    case 'degraded':
      return {
        label: 'Some failures',
        tone: 'warning',
        hint: 'It works now, but some calls failed in the last 24 hours. Check the log.',
      };
    case 'failing':
      return {
        label: 'Failing',
        tone: 'danger',
        hint: 'The latest call failed. Check the settings and the log, then send a test.',
      };
    case 'idle':
      return {
        label: 'No activity yet',
        tone: 'informative',
        hint: 'Turned on, but nothing has been sent or received yet. Send a test.',
      };
    case 'disabled':
      return { label: 'Turned off', tone: 'subtle', hint: 'Set up but switched off.' };
    case 'not_configured':
      return { label: 'Not set up', tone: 'subtle', hint: 'Configure it to start using it.' };
    default:
      return { label: health || 'Unknown', tone: 'informative', hint: '' };
  }
}

const PROVIDER_LABELS: Record<string, string> = {
  simulator: 'Simulator (training)',
  msg91: 'MSG91',
  twilio: 'Twilio',
  generic: 'Other SMS gateway',
  meta: 'Meta WhatsApp Cloud API',
  smtp: 'SMTP server',
  razorpay: 'Razorpay',
  pinelabs: 'Pine Labs',
  tally: 'Tally Prime',
  dicomweb: 'DICOMweb',
  dicom: 'DICOM worklist server',
  who: 'WHO ICD-API',
  disabled: 'Off',
  tcp: 'Network and serial analysers',
  'zpl/tspl': 'ZPL / TSPL printers',
  zkteco: 'ZKTeco / eSSL terminals',
  push: 'Terminals send their punches',
  uidai: 'UIDAI, through the ASA',
};

/** The ABDM gateway's state in words and its badge colour. */
export function abdmGatewayDisplay(status: AbdmGatewayStatus): { label: string; tone: Tone; hint: string } {
  if (status.live) {
    return { label: 'Sending records', tone: 'success', hint: 'Health records are shared with ABDM.' };
  }
  if (!status.enabled) {
    return { label: 'Off', tone: 'subtle', hint: 'The gateway is switched off: no records are sent to ABDM.' };
  }
  if (!status.configured) {
    return { label: 'Not set up', tone: 'warning', hint: 'Switched on, but its set-up is incomplete.' };
  }
  return {
    label: 'Set up, not sending yet',
    tone: 'informative',
    hint: 'Set up, but records are not sent until the steps below are done.',
  };
}

/** The consent manager id in words: `sbx` is NHA's sandbox, `abdm` production. */
export function consentManagerLabel(cmId: string | null): string {
  switch (cmId) {
    case 'sbx':
      return 'Sandbox (sbx)';
    case 'abdm':
      return 'Production (abdm)';
    case null:
    case '':
      return 'Not set';
    default:
      return cmId;
  }
}

export function providerLabel(provider: string | null | undefined): string {
  if (!provider) {
    return '—';
  }
  return PROVIDER_LABELS[provider.trim().toLowerCase()] ?? provider;
}

export function integrationLabel(name: string): string {
  return isIntegrationName(name) ? INTEGRATION_LABELS[name] : name;
}

/** What a STOP or START did, for the WhatsApp inbox; null for ordinary messages. */
export function keywordActionDisplay(
  keyword: WhatsAppKeyword,
  action: WhatsAppKeywordAction | null,
): { label: string; tone: Tone; detail: string } | null {
  if (keyword === 'None') {
    return null;
  }
  switch (action) {
    case 'OptedOut':
      return {
        label: 'Messages stopped',
        tone: 'danger',
        detail:
          'WhatsApp messages to this number have stopped, except sign-in codes. Marketing consent was withdrawn for the patients registered with this number. SMS messages still go out.',
      };
    case 'OptedIn':
      return {
        label: 'Messages resumed',
        tone: 'success',
        detail:
          'WhatsApp messages to this number have resumed. Offers resume only for patients whose consent a STOP had withdrawn.',
      };
    case 'Superseded':
      return {
        label: 'No change',
        tone: 'subtle',
        detail: 'A later STOP or START from this number had already arrived, so this one changed nothing.',
      };
    case 'None':
      return {
        label: 'Not applied',
        tone: 'warning',
        detail: 'The sender’s number could not be read, so nothing was changed.',
      };
    default:
      return {
        label: 'Waiting',
        tone: 'warning',
        detail:
          'This reply arrived before HC360 acted on STOP and START. It is applied the next time the server starts.',
      };
  }
}

export const SIMULATOR_CHANNEL_LABELS: Record<SimulatorChannel, string> = {
  Sms: 'SMS',
  WhatsApp: 'WhatsApp',
  Email: 'E-mail',
  Accounting: 'Tally',
  Labels: 'Labels',
};

export const TRANSACTION_KIND_LABELS: Record<SimulatorTransactionKind, string> = {
  PaymentOrder: 'Payment order',
  PaymentLink: 'Payment link',
  PosSale: 'POS sale',
  PaymentRefund: 'Payment refund',
  TerminalPunch: 'Attendance punch',
};

/** A simulated record's status; a punch on a training attendance terminal is not a payment, whatever its status. */
export function transactionStatusDisplay(
  status: SimulatorTransactionStatus,
  kind?: SimulatorTransactionKind,
): { label: string; tone: Tone } {
  if (kind === 'TerminalPunch') {
    return { label: 'Punched', tone: 'success' };
  }
  switch (status) {
    case 'Captured':
      return { label: 'Paid', tone: 'success' };
    case 'Approved':
      return { label: 'Approved', tone: 'success' };
    case 'AwaitingCustomer':
      return { label: 'Waiting for customer', tone: 'warning' };
    case 'Created':
      return { label: 'Created', tone: 'warning' };
    case 'Cancelled':
      return { label: 'Cancelled', tone: 'danger' };
    case 'Refunded':
      return { label: 'Refunded', tone: 'informative' };
    default:
      return { label: status, tone: 'informative' };
  }
}

/** "850 ms", "1.2 s", "—". */
export function formatDuration(ms: number | null | undefined): string {
  if (ms === null || ms === undefined) {
    return '—';
  }
  return ms < 1000 ? `${ms} ms` : `${(ms / 1000).toFixed(ms < 10_000 ? 1 : 0)} s`;
}

// ---------------------------------------------------------------------------------------------------- send a test

/** What the test does, in the administrator's words (IntegrationTestService). */
export const TEST_DESCRIPTIONS: Record<IntegrationName, string> = {
  sms: 'Sends a real SMS to the mobile number you enter. Indian routes need the DLT template id of an approved template.',
  whatsapp: 'Sends the test template to the mobile number you enter. The number must have WhatsApp.',
  email: 'Sends a short test e-mail to the address you enter.',
  payment:
    'Creates a ₹1 test order with the payment gateway. No money moves unless someone pays it, and it expires unpaid.',
  pos: 'Checks the POS settings. Real terminals are not prompted, so no customer sees a charge; the simulator runs a ₹1 practice sale.',
  tally: 'Asks Tally whether it is running and the company is open. Nothing is posted.',
  pacs: 'Runs a study search against the PACS. Nothing is changed.',
  mwl: 'Sends a DICOM echo from this server to its own worklist server. No patient data is sent.',
  lab: 'Checks that each network analyser’s port is open and each serial analyser’s lab agent is connected.',
  labels: 'Builds a sample label. Print it from a workstation to check the printer.',
  icd: 'Searches online ICD lookup (the WHO ICD-API or the simulator) for a sample term.',
  biometric:
    'Checks the settings for reading attendance terminals. Each terminal is read from HR › Attendance terminals with Read now; terminals that send their punches need none of this.',
  aadhaar:
    'Checks the AUA settings, the licence keys and the signing certificate (its private key and its dates). Nothing is sent to UIDAI: check a patient’s identity from their record to test the whole path with a fingerprint reader.',
};

export type TestInput = 'mobile' | 'email' | null;

export function testInputFor(name: IntegrationName): TestInput {
  return name === 'sms' || name === 'whatsapp' ? 'mobile' : name === 'email' ? 'email' : null;
}

export const testFormSchema = z.object({
  to: z.string().trim().max(254),
  message: z.string().trim().max(500, 'Keep the message under 500 characters.'),
  dltTemplateId: z
    .string()
    .trim()
    .refine((value) => !value || /^[0-9]{10,25}$/.test(value), 'A DLT template id is a 10–25 digit number.'),
  templateName: z
    .string()
    .trim()
    .refine(
      (value) => !value || /^[a-z0-9_]{1,512}$/.test(value),
      'Template names use lower-case letters, digits and underscores.',
    ),
  languageCode: z
    .string()
    .trim()
    .refine((value) => !value || /^[a-z]{2,3}(_[A-Z]{2})?$/.test(value), 'Use a language code such as en or en_US.'),
});

export type TestFormValues = z.infer<typeof testFormSchema>;

export const EMPTY_TEST_FORM: TestFormValues = {
  to: '',
  message: '',
  dltTemplateId: '',
  templateName: '',
  languageCode: '',
};

/** Field problems for the chosen integration (the recipient is required for messages). */
export function testFormProblems(
  name: IntegrationName,
  values: TestFormValues,
): Partial<Record<keyof TestFormValues, string>> {
  const input = testInputFor(name);
  if (input === 'mobile' && !normalizePhone(values.to)) {
    return { to: 'Enter the mobile number to send the test to, for example 98765 43210.' };
  }
  if (input === 'email' && !/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(values.to.trim())) {
    return { to: 'Enter the e-mail address to send the test to.' };
  }
  return {};
}

/** The request body: only the fields that apply to this integration, phone numbers in E.164. */
export function toTestRequest(name: IntegrationName, values: TestFormValues): TestIntegrationRequest {
  const input = testInputFor(name);
  const text = (value: string) => value.trim() || null;
  return {
    to: input === 'mobile' ? normalizePhone(values.to) : input === 'email' ? text(values.to) : null,
    message: name === 'sms' ? text(values.message) : null,
    dltTemplateId: name === 'sms' ? text(values.dltTemplateId) : null,
    templateName: name === 'whatsapp' ? text(values.templateName) : null,
    languageCode: name === 'whatsapp' ? text(values.languageCode) : null,
  };
}
