import type {
  DatabasePasswordStatus,
  DeviceDto,
  EncryptionKeysStatus,
  SystemHealthReport,
  WifiSample,
} from '@/modules/admin/api';
import type { FakeAnswers } from '../../fakeApi';

// System health of the server machine with canned data: a link that dipped overnight, a backup that failed once,
// integrations in each state, and the warnings the server raises for them.

const now = Date.now();
const hoursAgo = (hours: number) => new Date(now - hours * 3600_000).toISOString();
const GB = 1024 ** 3;

export const SYSTEM_HEALTH: SystemHealthReport = {
  server: {
    machineName: 'HC360-SRV01',
    osDescription: 'Microsoft Windows 11 Pro 10.0.26100',
    version: '1.0.0',
    environment: 'Production',
    role: 'Primary',
    startedAt: hoursAgo(72),
    uptimeHours: 72,
    processorCount: 8,
    workingSetBytes: 1.4 * GB,
    framework: '.NET 10.0',
  },
  network: {
    wifiSupported: true,
    wifi: {
      interfaceDescription: 'Intel(R) Wi-Fi 6E AX211 160MHz',
      isConnected: true,
      ssid: 'NANA-CLINICAL',
      bssid: '7c:21:0e:4a:9b:10',
      signalQuality: 78,
      rssiDbm: -58,
      receiveRateMbps: 573,
      transmitRateMbps: 480,
      phyType: '802.11ax',
      authAlgorithm: 'WPA3-Enterprise',
      cipherAlgorithm: 'GCMP-256',
      security: 'WPA3-Enterprise',
      detailsAvailable: true,
      note: null,
    },
    connectedSince: hoursAgo(9),
    lastDisconnectAt: hoursAgo(9.1),
    recentEvents: [
      { at: hoursAgo(9.1), kind: 'Disconnected', message: 'Lost NANA-CLINICAL (signal 18%).' },
      { at: hoursAgo(9), kind: 'Connected', message: 'Connected to NANA-CLINICAL.' },
    ],
    ipAddresses: ['192.168.10.5', 'fe80::1c2b:3d4e:5f60:7182'],
    httpsPort: 7360,
    portalHttpsPort: 7361,
    mdnsEnabled: true,
  },
  database: {
    reachable: true,
    version: 'PostgreSQL 16.4',
    sizeBytes: 38 * GB,
    connections: 42,
    lastMigration: '20261010093000_ProcedureRecords',
    error: null,
  },
  storage: {
    dataDirectory: 'D:\\HC360\\data',
    freeBytes: 310 * GB,
    totalBytes: 931 * GB,
    encryption: {
      checkedAt: hoursAgo(5),
      available: true,
      volumes: [
        { drive: 'C:', protection: 'On', conversion: 'Encrypted' },
        { drive: 'D:', protection: 'On', conversion: 'Encrypted' },
      ],
    },
  },
  backup: {
    enabled: true,
    lastSuccessAt: hoursAgo(3),
    lastFailureAt: hoursAgo(27),
    lastError: 'The backup disk was not connected.',
    lastSizeBytes: 6.2 * GB,
  },
  outbox: { pending: 3, failedLast24Hours: 1, oldestPendingAt: hoursAgo(0.2) },
  certificate: {
    caFingerprint: 'SHA256:4F:2A:91:0C:7E:55:B3:18:6D:E2:0A:99:C4:71:3B:8F',
    serverExpiresAt: new Date(now + 20 * 24 * 3600_000).toISOString(),
    subjectAlternativeNames: ['hc360.local', 'HC360-SRV01', '192.168.10.5'],
    caExpiresAt: new Date(now + 9 * 365 * 24 * 3600_000).toISOString(),
  },
  power: { available: true, onBattery: false, chargePercent: 100, source: 'APC Back-UPS 1500' },
  integrations: [
    {
      name: 'sms',
      displayName: 'SMS (TRAI DLT)',
      enabled: true,
      configured: true,
      health: 'healthy',
      lastSuccessAt: hoursAgo(0.1),
      lastFailureAt: null,
      failuresLast24Hours: 0,
    },
    {
      name: 'payments',
      displayName: 'Payment gateway',
      enabled: true,
      configured: true,
      health: 'degraded',
      lastSuccessAt: hoursAgo(1),
      lastFailureAt: hoursAgo(0.5),
      failuresLast24Hours: 4,
    },
    {
      name: 'abdm',
      displayName: 'ABDM (ABHA)',
      enabled: true,
      configured: true,
      health: 'failing',
      lastSuccessAt: hoursAgo(30),
      lastFailureAt: hoursAgo(0.3),
      failuresLast24Hours: 18,
    },
    {
      name: 'whatsapp',
      displayName: 'WhatsApp',
      enabled: false,
      configured: false,
      health: 'not_configured',
      lastSuccessAt: null,
      lastFailureAt: null,
      failuresLast24Hours: 0,
    },
  ],
  warnings: [
    { code: 'certificate.expiring', severity: 'warning', message: 'The server certificate expires in 20 days.' },
    { code: 'integration.failing', severity: 'critical', message: 'ABDM (ABHA) has failed 18 times today.' },
  ],
  generatedAt: hoursAgo(0.01),
  servers: null,
};

