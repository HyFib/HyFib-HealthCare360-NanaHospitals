import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type * as AdminApi from './api';
import type { ServerNodeHealth, SystemHealthReport } from './api';
import { SystemHealthWidget } from './SystemHealthWidget';

// The server is outside the test: the widget's own rendering of the report is what is tested.
const readHealth = vi.hoisted(() => vi.fn());
vi.mock('./api', async (importOriginal) => {
  const actual = await importOriginal<typeof AdminApi>();
  return {
    ...actual,
    adminQueries: {
      ...actual.adminQueries,
      health: () => ({ queryKey: ['admin', 'health'], queryFn: () => readHealth() }),
    },
  };
});
vi.mock('@tanstack/react-router', () => ({ useNavigate: () => vi.fn() }));

/** An API node of several, as the API reports it: its WiFi never monitored, since only the job node watches its own. */
function report(servers: ServerNodeHealth[] | null): SystemHealthReport {
  const server = {
    machineName: 'HC360-API1',
    osDescription: 'Microsoft Windows 10.0.20348',
    version: '1.4.0',
    environment: 'Production',
    role: servers ? 'api' : 'all',
    startedAt: '2026-10-20T04:00:00Z',
    uptimeHours: 24,
    processorCount: 8,
    workingSetBytes: 800_000_000,
    framework: '.NET 10.0.12',
  };
  return {
    server,
    network: {
      wifiSupported: true,
      wifi: null,
      connectedSince: null,
      lastDisconnectAt: null,
      recentEvents: [],
      ipAddresses: ['10.0.0.11'],
      httpsPort: 7360,
      portalHttpsPort: 7361,
      mdnsEnabled: false,
    },
    database: {
      reachable: true,
      version: 'PostgreSQL 16.4',
      sizeBytes: 1,
      connections: 4,
      lastMigration: 'x',
      error: null,
    },
    storage: { dataDirectory: 'C:\\ProgramData\\HyFib\\HealthCare360', freeBytes: 2, totalBytes: 4, encryption: null },
    backup: {
      enabled: true,
      lastSuccessAt: '2026-10-21T01:30:00Z',
      lastFailureAt: null,
      lastError: null,
      lastSizeBytes: 1,
    },
    outbox: { pending: 0, failedLast24Hours: 0, oldestPendingAt: null },
    certificate: { caFingerprint: 'AB:CD', serverExpiresAt: null, subjectAlternativeNames: [], caExpiresAt: null },
    power: { available: true, onBattery: false, chargePercent: 100, source: 'UPS' },
    integrations: [],
    warnings: [],
    generatedAt: '2026-10-21T06:00:00Z',
    servers,
  };
}

function node(machineName: string, role: string, reporting = true): ServerNodeHealth {
  const { network, storage, certificate, power, server } = report(null);
  return {
    server: { ...server, machineName, role },
    network,
    storage,
    certificate,
    power,
    warnings: [],
    reportedAt: '2026-10-21T05:59:00Z',
    reporting,
  };
}

function renderWidget() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={client}>
      <FluentProvider theme={webLightTheme}>
        <SystemHealthWidget />
      </FluentProvider>
    </QueryClientProvider>,
  );
}

describe('SystemHealthWidget', () => {
  beforeEach(() => readHealth.mockReset());

  it('shows the server machine on a single Server Node, as it always did', async () => {
    readHealth.mockResolvedValue(report(null));
    renderWidget();

    expect(await screen.findByText('WiFi')).toBeInTheDocument();
    expect(screen.getByText('Server machine')).toBeInTheDocument();
    expect(screen.getByText('Power')).toBeInTheDocument();
    expect(screen.queryByText('Servers')).not.toBeInTheDocument();
  });

  it('says which of several servers no longer reports, the one that runs the jobs included', async () => {
    readHealth.mockResolvedValue(
      report([node('HC360-JOBS', 'worker', false), node('HC360-API1', 'api'), node('HC360-API2', 'api')]),
    );
    renderWidget();

    expect(await screen.findByText('HC360-JOBS not reporting')).toBeInTheDocument();
    expect(screen.getAllByText('Servers')).not.toHaveLength(0);
    // Not the WiFi and power of whichever API node answered: an API node does not watch its WiFi.
    expect(screen.queryByText('WiFi')).not.toBeInTheDocument();
    expect(screen.queryByText('Disconnected')).not.toBeInTheDocument();
    expect(screen.queryByText('Power')).not.toBeInTheDocument();
  });

  it('says all of several servers report', async () => {
    readHealth.mockResolvedValue(report([node('HC360-JOBS', 'worker'), node('HC360-API1', 'api')]));
    renderWidget();

    expect(await screen.findByText('All 2 reporting')).toBeInTheDocument();
  });
});
