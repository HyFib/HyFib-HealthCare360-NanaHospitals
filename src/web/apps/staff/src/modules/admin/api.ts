import type { PagedResult } from '@hc360/core';
import { keepPreviousData, queryOptions } from '@tanstack/react-query';
import { api } from '@/lib/api';
import type { ApprovalRequestDto } from '@/modules/approvals/api';

export interface UserListItem {
  id: string;
  userName: string;
  displayName: string;
  email: string | null;
  phone: string | null;
  isActive: boolean;
  isLocked: boolean;
  mfaEnabled: boolean;
  lastLoginAt: string | null;
  roles: string[];
}

export interface UserDetail {
  id: string;
  userName: string;
  displayName: string;
  email: string | null;
  phone: string | null;
  isActive: boolean;
  mfaEnabled: boolean;
  mustChangePassword: boolean;
  lastLoginAt: string | null;
  lockoutEndsAt: string | null;
  practitionerId: string | null;
  defaultFacilityId: string | null;
  roleIds: string[];
  facilityIds: string[];
  version: number;
  /** Roles and facilities asked for the user, or their reactivation, waiting for a second administrator. */
  pendingGrant?: PendingUserGrant | null;
  /**
   * Changes to the account waiting for a second administrator: a temporary password to be handed over, a two-step
   * verification reset, new contact details, a practitioner link.
   */
  pendingChanges?: PendingAccountChange[] | null;
}

/** A change to an account waiting for a second administrator who may approve grants: what it changes, in words. */
export interface PendingAccountChange {
  approvalId: string;
  kind: string;
  summary: string;
  requestedBy: string;
  requestedAt: string;
}

/**
 * A password reset made (200): where the temporary password went, masked, never the password itself. Only an account
 * with nowhere to send it, reset while the hospital has one administrator who may approve, gets it back to hand over;
 * otherwise that reset waits for a second administrator (202, an approval request), who is shown it.
 */
export interface PasswordResetResponse {
  userId: string;
  sentTo: string[];
  expiresAt: string;
  temporaryPassword?: string | null;
}

/** Access asked for a user, waiting for a second administrator who may approve grants. */
export interface PendingUserGrant {
  approvalId: string;
  roleIds: string[];
  facilityIds: string[];
  activate: boolean;
  requestedBy: string;
  requestedAt: string;
}

/** Permissions asked for a role, waiting for a second administrator who may approve grants. */
export interface PendingRoleGrant {
  approvalId: string;
  permissions: string[];
  requestedBy: string;
  requestedAt: string;
}

/** The approval request a save that adds access makes (202): the access waits for a second administrator. */
export interface AskedForApproval {
  id: string;
  /** The approval kind, such as `masters.practitioner_identity` or `masters.practitioner_fee`. */
  kind: string;
  summary: string;
}

/** The request a save made, when what it added waits for approval; null when it was saved as it is. */
export function askedForApproval(result: unknown): AskedForApproval | null {
  return result !== null && typeof result === 'object' && 'kind' in result && 'summary' in result && 'id' in result
    ? (result as AskedForApproval)
    : null;
}

export interface SaveUserRequest {
  userName?: string;
  displayName: string;
  email: string | null;
  phone: string | null;
  roleIds: string[];
  facilityIds: string[] | null;
  defaultFacilityId: string | null;
  practitionerId: string | null;
  temporaryPassword?: string | null;
  isActive?: boolean;
  version?: number;
  /** Why the user needs the roles added, for the administrator who approves them. */
  reason?: string | null;
}

export interface RoleListItem {
  id: string;
  name: string;
  description: string | null;
  isSystem: boolean;
  permissionCount: number;
  userCount: number;
}

export interface RoleDetail {
  id: string;
  name: string;
  description: string | null;
  isSystem: boolean;
  permissions: string[];
  version: number;
  /** Permissions asked for the role, waiting for a second administrator. */
  pendingGrant?: PendingRoleGrant | null;
}

