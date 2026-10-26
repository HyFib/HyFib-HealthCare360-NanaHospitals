import type { AiUsageDto } from '@/modules/admin/aiApi';
import type {
  AuditEventDto,
  AuditVerification,
  BackupRunDto,
  PermissionGroup,
  RecoveryKeyStatus,
  RoleDetail,
  RoleListItem,
  SettingDto,
  UserDetail,
  UserListItem,
} from '@/modules/admin/api';
import type { AbdmGatewayStatus, IntegrationStatusDto } from '@/modules/admin/integrationsApi';
import type { NotificationLogDto, NotificationTemplateDto } from '@/modules/admin/notificationTemplatesApi';
import { FACILITY_ID, paged, STAFF_USER, type FakeAnswers, type FakeRequest } from '../fakeApi';

// Administration: users (one locked with two-step verification, so every row action shows), roles, the audit log,
// settings, backups with the original recovery key, integrations in each state, notification templates and their
// delivery log, and the AI usage log. System health and devices are in accessibility/fixtures/admin.ts.

const now = Date.now();
const hoursAgo = (hours: number) => new Date(now - hours * 3600_000).toISOString();
const id = (suffix: string) => `0192a000-0000-7000-8000-${suffix.padStart(12, '0')}`;

// ---------------------------------------------------------------------------------------------------- users, roles

const ROLE_IDS = {
  HospitalAdmin: id('7001'),
  Doctor: id('7002'),
  Nurse: id('7003'),
  FrontOffice: id('7004'),
  QualityManager: id('7005'),
  MrdOfficer: id('7006'),
};

const ROLES: RoleListItem[] = [
  {
    id: ROLE_IDS.HospitalAdmin,
    name: 'HospitalAdmin',
    description: 'Runs the hospital system',
    isSystem: true,
    permissionCount: 153,
    userCount: 2,
  },
  {
    id: ROLE_IDS.Doctor,
    name: 'Doctor',
    description: 'Consults, prescribes and orders',
    isSystem: true,
    permissionCount: 65,
    userCount: 38,
  },
  {
    id: ROLE_IDS.Nurse,
    name: 'Nurse',
    description: 'Ward and OPD nursing',
    isSystem: true,
    permissionCount: 63,
    userCount: 72,
  },
  {
    id: ROLE_IDS.FrontOffice,
    name: 'FrontOffice',
    description: 'Registration, appointments and the queue',
    isSystem: true,
    permissionCount: 37,
    userCount: 14,
  },
  {
    id: ROLE_IDS.QualityManager,
    name: 'QualityManager',
    description: 'Incidents, CAPA, risks and indicators',
    isSystem: true,
    permissionCount: 24,
    userCount: 1,
  },
  {
    id: ROLE_IDS.MrdOfficer,
    name: 'MrdOfficer',
    description: 'Medical records and coding',
    isSystem: true,
    permissionCount: 21,
    userCount: 3,
  },
  {
    id: id('7007'),
    name: 'Night supervisor',
    description: 'Duty officer after 8 pm',
    isSystem: false,
    permissionCount: 18,
    userCount: 4,
  },
];

const PERMISSIONS: PermissionGroup[] = [
  {
    group: 'Administration',
    permissions: [
      { name: 'admin.users.manage', description: 'Add and change user accounts' },
      { name: 'admin.roles.manage', description: 'Change roles and their permissions' },
      { name: 'admin.settings.manage', description: 'Change settings' },
    ],
  },
  {
    group: 'Quality',
    permissions: [
      { name: 'quality.incidents.report', description: 'Report incidents' },
      { name: 'quality.incidents.manage', description: 'Review and close incidents' },
    ],
  },
];

const ROLE: RoleDetail = {
  id: id('7007'),
  name: 'Night supervisor',
  description: 'Duty officer after 8 pm',
  isSystem: false,
  permissions: ['quality.incidents.report', 'quality.incidents.manage'],
  version: 3,
};

