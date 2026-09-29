import type { PagedResult } from '@hc360/core';
import { keepPreviousData, queryOptions } from '@tanstack/react-query';
import { api } from '@/lib/api';

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
}

export interface PermissionGroup {
  group: string;
  permissions: { name: string; description: string }[];
}

export type DeviceKind = 'Workstation' | 'Kiosk' | 'DisplayBoard' | 'Tablet';

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

export interface HealthWarning {
  code: string;
  severity: 'info' | 'warning' | 'critical' | string;
  message: string;
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

export interface SystemHealthReport {
  server: {
    machineName: string;
    osDescription: string;
    version: string;
    environment: string;
    role: string;
    startedAt: string;
    uptimeHours: number;
    processorCount: number;
    workingSetBytes: number;
    framework: string;
  };
  network: {
    wifiSupported: boolean;
    wifi: WifiConnectionInfo | null;
    connectedSince: string | null;
    lastDisconnectAt: string | null;
    recentEvents: { at: string; kind: string; message: string }[];
    ipAddresses: string[];
    httpsPort: number;
    mdnsEnabled: boolean;
  };
  database: { reachable: boolean; version: string | null; sizeBytes: number | null; connections: number | null; lastMigration: string | null; error: string | null };
  storage: { dataDirectory: string; freeBytes: number | null; totalBytes: number | null };
  backup: { enabled: boolean; lastSuccessAt: string | null; lastFailureAt: string | null; lastError: string | null; lastSizeBytes: number | null };
  outbox: { pending: number; failedLast24Hours: number; oldestPendingAt: string | null };
  certificate: { caFingerprint: string | null; serverExpiresAt: string | null; subjectAlternativeNames: string[] };
  power: { available: boolean; onBattery: boolean | null; chargePercent: number | null; source: string | null };
  warnings: HealthWarning[];
  generatedAt: string;
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
}

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
  /** 1: the database only (older backups). 2: the database, the field encryption key and, for full backups, the documents. */
  formatVersion: number;
  /** Documents in the backup; null when it holds the database only (older backups, and those taken before an update). */
  documentCount: number | null;
  /** When the finished file was read back in full and checked; null for older backups. */
  verifiedAt: string | null;
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
      queryFn: ({ signal }) => api.get<PagedResult<UserListItem>>('/api/v1/admin/users', { query: { ...params, pageSize: 25 }, signal }),
      placeholderData: keepPreviousData,
    }),
  user: (id: string) =>
    queryOptions({ queryKey: ['admin', 'user', id], queryFn: ({ signal }) => api.get<UserDetail>(`/api/v1/admin/users/${id}`, { signal }) }),
  roles: () => queryOptions({ queryKey: ['admin', 'roles'], queryFn: ({ signal }) => api.get<RoleListItem[]>('/api/v1/admin/roles', { signal }) }),
  role: (id: string) => queryOptions({ queryKey: ['admin', 'role', id], queryFn: ({ signal }) => api.get<RoleDetail>(`/api/v1/admin/roles/${id}`, { signal }) }),
  permissions: () =>
    queryOptions({
      queryKey: ['admin', 'permissions'],
      queryFn: ({ signal }) => api.get<PermissionGroup[]>('/api/v1/admin/permissions', { signal }),
      staleTime: 30 * 60_000,
    }),
  devices: () => queryOptions({ queryKey: ['admin', 'devices'], queryFn: ({ signal }) => api.get<DeviceDto[]>('/api/v1/admin/devices', { signal }) }),
  health: () =>
    queryOptions({
      queryKey: ['admin', 'health'],
      queryFn: ({ signal }) => api.get<SystemHealthReport>('/api/v1/admin/system/health', { signal }),
      refetchInterval: 30_000,
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
      queryFn: ({ signal }) => api.get<PagedResult<AuditEventDto>>('/api/v1/admin/audit', { query: { ...filters, pageSize: 50 }, signal }),
      placeholderData: keepPreviousData,
    }),
  settings: () => queryOptions({ queryKey: ['admin', 'settings'], queryFn: ({ signal }) => api.get<SettingDto[]>('/api/v1/admin/settings', { signal }) }),
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