export interface PermissionGroup {
  group: string;
  permissions: { name: string; description: string }[];
}

export type DeviceKind = 'Workstation' | 'Kiosk' | 'DisplayBoard' | 'Tablet' | 'LabAgent';

export interface DeviceDto {
  id: string;
  name: string;
  machineName: string | null;
  kind: DeviceKind;
  osVersion: string | null;
  appVersion: string | null;
  enrolledAt: string;
  lastSeenAt: string | null;
  lastIpAddress: string | null;
  lastWifiSsid: string | null;
  lastWifiSignal: number | null;
  isRevoked: boolean;
  facilityId: string | null;
  departmentId: string | null;
  location: string | null;
  version: number;
}

/** A one-time enrolment code, shown once: formatted for reading aloud (ABCD-EFGH), and when it expires. */
export interface EnrollmentCode {
  code: string;
  expiresAt: string;
}

/**
 * An enrolment code for a workstation or tablet the signed-in administrator asked for, from which the hospital can be
 * administered: waiting for a second administrator (`ready` false), or approved and not yet shown, until `expiresAt`.
 */
export interface EnrollmentCodeRequest {
  approvalId: string;
  kind: DeviceKind;
  note: string | null;
  validMinutes: number;
  ready: boolean;
  requestedAt: string;
  expiresAt: string | null;
}

/** The HC360 Desktop version the server offers to enrolled workstations, or why it offers none (SRS DED-18). */
export interface DesktopUpdateState {
  offeredVersion: string | null;
  releasedAt: string | null;
  problem: string | null;
}

export interface HealthWarning {
  code: string;
  severity: 'info' | 'warning' | 'critical' | string;
  message: string;
  /**
   * With several servers (SCAL-01), the server a warning about one machine is about; null for the hospital's own (the
   * database, backups, messages, keys), and on a single Server Node.
   */
  server?: string | null;
}

/**
 * IntegrationHealth: one integration as System health shows it (SRS DED-17). `health` uses the integrations dashboard's
 * words; the last error is only on that dashboard.
 */
export interface IntegrationHealthSummary {
  name: string;
  displayName: string;
  enabled: boolean;
  configured: boolean;
  health: string;
  lastSuccessAt: string | null;
  lastFailureAt: string | null;
  failuresLast24Hours: number;
}

/** One of the server's own network adapters, a cable or WiFi (DED-06): `isUp` is a cable in with a link to its switch. */
export interface NetworkLink {
  name: string;
  description: string;
  medium: 'Cable' | 'Wifi';
  isUp: boolean;
  speedMbps: number | null;
  addresses: string[];
  carriesNetwork: boolean;
}

export interface WifiConnectionInfo {
  interfaceDescription: string;
  isConnected: boolean;
  ssid: string | null;
  bssid: string | null;
  signalQuality: number;
  rssiDbm: number | null;
  receiveRateMbps: number | null;
  transmitRateMbps: number | null;
  phyType: string | null;
  authAlgorithm: string | null;
  cipherAlgorithm: string | null;
  security: string;
  detailsAvailable: boolean;
  note: string | null;
}

/** BitLocker on one fixed disk of the server (Win32_EncryptableVolume). */
export interface VolumeEncryption {
  drive: string;
  protection: 'Off' | 'On' | 'Unknown';
  conversion:
    'NotEncrypted' | 'Encrypted' | 'Encrypting' | 'Decrypting' | 'EncryptionPaused' | 'DecryptionPaused' | 'Unknown';
}

/** What the last disk encryption check recorded (SRS SEC-05). `available` is false when BitLocker is not installed. */
export interface DiskEncryptionReport {
  checkedAt: string;
  available: boolean;
  volumes: VolumeEncryption[];
}

/** Whether Administration can change the database password (SRS SEC-07), and when it was last changed. */
export interface DatabasePasswordStatus {
  /** The PostgreSQL account the server signs in as. */
  username: string;
  canChange: boolean;
  /** Why it cannot be changed here, when it cannot. */
  reason: string | null;
  lastChangedAt: string | null;
  lastChangedBy: string | null;
}