/** The server's WiFi signal every half hour over the last day, with a dip overnight. */
export const WIFI_SAMPLES: WifiSample[] = Array.from({ length: 48 }, (_, index) => {
  const hours = 24 - index / 2;
  const dip = hours > 8.5 && hours < 10;
  return {
    at: hoursAgo(hours),
    connected: !dip || hours > 9.05,
    signalQuality: dip ? 18 : 72 + (index % 5) * 2,
    rssiDbm: dip ? -86 : -60 + (index % 3),
    receiveRateMbps: dip ? 24 : 540,
    ssid: 'NANA-CLINICAL',
  };
});

export const DATABASE_PASSWORD: DatabasePasswordStatus = {
  username: 'hc360',
  canChange: true,
  reason: null,
  lastChangedAt: hoursAgo(24 * 40),
  lastChangedBy: 'Dr. Meera Iyer',
};

/** A data key rotated yesterday, with documents still moving to it from the earlier key. */
export const ENCRYPTION_KEYS: EncryptionKeysStatus = {
  dataKey: {
    keyId: 'dk-2026-10',
    since: hoursAgo(20),
    keys: 2,
    valuesToMove: 0,
    documentsToMove: 140,
    lastRotation: { at: hoursAgo(20), by: 'Dr. Meera Iyer' },
    earlier: [
      {
        keyId: 'dk-2026-01',
        since: hoursAgo(24 * 280),
        values: 0,
        documents: 140,
        expiring: 12,
        retireFrom: new Date(now + 4 * 3600_000).toISOString(),
        canRetire: false,
      },
    ],
  },
  signingKey: { keyId: 'sk-2026-01', since: hoursAgo(24 * 280), lastRotation: null },
};

export const SYSTEM_HEALTH_ANSWERS: FakeAnswers = {
  'GET /api/v1/admin/system/health': SYSTEM_HEALTH,
  'GET /api/v1/admin/system/wifi-samples': WIFI_SAMPLES,
  'GET /api/v1/admin/system/database-password': DATABASE_PASSWORD,
  'GET /api/v1/admin/system/keys': ENCRYPTION_KEYS,
};

// The devices page of a hospital enrolling its first workstation: a queue display and a lab PC, no workstation yet.

export const DEVICES: DeviceDto[] = [
  {
    id: '0192a000-0000-7000-8000-0000000000e1',
    name: 'OPD queue display',
    machineName: 'OPD-TV-01',
    kind: 'DisplayBoard',
    osVersion: 'Windows 11 IoT Enterprise',
    appVersion: '1.0.0',
    enrolledAt: hoursAgo(240),
    lastSeenAt: hoursAgo(0.1),
    lastIpAddress: '192.168.10.40',
    lastWifiSsid: 'NANA-CLINICAL',
    lastWifiSignal: 81,
    isRevoked: false,
    facilityId: null,
    departmentId: null,
    location: 'OPD waiting hall',
    version: 4,
  },
  {
    id: '0192a000-0000-7000-8000-0000000000e2',
    name: 'Haematology PC',
    machineName: 'LAB-PC-02',
    kind: 'LabAgent',
    osVersion: 'Windows 10 Pro',
    appVersion: '1.0.0',
    enrolledAt: hoursAgo(200),
    lastSeenAt: hoursAgo(0.2),
    lastIpAddress: '192.168.10.52',
    lastWifiSsid: null,
    lastWifiSignal: null,
    isRevoked: false,
    facilityId: null,
    departmentId: null,
    location: 'Laboratory',
    version: 2,
  },
];

export const DEVICES_ANSWERS: FakeAnswers = {
  'GET /api/v1/admin/devices': DEVICES,
  'GET /api/v1/admin/devices/desktop-update': { offeredVersion: null, releasedAt: null, problem: null },
};
