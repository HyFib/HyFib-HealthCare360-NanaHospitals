import {
  Button,
  Caption1,
  makeStyles,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  Select,
  Text,
  tokens,
} from '@fluentui/react-components';
import { ArrowClockwiseRegular, ArrowDownloadRegular } from '@fluentui/react-icons';
import { formatDateTime, formatRelative, formatTime, humanize } from '@hc360/core';
import { ErrorState, LoadingState, Page, PageHeader, Section } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute } from '@tanstack/react-router';
import { useMemo, useState, type ReactNode } from 'react';
import { EChart, type ChartOption } from '@/lib/EChart';
import { adminQueries, formatBytes, type SystemHealthReport, type WifiSample } from '@/modules/admin/api';
import { encryptionSummary } from '@/modules/admin/diskEncryption';

export const Route = createFileRoute('/_app/admin/system')({
  component: SystemHealthPage,
});

const useStyles = makeStyles({
  grid: {
    display: 'grid',
    gap: '16px',
    gridTemplateColumns: 'repeat(auto-fill, minmax(320px, 1fr))',
    alignItems: 'start',
  },
  rows: { display: 'grid', gridTemplateColumns: 'minmax(120px, auto) 1fr', gap: '6px 16px' },
  label: { color: tokens.colorNeutralForeground3 },
  bad: { color: tokens.colorPaletteRedForeground1, fontWeight: tokens.fontWeightSemibold },
  warn: { color: tokens.colorPaletteMarigoldForeground1, fontWeight: tokens.fontWeightSemibold },
  ok: { color: tokens.colorPaletteGreenForeground1, fontWeight: tokens.fontWeightSemibold },
  mono: { fontFamily: tokens.fontFamilyMonospace, wordBreak: 'break-all' },
  warnings: { display: 'flex', flexDirection: 'column', gap: '8px' },
  events: { display: 'flex', flexDirection: 'column', gap: '4px', maxHeight: '200px', overflowY: 'auto' },
});

function Row({ label, children, tone }: { label: string; children: ReactNode; tone?: 'ok' | 'warn' | 'bad' }) {
  const styles = useStyles();
  return (
    <>
      <Caption1 className={styles.label}>{label}</Caption1>
      <Text className={tone ? styles[tone] : undefined}>{children ?? '—'}</Text>
    </>
  );
}

/** BitLocker on the server's fixed disks, as the last daily check recorded it (SRS SEC-05). */
function EncryptionRow({ report }: { report: NonNullable<SystemHealthReport['storage']['encryption']> }) {
  const styles = useStyles();
  const summary = encryptionSummary(report);
  return (
    <Row label="BitLocker" tone={summary.tone}>
      {summary.text}
      <Caption1 block className={styles.label}>
        Checked {formatDateTime(report.checkedAt)}
      </Caption1>
    </Row>
  );
}