/** When a key was last rotated, and by whom. */
export interface KeyRotationEvent {
  at: string;
  by: string | null;
}

/**
 * A data key a rotation added that is no longer current, and what it still encrypts. It can be retired once it encrypts
 * nothing and the current key has been current for a day.
 */
export interface EarlierDataKey {
  keyId: string;
  since: string | null;
  /** Identifiers and secret settings still encrypted with it, moved in the background. */
  values: number;
  /** Documents and scheduled report files, moved in the background. */
  documents: number;
  /** Stored answers to repeated requests: not moved, they expire within a day. */
  expiring: number;
  /** When the current key will have been current for a day, while it has not. */
  retireFrom: string | null;
  canRetire: boolean;
}

/**
 * How often the encryption keys are read again: every 30 seconds while data moves to a new key, so the count goes down,
 * and every 5 minutes while an earlier key waits to become retirable (for its stored answers to expire, or for the
 * current key to have been current a day), so Retire appears on a screen left open.
 */
export function encryptionKeysRefetchInterval(status: EncryptionKeysStatus | undefined): number | false {
  if (!status) {
    return false;
  }
  const { dataKey } = status;
  if (dataKey.valuesToMove + dataKey.documentsToMove > 0) {
    return 30_000;
  }
  return dataKey.earlier.some((key) => !key.canRetire) ? 300_000 : false;
}

/** The server's encryption keys (GET /api/v1/admin/system/keys, SRS SEC-07). */
export interface EncryptionKeysStatus {
  dataKey: {
    keyId: string;
    since: string | null;
    /** Keys in the ring: the original, and one per rotation until it is retired. */
    keys: number;
    /** Values, documents and report files still encrypted with an earlier key, moved in the background. */
    valuesToMove: number;
    documentsToMove: number;
    lastRotation: KeyRotationEvent | null;
    /** The keys rotations added that are no longer current, oldest first. */
    earlier: EarlierDataKey[];
  };
  signingKey: {
    keyId: string;
    since: string | null;
    lastRotation: KeyRotationEvent | null;
  };
}

export interface DatabasePasswordChanged {
  changedAt: string;
  /** The settings that now hold the new password, e.g. ConnectionStrings:Hospital. */
  settingsChanged: string[];
}

export interface ServerInfo {
  machineName: string;
  osDescription: string;
  version: string;
  environment: string;
  /**
   * `all` on a single Server Node; with several, `api` for an API node, and `worker` or `all` for one that may run the
   * jobs (with the job lease, only the one holding it does).
   */
  role: string;
  startedAt: string;
  uptimeHours: number;
  processorCount: number;
  workingSetBytes: number;
  framework: string;
  /** The last check of this server's clock against the national time servers (CERT-In 2022); null before the first. */
  clock?: ClockCheck | null;
  /** With several servers, the server's id; null on a single Server Node. */
  node?: string | null;
  /**
   * With several servers taking turns at the background jobs (the job lease), whether this one runs them now, and since
   * when; null where its role alone decides.
   */
  jobs?: JobNodeState | null;
}

/** Whether a server that takes turns at the background jobs holds the job lease now. */
export interface JobNodeState {
  active: boolean;
  since: string | null;
}

/** How far this server's clock was from a national time server (positive: behind), or why none answered. */
export interface ClockCheck {
  checkedAt: string;
  server: string | null;
  offsetSeconds: number | null;
  problem: string | null;
}

/**
 * One server of several (SCAL-01): what it reports of its own machine, and when it last did. `reporting` is false once it
 * has not for three minutes: it stopped without saying so, or cannot reach the database.
 */
export interface ServerNodeHealth {
  server: ServerInfo;
  network: SystemHealthReport['network'];
  storage: SystemHealthReport['storage'];
  certificate: SystemHealthReport['certificate'];
  power: SystemHealthReport['power'];
  warnings: HealthWarning[];
  reportedAt: string;
  reporting: boolean;
}