/** The first row is locked and uses two-step verification: its menu offers every action. */
const USERS: UserListItem[] = [
  {
    id: id('a5'),
    userName: 'kavita.pawar',
    displayName: 'Kavita Pawar',
    email: 'kavita.pawar@nanahospitals.in',
    phone: '98******31',
    isActive: true,
    isLocked: true,
    mfaEnabled: true,
    lastLoginAt: hoursAgo(20),
    roles: ['Nurse'],
  },
  {
    id: id('a3'),
    userName: 'mary.thomas',
    displayName: 'Sr. Mary Thomas',
    email: null,
    phone: '97******02',
    isActive: true,
    isLocked: false,
    mfaEnabled: false,
    lastLoginAt: hoursAgo(2),
    roles: ['Nurse', 'QualityManager'],
  },
  {
    id: id('a6'),
    userName: 'suresh.kale',
    displayName: 'Suresh Kale',
    email: 'suresh.kale@nanahospitals.in',
    phone: null,
    isActive: false,
    isLocked: false,
    mfaEnabled: false,
    lastLoginAt: hoursAgo(24 * 90),
    roles: ['FrontOffice'],
  },
];

const detail = (user: UserListItem, roleIds: string[], more: Partial<UserDetail> = {}): UserDetail => ({
  id: user.id,
  userName: user.userName,
  displayName: user.displayName,
  email: user.email,
  phone: user.phone,
  isActive: user.isActive,
  mfaEnabled: user.mfaEnabled,
  mustChangePassword: false,
  lastLoginAt: user.lastLoginAt,
  lockoutEndsAt: user.isLocked ? hoursAgo(-0.5) : null,
  practitionerId: null,
  defaultFacilityId: FACILITY_ID,
  roleIds,
  facilityIds: [FACILITY_ID],
  version: 2,
  ...more,
});

/** Every user a screen may name (the quality screens show who an incident or a risk is assigned to). */
const USER_DETAILS: UserDetail[] = [
  detail(USERS[0]!, [ROLE_IDS.Nurse]),
  detail(USERS[1]!, [ROLE_IDS.Nurse, ROLE_IDS.QualityManager]),
  detail(USERS[2]!, [ROLE_IDS.FrontOffice]),
  {
    id: STAFF_USER.id,
    userName: STAFF_USER.userName,
    displayName: STAFF_USER.displayName,
    email: 'meera.iyer@nanahospitals.in',
    phone: null,
    isActive: true,
    mfaEnabled: true,
    mustChangePassword: false,
    lastLoginAt: hoursAgo(1),
    lockoutEndsAt: null,
    practitionerId: STAFF_USER.practitionerId,
    defaultFacilityId: FACILITY_ID,
    roleIds: [ROLE_IDS.HospitalAdmin, ROLE_IDS.Doctor],
    facilityIds: [],
    version: 9,
  },
];

/** One user by the id at the end of the path; a 404 for anything else. */
const userById = (request: FakeRequest) => USER_DETAILS.find((u) => request.path.endsWith(`/${u.id}`));

// ---------------------------------------------------------------------------------------------------- audit

const audit = (
  n: number,
  hours: number,
  category: string,
  action: string,
  summary: string,
  more: Partial<AuditEventDto> = {},
): AuditEventDto => ({
  id: n,
  occurredAt: hoursAgo(hours),
  category,
  action,
  severity: 'Info',
  entityType: null,
  entityId: null,
  patientId: null,
  userId: STAFF_USER.id,
  userName: STAFF_USER.displayName,
  ipAddress: '192.168.10.21',
  summary,
  details: null,
  sealed: true,
  ...more,
});

const AUDIT: AuditEventDto[] = [
  audit(90_412, 0.1, 'Auth', 'SignIn', 'Signed in with two-step verification'),
  audit(90_409, 0.4, 'Patient', 'Read', 'Opened the chart of Asha Devi (NH26000123)', {
    entityType: 'Patient',
    entityId: id('1'),
    patientId: id('1'),
    userId: id('a3'),
    userName: 'Sr. Mary Thomas',
  }),
  audit(90_388, 2, 'Billing', 'DiscountApproved', 'Approved a discount of ₹1,500 on OP/26/04811', {
    entityType: 'Bill',
    entityId: id('b11'),
    severity: 'Warning',
    details: '{"amount":1500,"reason":"Staff family"}',
  }),
  audit(90_301, 6, 'Auth', 'SignInFailed', 'Five wrong passwords for kavita.pawar; account locked', {
    severity: 'Critical',
    userId: null,
    userName: null,
    ipAddress: '192.168.10.77',
    sealed: false,
  }),
];