/** Health of the dedicated WiFi server machine (SRS DED-13): link quality, database, disk, backups, certificate, UPS. */
function SystemHealthPage() {
  const styles = useStyles();
  const health = useQuery(adminQueries.health());
  const [hours, setHours] = useState(24);
  const samples = useQuery(adminQueries.wifiSamples(hours));

  if (health.isLoading) {
    return <LoadingState label="Checking the server…" />;
  }
  if (health.error || !health.data) {
    return (
      <Page maxWidth={900}>
        <ErrorState error={health.error} onRetry={() => void health.refetch()} />
      </Page>
    );
  }

  const h = health.data;
  return (
    <Page>
      <PageHeader
        title="System health"
        subtitle={`${h.server.machineName} · checked ${formatRelative(h.generatedAt)}`}
        actions={
          <>
            <Button
              icon={<ArrowDownloadRegular />}
              onClick={() => window.open('/api/v1/system/ca-certificate', '_blank')}
            >
              Hospital CA certificate
            </Button>
            <Button icon={<ArrowClockwiseRegular />} onClick={() => void health.refetch()} disabled={health.isFetching}>
              Refresh
            </Button>
          </>
        }
      />

      {h.warnings.length > 0 && (
        <div className={styles.warnings}>
          {h.warnings.map((w) => (
            <MessageBar
              key={w.code}
              intent={w.severity === 'critical' ? 'error' : w.severity === 'warning' ? 'warning' : 'info'}
              layout="multiline"
            >
              <MessageBarBody>
                <MessageBarTitle>{humanize(w.code.replace(/[._]/g, ' '))}</MessageBarTitle>
                {w.message}
              </MessageBarBody>
            </MessageBar>
          ))}
        </div>
      )}

      <Section
        title="WiFi signal of the server"
        description="The server should stay above 60% signal. Dips here explain slow screens on the wards."
        actions={
          <Select value={String(hours)} onChange={(_, d) => setHours(Number(d.value))} aria-label="Period">
            <option value="6">Last 6 hours</option>
            <option value="24">Last 24 hours</option>
            <option value="168">Last 7 days</option>
          </Select>
        }
      >
        {samples.data && samples.data.length > 0 ? (
          <WifiChart samples={samples.data} />
        ) : (
          <Caption1 className={styles.label}>
            {samples.isLoading ? 'Loading…' : 'No WiFi samples yet (wired connection, or monitoring just started).'}
          </Caption1>
        )}
      </Section>

      <div className={styles.grid}>
        <NetworkCard h={h} />
        <Section title="Database">
          <div className={styles.rows}>
            <Row label="Status" tone={h.database.reachable ? 'ok' : 'bad'}>
              {h.database.reachable ? 'Online' : `Not reachable${h.database.error ? `: ${h.database.error}` : ''}`}
            </Row>
            <Row label="Version">{h.database.version}</Row>
            <Row label="Size">{formatBytes(h.database.sizeBytes)}</Row>
            <Row label="Connections">{h.database.connections}</Row>
            <Row label="Schema">{h.database.lastMigration}</Row>
          </div>
        </Section>
        <Section title="Storage">
          <div className={styles.rows}>
            <Row label="Data folder">
              <span className={styles.mono}>{h.storage.dataDirectory}</span>
            </Row>
            <Row
              label="Free space"
              tone={
                h.storage.freeBytes !== null &&
                h.storage.totalBytes !== null &&
                h.storage.freeBytes / h.storage.totalBytes < 0.1
                  ? 'bad'
                  : undefined
              }
            >
              {formatBytes(h.storage.freeBytes)} of {formatBytes(h.storage.totalBytes)}
            </Row>
            {h.storage.encryption && <EncryptionRow report={h.storage.encryption} />}
          </div>
        </Section>
        <Section title="Backups">
          <div className={styles.rows}>
            <Row label="Automatic" tone={h.backup.enabled ? 'ok' : 'warn'}>
              {h.backup.enabled ? 'On' : 'Off'}
            </Row>
            <Row label="Last success" tone={h.backup.lastSuccessAt ? undefined : 'warn'}>
              {h.backup.lastSuccessAt
                ? `${formatDateTime(h.backup.lastSuccessAt)} (${formatBytes(h.backup.lastSizeBytes)})`
                : 'Never'}
            </Row>
            {h.backup.lastFailureAt && (
              <Row label="Last failure" tone="bad">
                {formatDateTime(h.backup.lastFailureAt)}
                {h.backup.lastError ? `: ${h.backup.lastError}` : ''}
              </Row>
            )}
          </div>
        </Section>
        <Section title="Messages waiting to send">
          <div className={styles.rows}>
            <Row label="Pending" tone={h.outbox.pending > 100 ? 'warn' : undefined}>
              {h.outbox.pending}
            </Row>
            <Row label="Failed (24 h)" tone={h.outbox.failedLast24Hours > 0 ? 'warn' : undefined}>
              {h.outbox.failedLast24Hours}
            </Row>
            <Row label="Oldest">{h.outbox.oldestPendingAt ? formatRelative(h.outbox.oldestPendingAt) : 'None'}</Row>
          </div>
        </Section>
        <Section title="Security certificate">
          <div className={styles.rows}>
            <Row label="CA fingerprint">
              <span className={styles.mono}>{h.certificate.caFingerprint}</span>
            </Row>
            <Row label="Server certificate expires">
              {h.certificate.serverExpiresAt ? formatDateTime(h.certificate.serverExpiresAt) : '—'}
            </Row>
            <Row label="Valid for">{h.certificate.subjectAlternativeNames.join(', ')}</Row>
          </div>
        </Section>
        <Section title="Power">
          <div className={styles.rows}>
            {h.power.available ? (
              <>
                <Row label="Source" tone={h.power.onBattery ? 'bad' : 'ok'}>
                  {h.power.onBattery ? 'UPS battery' : 'Mains'}
                </Row>
                <Row label="Battery">{h.power.chargePercent !== null ? `${h.power.chargePercent}%` : '—'}</Row>
              </>
            ) : (
              <Row label="UPS">Not detected. Connect the UPS by USB so the server can report power cuts.</Row>
            )}
          </div>
        </Section>
        <Section title="Server">
          <div className={styles.rows}>
            <Row label="Machine">{h.server.machineName}</Row>
            <Row label="Windows">{h.server.osDescription}</Row>
            <Row label="HC360 version">{h.server.version}</Row>
            <Row label="Mode">
              {humanize(h.server.environment)} · {h.server.role}
            </Row>
            <Row label="Running for">
              {h.server.uptimeHours < 48
                ? `${Math.round(h.server.uptimeHours)} hours`
                : `${Math.round(h.server.uptimeHours / 24)} days`}
            </Row>
            <Row label="Memory used">{formatBytes(h.server.workingSetBytes)}</Row>
            <Row label="Processors">{h.server.processorCount}</Row>
          </div>
        </Section>
      </div>
    </Page>
  );
}