export interface SystemHealthReport {
  /** The server that answered; with several, `servers` has every one. */
  server: ServerInfo;
  network: {
    wifiSupported: boolean;
    wifi: WifiConnectionInfo | null;
    connectedSince: string | null;
    lastDisconnectAt: string | null;
    recentEvents: { at: string; kind: string; message: string }[];
    ipAddresses: string[];
    httpsPort: number;
    portalHttpsPort: number;
    mdnsEnabled: boolean;
    /** The network cables up, empty when none is; null when the link monitor has not read them (it is off). */
    cables: NetworkLink[] | null;
    /** The cable the server was on is lost, and none is up again. */
    cableLost: boolean;
    lastCableDropAt: string | null;
  };
  database: {
    reachable: boolean;
    version: string | null;
    sizeBytes: number | null;
    connections: number | null;
    lastMigration: string | null;
    error: string | null;
  };
  storage: {
    dataDirectory: string;
    freeBytes: number | null;
    totalBytes: number | null;
    /** Null when the check has never run on this server, and on servers without Windows. */
    encryption: DiskEncryptionReport | null;
  };
  backup: {
    enabled: boolean;
    lastSuccessAt: string | null;
    lastFailureAt: string | null;
    lastError: string | null;
    lastSizeBytes: number | null;
  };
  outbox: { pending: number; failedLast24Hours: number; oldestPendingAt: string | null };
  certificate: {
    caFingerprint: string | null;
    serverExpiresAt: string | null;
    subjectAlternativeNames: string[];
    /** Null with an administrator-supplied certificate, whose issuer HC360 does not hold. */
    caExpiresAt: string | null;
    /** While a next CA is prepared ('certificates prepare-ca'): that CA, which devices take before the switch. */
    nextCaFingerprint?: string | null;
    /** After 'certificates switch-ca': the CA the current one replaced, until it expires. */
    previousCaFingerprint?: string | null;
    /** When the previous CA expires: devices keep it as a second CA until then. */
    previousCaExpiresAt?: string | null;
  };
  power: { available: boolean; onBattery: boolean | null; chargePercent: number | null; source: string | null };
  /** Empty when the integrations are not loaded (never on a real server). */
  integrations: IntegrationHealthSummary[];
  warnings: HealthWarning[];
  generatedAt: string;
  /**
   * Every server when there are several (SCAL-01), the one that runs the background jobs among them and first; null on a
   * single Server Node.
   */
  servers: ServerNodeHealth[] | null;
}

export interface WifiSample {
  at: string;
  connected: boolean;
  signalQuality: number;
  rssiDbm: number | null;
  receiveRateMbps: number | null;
  ssid: string | null;
}

export type AuditSeverity = 'Info' | 'Warning' | 'Critical';

export interface AuditEventDto {
  id: number;
  occurredAt: string;
  category: string;
  action: string;
  severity: AuditSeverity;
  entityType: string | null;
  entityId: string | null;
  patientId: string | null;
  userId: string | null;
  userName: string | null;
  ipAddress: string | null;
  summary: string | null;
  details: string | null;
  sealed: boolean;
}

export interface AuditFilters {
  from?: string;
  to?: string;
  category?: string;
  action?: string;
  minSeverity?: AuditSeverity;
  search?: string;
  page?: number;
}

export interface AuditVerification {
  valid: boolean;
  checkedCount: number;
  firstInvalidChainIndex: number | null;
  unsealedCount: number;
  message: string | null;
  /** How many anchors kept outside the database the chain was checked against. */
  anchorsChecked: number;
  lastAnchor: AuditAnchor | null;
}

/** Where the audit chain ended at a moment, written outside the database (the anchor file and the server log). */
export interface AuditAnchor {
  chainIndex: number;
  hash: string;
  mac: string | null;
  anchoredAt: string;
}