const VERIFICATION: AuditVerification = {
  valid: true,
  checkedCount: 90_412,
  firstInvalidChainIndex: null,
  unsealedCount: 1,
  message: null,
  anchorsChecked: 30,
  lastAnchor: { chainIndex: 90_000, hash: '4f2a910c7e55b3186de20a99c4713b8f', mac: null, anchoredAt: hoursAgo(10) },
};

// ---------------------------------------------------------------------------------------------------- settings

const setting = (n: string, key: string, value: unknown, more: Partial<SettingDto> = {}): SettingDto => ({
  id: id(n),
  key,
  facilityId: null,
  value,
  isSecret: false,
  hasValue: value !== null,
  description: null,
  updatedAt: hoursAgo(24 * 12),
  version: 2,
  ...more,
});

const SETTINGS: SettingDto[] = [
  setting('5701', 'ui.hospitalName', 'Nana Hospitals'),
  setting('5702', 'billing.roundOff', true, { description: 'Round bill totals to the nearest rupee' }),
  setting('5703', 'scheduling.slotMinutes', 15, { facilityId: FACILITY_ID }),
  setting('5704', 'integrations.whatsapp', {
    enabled: true,
    provider: 'meta',
    graphBaseUrl: 'https://graph.facebook.com',
    apiVersion: 'v21.0',
    phoneNumberId: '109876543210987',
    businessAccountId: '204512345678901',
    languageCodes: { en: 'en_US', hi: 'hi' },
    maxDocumentMegabytes: 20,
    testTemplateName: 'hello_world',
  }),
  setting('5705', 'integrations.whatsapp.secret', null, { isSecret: true, hasValue: true }),
  setting('5706', 'integrations.sms', { enabled: true, provider: 'simulator', senderId: 'NANAHO' }),
];

// ---------------------------------------------------------------------------------------------------- backups

const BACKUPS: BackupRunDto[] = [
  {
    id: id('bk01'),
    startedAt: hoursAgo(3.2),
    completedAt: hoursAgo(3),
    kind: 'Scheduled',
    status: 'Succeeded',
    fileName: 'hc360-20261024-0200.hc360bak',
    sizeBytes: 6.2 * 1024 ** 3,
    sha256: '7d1c0f3e9a4b2c6d8e0f1a2b3c4d5e6f7a8b9c0d1e2f3a4b5c6d7e8f9a0b1c2d',
    secondaryCopied: true,
    error: null,
    formatVersion: 2,
    documentCount: 18_204,
    verifiedAt: hoursAgo(2.9),
    recoveryKeyId: 'rk-2026-01',
  },
  {
    id: id('bk02'),
    startedAt: hoursAgo(27.1),
    completedAt: hoursAgo(27),
    kind: 'Scheduled',
    status: 'Failed',
    fileName: null,
    sizeBytes: null,
    sha256: null,
    secondaryCopied: false,
    error: 'The backup disk was not connected.',
    formatVersion: 2,
    documentCount: null,
    verifiedAt: null,
    recoveryKeyId: null,
  },
  {
    id: id('bk03'),
    startedAt: hoursAgo(24 * 9),
    completedAt: hoursAgo(24 * 9 - 0.2),
    kind: 'PreMigration',
    status: 'Succeeded',
    fileName: 'hc360-20261015-premigration.hc360bak',
    sizeBytes: 6.0 * 1024 ** 3,
    sha256: '1a2b3c4d5e6f7a8b9c0d1e2f3a4b5c6d7e8f9a0b1c2d3e4f5a6b7c8d9e0f1a2b',
    secondaryCopied: false,
    error: null,
    formatVersion: 2,
    documentCount: 17_960,
    verifiedAt: null,
    recoveryKeyId: 'rk-2026-01',
  },
];