function NetworkCard({ h }: { h: SystemHealthReport }) {
  const styles = useStyles();
  const wifi = h.network.wifi;
  const signalTone = wifi?.detailsAvailable
    ? wifi.signalQuality < 40
      ? 'bad'
      : wifi.signalQuality < 60
        ? 'warn'
        : 'ok'
    : undefined;
  return (
    <Section title="Network">
      <div className={styles.rows}>
        {!h.network.wifiSupported ? (
          <Row label="Connection">Wired (no WiFi adapter)</Row>
        ) : wifi ? (
          <>
            <Row label="WiFi" tone={wifi.isConnected ? 'ok' : 'bad'}>
              {wifi.isConnected ? (wifi.ssid ?? 'Connected') : 'Disconnected'}
            </Row>
            {wifi.detailsAvailable ? (
              <>
                <Row label="Signal" tone={signalTone}>
                  {wifi.signalQuality}%{wifi.rssiDbm !== null ? ` (${wifi.rssiDbm} dBm)` : ''}
                </Row>
                <Row label="Speed">{wifi.receiveRateMbps !== null ? `${wifi.receiveRateMbps} Mbps` : '—'}</Row>
                <Row label="Security" tone={wifi.security === 'Open' || wifi.security === 'Wep' ? 'bad' : undefined}>
                  {humanize(wifi.security)}
                </Row>
                <Row label="Band / standard">{wifi.phyType}</Row>
              </>
            ) : (
              <Row label="Details">{wifi.note ?? 'Hidden by Windows location privacy settings.'}</Row>
            )}
          </>
        ) : (
          <Row label="WiFi">No adapter information</Row>
        )}
        <Row label="Addresses">
          <span className={styles.mono}>{h.network.ipAddresses.join(', ')}</span>
        </Row>
        <Row label="Port">{h.network.httpsPort}</Row>
        <Row label="Auto-discovery">
          {h.network.mdnsEnabled ? 'On (HC360 Desktop finds the server by itself)' : 'Off'}
        </Row>
        {h.network.lastDisconnectAt && <Row label="Last drop">{formatDateTime(h.network.lastDisconnectAt)}</Row>}
      </div>
      {h.network.recentEvents.length > 0 && (
        <div className={styles.events} style={{ marginTop: 12 }}>
          {h.network.recentEvents.slice(0, 20).map((e) => (
            <Caption1 key={`${e.at}-${e.kind}`}>
              {formatTime(e.at)} · {humanize(e.kind)} · {e.message}
            </Caption1>
          ))}
        </div>
      )}
    </Section>
  );
}

function WifiChart({ samples }: { samples: WifiSample[] }) {
  const option = useMemo<ChartOption>(
    () => ({
      grid: { left: 40, right: 16, top: 16, bottom: 32 },
      tooltip: { trigger: 'axis' },
      xAxis: { type: 'time' },
      yAxis: { type: 'value', min: 0, max: 100, axisLabel: { formatter: '{value}%' } },
      series: [
        {
          type: 'line',
          name: 'Signal',
          showSymbol: false,
          smooth: true,
          areaStyle: { opacity: 0.15 },
          data: samples.map((s) => [s.at, s.connected ? s.signalQuality : 0]),
          markLine: {
            silent: true,
            symbol: 'none',
            lineStyle: { type: 'dashed' },
            data: [{ yAxis: 40, name: 'Weak' }],
          },
        },
      ],
    }),
    [samples],
  );
  return <EChart option={option} height={220} ariaLabel="Server WiFi signal over time" />;
}