export interface SettingDto {
  id: string;
  key: string;
  facilityId: string | null;
  value: unknown;
  isSecret: boolean;
  hasValue: boolean;
  description: string | null;
  updatedAt: string | null;
  /** Sent back with a change, so a save over someone else's newer one is refused (409) rather than lost (REL-04). */
  version: number;
  /** A change to it waiting for a second administrator, if any. */
  pending?: PendingSettingChange | null;
}

/** A change to a guarded setting waiting for a second administrator (maker-checker). */
export interface PendingSettingChange {
  approvalId: string;
  /** "Second pathologist for lab corrections: on → off". */
  summary: string;
  requestedBy: string;
  requestedAt: string;
}

/**
 * What saving a setting would do, before it is saved (`POST /api/v1/admin/settings/{key}/check`). A change that loosens
 * a control (switched off, a limit raised, a value cleared), or changes where money, patients' data or alerts go, waits
 * for a second administrator; the screens ask for a reason only then.
 */
export interface SettingChangeCheck {
  waits: boolean;
  /** The setting's name, as the approver reads it. */
  setting: string;
  /** What it controls; null for a setting that is not guarded. */
  controls: string | null;
  from: string;
  to: string;
}

/** A setting saved as the settings API takes it (`PUT /api/v1/admin/settings/{key}`). */
export interface SaveSettingBody {
  value: unknown;
  facilityId: string | null;
  secret: boolean;
  description: string | null;
  /** The version the value was shown at; none for a value not set (REL-04). */
  version?: number;
  /** Why, for the administrator who decides a change that waits: required then, not kept otherwise. */
  reason?: string | null;
}

export const settingsApi = {
  check: (key: string, body: SaveSettingBody) =>
    api.post<SettingChangeCheck>(`/api/v1/admin/settings/${encodeURIComponent(key)}/check`, body),
  /** Saved at once (nothing back), or asked for (202: the request a second administrator decides). */
  save: (key: string, body: SaveSettingBody) =>
    api.put<ApprovalRequestDto | undefined>(`/api/v1/admin/settings/${encodeURIComponent(key)}`, body),
};

export interface BackupRunDto {
  id: string;
  startedAt: string;
  completedAt: string | null;
  kind: 'Scheduled' | 'Manual' | 'PreMigration';
  status: 'Running' | 'Succeeded' | 'Failed';
  fileName: string | null;
  sizeBytes: number | null;
  sha256: string | null;
  secondaryCopied: boolean;
  error: string | null;
  /**
   * 1: the database only (older backups). 2: the database, the field encryption key and, for full backups, the documents.
   * 3: the same, in a file that names its recovery key.
   */
  formatVersion: number;
  /** Documents in the backup; null when it holds the database only (older backups, and those taken before an update). */
  documentCount: number | null;
  /** When the finished file was read back in full and checked; null for older backups. */
  verifiedAt: string | null;
  /** The id of the recovery key that opens the backup; null for backups made before backups named it (the original key). */
  recoveryKeyId: string | null;
}

/** Which recovery key new backups use. The key itself is shown once only, when it is made. */
export interface RecoveryKeyStatus {
  keyId: string;
  /** Backups still use the original recovery key, which comes from the original data key. */
  original: boolean;
  /** Since when this server has used the key; null for the original key. */
  since: string | null;
  by: string | null;
  /** For the original key: whether it was ever displayed, when administrators still could. If not, nobody holds it. */
  originalShown: boolean;
  /** The original key's id, which the backups made with it name. */
  originalKeyId: string;
}

/** A new recovery key, shown this once. It replaces the current key only when it is typed back before `expiresAt`. */
export interface NewRecoveryKey {
  keyId: string;
  /** 14 groups of 4 letters and digits. */
  key: string;
  expiresAt: string;
}

/** The recovery key a backup opens with, in words for the backup list. */
export function backupKeyText(keyId: string | null, originalKeyId: string | undefined): string {
  return keyId === null || keyId === originalKeyId
    ? 'Opens with the original recovery key'
    : `Opens with recovery key ${keyId}`;
}