const RECOVERY_KEY: RecoveryKeyStatus = {
  keyId: 'rk-2026-01',
  original: true,
  since: hoursAgo(24 * 280),
  by: null,
  originalShown: true,
  originalKeyId: 'rk-2026-01',
};

// ---------------------------------------------------------------------------------------------------- integrations

const integration = (
  name: string,
  displayName: string,
  provider: string | null,
  health: IntegrationStatusDto['health'],
  more: Partial<IntegrationStatusDto> = {},
): IntegrationStatusDto => ({
  name,
  displayName,
  provider,
  enabled: health !== 'disabled' && health !== 'not_configured',
  configured: health !== 'not_configured',
  health,
  lastSuccessAt: health === 'healthy' || health === 'degraded' ? hoursAgo(0.2) : null,
  lastFailureAt: health === 'degraded' || health === 'failing' ? hoursAgo(0.5) : null,
  lastError: health === 'degraded' || health === 'failing' ? 'The provider did not answer within 30 seconds.' : null,
  failuresLast24Hours: health === 'failing' ? 18 : health === 'degraded' ? 4 : 0,
  listeners: null,
  ...more,
});

const INTEGRATIONS: IntegrationStatusDto[] = [
  integration('whatsapp', 'WhatsApp', 'meta', 'healthy'),
  integration('sms', 'SMS (TRAI DLT)', 'simulator', 'healthy'),
  integration('payment', 'Payment gateway', 'razorpay', 'degraded'),
  integration('tally', 'Tally', 'tally-prime', 'failing'),
  integration('email', 'E-mail', null, 'not_configured'),
  integration('lab', 'Laboratory equipment', 'astm', 'healthy', {
    listeners: [
      {
        instrumentCode: 'SYSMEX-XN',
        protocol: 'ASTM',
        port: 5001,
        state: 'listening',
        activeConnections: 1,
        messagesAccepted: 412,
        messagesRefused: 2,
        lastMessageAt: hoursAgo(0.05),
        lastErrorAt: null,
        lastError: null,
        transport: 'tcp',
        agent: null,
        serialPort: null,
        connectedSince: hoursAgo(9),
      },
    ],
  }),
];

// ---------------------------------------------------------------------------------------------------- notifications

const template = (
  n: string,
  key: string,
  channel: NotificationTemplateDto['channel'],
  body: string,
  more: Partial<NotificationTemplateDto> = {},
): NotificationTemplateDto => ({
  id: id(n),
  facilityId: null,
  key,
  channel,
  language: 'en',
  subject: null,
  body,
  dltTemplateId: channel === 'Sms' ? '1107161234567890123' : null,
  whatsAppTemplateName: channel === 'WhatsApp' ? key.replace('.', '_') : null,
  description: null,
  isActive: true,
  version: 1,
  whatsAppDocumentHeader: false,
  ...more,
});

const TEMPLATES: NotificationTemplateDto[] = [
  template(
    '7e01',
    'appointment.reminder',
    'Sms',
    'Dear {{name}}, your appointment with {{doctor}} is on {{date}} at {{time}}. Nana Hospitals',
  ),
  template('7e02', 'lab.report_ready', 'WhatsApp', 'Dear {{name}}, your report {{report}} is ready.', {
    whatsAppDocumentHeader: true,
    version: 3,
  }),
  template(
    '7e03',
    'appointment.reminder',
    'Sms',
    'प्रिय {{name}}, {{date}} को {{time}} बजे {{doctor}} के साथ आपकी अपॉइंटमेंट है।',
    {
      language: 'hi',
    },
  ),
  template(
    '7e04',
    'bill.receipt',
    'Email',
    'Dear {{name}},\n\nPlease find your receipt {{receipt}} for ₹{{amount}} attached.',
    {
      subject: 'Your receipt from Nana Hospitals',
      facilityId: FACILITY_ID,
    },
  ),
];