/** What a backup holds, in plain words, for the backup list. */
export function backupContents(run: Pick<BackupRunDto, 'status' | 'formatVersion' | 'documentCount'>): string {
  if (run.status !== 'Succeeded') {
    return '—';
  }
  if (run.formatVersion < 2) {
    return 'Database only (older backup)';
  }
  if (run.documentCount === null) {
    return 'Database and key (before an update)';
  }
  return `Database, key and ${run.documentCount.toLocaleString('en-IN')} ${run.documentCount === 1 ? 'document' : 'documents'}`;
}

export const adminQueries = {
  users: (params: { search?: string; page: number; active?: boolean; roleId?: string }) =>
    queryOptions({
      queryKey: ['admin', 'users', params],
      queryFn: ({ signal }) =>
        api.get<PagedResult<UserListItem>>('/api/v1/admin/users', { query: { ...params, pageSize: 25 }, signal }),
      placeholderData: keepPreviousData,
    }),
  user: (id: string) =>
    queryOptions({
      queryKey: ['admin', 'user', id],
      queryFn: ({ signal }) => api.get<UserDetail>(`/api/v1/admin/users/${id}`, { signal }),
    }),
  roles: () =>
    queryOptions({
      queryKey: ['admin', 'roles'],
      queryFn: ({ signal }) => api.get<RoleListItem[]>('/api/v1/admin/roles', { signal }),
    }),
  role: (id: string) =>
    queryOptions({
      queryKey: ['admin', 'role', id],
      queryFn: ({ signal }) => api.get<RoleDetail>(`/api/v1/admin/roles/${id}`, { signal }),
    }),
  permissions: () =>
    queryOptions({
      queryKey: ['admin', 'permissions'],
      queryFn: ({ signal }) => api.get<PermissionGroup[]>('/api/v1/admin/permissions', { signal }),
      staleTime: 30 * 60_000,
    }),
  devices: () =>
    queryOptions({
      queryKey: ['admin', 'devices'],
      queryFn: ({ signal }) => api.get<DeviceDto[]>('/api/v1/admin/devices', { signal }),
    }),
  /** My enrolment codes for administration desktops, waiting for approval or ready to be shown. */
  enrollmentRequests: (poll = false) =>
    queryOptions({
      queryKey: ['admin', 'devices', 'enrollment-requests'],
      queryFn: ({ signal }) =>
        api.get<EnrollmentCodeRequest[]>('/api/v1/admin/devices/enrollment-codes/requests', { signal }),
      refetchInterval: poll ? 10_000 : false,
    }),
  desktopUpdate: () =>
    queryOptions({
      queryKey: ['admin', 'devices', 'desktop-update'],
      queryFn: ({ signal }) => api.get<DesktopUpdateState>('/api/v1/admin/devices/desktop-update', { signal }),
    }),
  health: () =>
    queryOptions({
      queryKey: ['admin', 'health'],
      queryFn: ({ signal }) => api.get<SystemHealthReport>('/api/v1/admin/system/health', { signal }),
      refetchInterval: 30_000,
    }),
  databasePassword: () =>
    queryOptions({
      queryKey: ['admin', 'database-password'],
      queryFn: ({ signal }) => api.get<DatabasePasswordStatus>('/api/v1/admin/system/database-password', { signal }),
    }),
  recoveryKey: () =>
    queryOptions({
      queryKey: ['admin', 'backups', 'recovery-key'],
      queryFn: ({ signal }) => api.get<RecoveryKeyStatus>('/api/v1/admin/backups/recovery-key', { signal }),
    }),
  encryptionKeys: () =>
    queryOptions({
      queryKey: ['admin', 'encryption-keys'],
      queryFn: ({ signal }) => api.get<EncryptionKeysStatus>('/api/v1/admin/system/keys', { signal }),
      refetchInterval: (query) => encryptionKeysRefetchInterval(query.state.data),
    }),
  wifiSamples: (hours: number) =>
    queryOptions({
      queryKey: ['admin', 'wifi-samples', hours],
      queryFn: ({ signal }) => api.get<WifiSample[]>('/api/v1/admin/system/wifi-samples', { query: { hours }, signal }),
      refetchInterval: 60_000,
    }),
  audit: (filters: AuditFilters) =>
    queryOptions({
      queryKey: ['admin', 'audit', filters],
      queryFn: ({ signal }) =>
        api.get<PagedResult<AuditEventDto>>('/api/v1/admin/audit', { query: { ...filters, pageSize: 50 }, signal }),
      placeholderData: keepPreviousData,
    }),
  settings: () =>
    queryOptions({
      queryKey: ['admin', 'settings'],
      queryFn: ({ signal }) => api.get<SettingDto[]>('/api/v1/admin/settings', { signal }),
    }),
  backups: () =>
    queryOptions({
      queryKey: ['admin', 'backups'],
      queryFn: ({ signal }) => api.get<BackupRunDto[]>('/api/v1/admin/backups', { signal }),
      refetchInterval: (query) => (query.state.data?.some((run) => run.status === 'Running') ? 5_000 : 60_000),
    }),
};

export function formatBytes(bytes: number | null | undefined): string {
  if (bytes === null || bytes === undefined) {
    return '—';
  }
  const units = ['B', 'KB', 'MB', 'GB', 'TB'];
  let value = bytes;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit++;
  }
  return `${value.toFixed(value >= 10 || unit === 0 ? 0 : 1)} ${units[unit]}`;
}

/**
 * Gives the server's database account a new random password, needing `admin.system.manage`. 422 when it cannot be
 * changed here (`database.password_none`, `database.password_not_windows`, `database.password_elsewhere`), when the
 * change failed and the old password stays (`database.password_not_changed`), and when the database could not be reached
 * to confirm a failed commit, so the old password was kept (`database.password_unconfirmed`).
 */
export function changeDatabasePassword() {
  return api.post<DatabasePasswordChanged>('/api/v1/admin/system/database-password/change');
}

/**
 * Makes a new backup recovery key and returns it, this once, after the password is given again (400 with a `password`
 * field error when it is wrong). Needs `admin.system.manage`.
 */
export function createRecoveryKey(password: string) {
  return api.post<NewRecoveryKey>('/api/v1/admin/backups/recovery-key', { password });
}

/**
 * Makes the new recovery key current once it is typed back from the administrator's copy: 400 with a `key` field error
 * when it does not match, 422 `backup.recovery_key_not_waiting` once it has expired (30 minutes) or was replaced.
 */
export function confirmRecoveryKey(keyId: string, key: string) {
  return api.post<RecoveryKeyStatus>(`/api/v1/admin/backups/recovery-key/${encodeURIComponent(keyId)}/confirm`, {
    key,
  });
}

/** Makes a new data encryption key current; data encrypted before moves to it in the background. Needs `admin.system.manage`. */
export function rotateDataKey() {
  return api.post<EncryptionKeysStatus>('/api/v1/admin/system/keys/data/rotate');
}

/**
 * Deletes an earlier data key from the server once nothing is encrypted with it (ASVS V11.1.1), after the password is
 * given again: 400 with a `password` field error when it is wrong, 422 `keys.in_use` or `keys.rotated_recently` while it
 * cannot be retired yet. Needs `admin.system.manage`.
 */
export function retireDataKey(keyId: string, password: string) {
  return api.post<EncryptionKeysStatus>(`/api/v1/admin/system/keys/data/${encodeURIComponent(keyId)}/retire`, {
    password,
  });
}

/** Makes a new token signing key current; tokens already issued stay valid until they expire. Needs `admin.system.manage`. */
export function rotateSigningKey() {
  return api.post<EncryptionKeysStatus>('/api/v1/admin/system/keys/signing/rotate');
}