const NOTIFICATION_LOGS: NotificationLogDto[] = [
  {
    id: id('9101'),
    createdAt: hoursAgo(0.2),
    channel: 'WhatsApp',
    templateKey: 'lab.report_ready',
    recipient: '98******10',
    patientId: id('1'),
    status: 'Delivered',
    error: null,
    sentAt: hoursAgo(0.19),
  },
  {
    id: id('9102'),
    createdAt: hoursAgo(1),
    channel: 'Sms',
    templateKey: 'appointment.reminder',
    recipient: '97******21',
    patientId: id('2'),
    status: 'Sent',
    error: null,
    sentAt: hoursAgo(0.99),
  },
  {
    id: id('9103'),
    createdAt: hoursAgo(2),
    channel: 'Sms',
    templateKey: 'appointment.reminder',
    recipient: '99******88',
    patientId: id('5'),
    status: 'Failed',
    error: 'DLT template id not approved for this sender',
    sentAt: null,
  },
  {
    id: id('9104'),
    createdAt: hoursAgo(3),
    channel: 'Email',
    templateKey: 'bill.receipt',
    recipient: null,
    patientId: id('3'),
    status: 'Skipped',
    error: 'No e-mail address',
    sentAt: null,
  },
  {
    id: id('9105'),
    createdAt: hoursAgo(4),
    channel: 'WhatsApp',
    templateKey: 'appointment.reminder',
    recipient: '96******54',
    patientId: id('4'),
    status: 'Suppressed',
    error: 'The number opted out (STOP).',
    sentAt: null,
  },
];

// ---------------------------------------------------------------------------------------------------- AI

const AI_USAGE: AiUsageDto[] = [
  {
    id: id('a101'),
    createdAt: hoursAgo(0.3),
    userId: STAFF_USER.id,
    patientId: id('1'),
    purpose: 'clinical.structure-note',
    provider: 'anthropic',
    model: 'claude-sonnet',
    inputTokens: 1840,
    outputTokens: 620,
    latencyMs: 4200,
    success: true,
    error: null,
  },
  {
    id: id('a102'),
    createdAt: hoursAgo(1.5),
    userId: id('a3'),
    patientId: null,
    purpose: 'voice.transcribe',
    provider: 'whisper',
    model: 'large-v3',
    inputTokens: 0,
    outputTokens: 0,
    latencyMs: 2900,
    success: true,
    error: null,
  },
  {
    id: id('a103'),
    createdAt: hoursAgo(5),
    userId: id('a5'),
    patientId: null,
    purpose: 'assistant.command',
    provider: 'anthropic',
    model: 'claude-sonnet',
    inputTokens: 420,
    outputTokens: 0,
    latencyMs: 30_000,
    success: false,
    error: 'The AI service took too long to answer.',
  },
];

// The ABDM gateway as Integrations shows it while ABDM health records are switched on: set up for NHA's sandbox.
const ABDM_GATEWAY: AbdmGatewayStatus = {
  enabled: true,
  configured: true,
  live: true,
  baseUrl: 'https://dev.abdm.gov.in/api/hiecm',
  hipId: 'IN2710000123',
  cmId: 'sbx',
  hasClientId: true,
  hasClientSecret: true,
  problems: [],
  remaining: [
    "Pass NHA's sandbox functional tests and certification (milestone M2), then switch to the production gateway.",
  ],
};

export const ANSWERS: FakeAnswers = {
  'GET /api/v1/abdm/gateway': ABDM_GATEWAY,
  'GET /api/v1/admin/users': paged(USERS),
  'GET /api/v1/admin/users/*': userById,
  'GET /api/v1/admin/roles': ROLES,
  [`GET /api/v1/admin/roles/${ROLE.id}`]: ROLE,
  'GET /api/v1/admin/permissions': PERMISSIONS,
  'GET /api/v1/admin/audit': paged(AUDIT),
  'GET /api/v1/admin/audit/verify': VERIFICATION,
  'GET /api/v1/admin/settings': SETTINGS,
  'GET /api/v1/admin/backups': BACKUPS,
  'GET /api/v1/admin/backups/recovery-key': RECOVERY_KEY,
  'GET /api/v1/admin/integrations': INTEGRATIONS,
  'GET /api/v1/admin/notification-templates': TEMPLATES,
  'GET /api/v1/admin/notification-logs': paged(NOTIFICATION_LOGS),
  'GET /api/v1/ai/usage': paged(AI_USAGE),
};
