import { Button, Caption1, makeStyles, Text, tokens } from '@fluentui/react-components';
import { CheckmarkCircleRegular, WarningRegular } from '@fluentui/react-icons';
import { formatRelative } from '@hc360/core';
import { ErrorState, Section } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { useNavigate } from '@tanstack/react-router';
import { adminQueries } from './api';
import { serversSummary } from './systemHealth';

const useStyles = makeStyles({
  rows: { display: 'grid', gridTemplateColumns: 'auto 1fr', gap: '6px 16px', alignItems: 'center' },
  muted: { color: tokens.colorNeutralForeground3 },
  ok: { color: tokens.colorPaletteGreenForeground1 },
  warn: { color: tokens.colorPaletteMarigoldForeground1 },
  bad: { color: tokens.colorPaletteRedForeground1 },
});

/**
 * Home-screen summary of the dedicated server machine for IT/administrators. With several servers (SCAL-01) it says
 * whether every one reports, instead of the WiFi and power of whichever server answered.
 */
export function SystemHealthWidget() {
  const styles = useStyles();
  const navigate = useNavigate();
  const health = useQuery(adminQueries.health());

  if (health.error) {
    return <ErrorState error={health.error} onRetry={() => void health.refetch()} />;
  }
  const h = health.data;
  const problems = h?.warnings.filter((w) => w.severity !== 'info') ?? [];
  const wifi = h?.network.wifi;
  const servers = h?.servers ? serversSummary(h.servers) : null;

  return (
    <Section
      title={servers ? 'Servers' : 'Server machine'}
      actions={
        <Button size="small" appearance="subtle" onClick={() => void navigate({ to: '/admin/system' })}>
          Details
        </Button>
      }
    >
      {!h ? (
        <Caption1>Checking…</Caption1>
      ) : (
        <div className={styles.rows}>
          {problems.length === 0 ? (
            <CheckmarkCircleRegular className={styles.ok} />
          ) : (
            <WarningRegular className={styles.warn} />
          )}
          <Text weight="semibold">
            {problems.length === 0
              ? 'All systems normal'
              : `${problems.length} item${problems.length > 1 ? 's' : ''} need attention`}
          </Text>
          {servers ? (
            <>
              <Caption1 className={styles.muted}>Servers</Caption1>
              <Caption1 className={servers.tone === 'bad' ? styles.bad : undefined}>{servers.text}</Caption1>
            </>
          ) : (
            <>
              <Caption1 className={styles.muted}>WiFi</Caption1>
              <Caption1>
                {wifi?.isConnected
                  ? `${wifi.ssid ?? 'Connected'}${wifi.detailsAvailable ? ` · ${wifi.signalQuality}%` : ''}`
                  : h.network.wifiSupported
                    ? 'Disconnected'
                    : 'Wired network'}
              </Caption1>
            </>
          )}
          <Caption1 className={styles.muted}>Database</Caption1>
          <Caption1 className={h.database.reachable ? undefined : styles.bad}>
            {h.database.reachable ? 'Online' : 'Not reachable'}
          </Caption1>
          <Caption1 className={styles.muted}>Last backup</Caption1>
          <Caption1 className={!h.backup.enabled || !h.backup.lastSuccessAt ? styles.warn : undefined}>
            {!h.backup.enabled
              ? 'Backups are off'
              : h.backup.lastSuccessAt
                ? formatRelative(h.backup.lastSuccessAt)
                : 'Never'}
          </Caption1>
          {!servers && h.power.available && (
            <>
              <Caption1 className={styles.muted}>Power</Caption1>
              <Caption1 className={h.power.onBattery ? styles.bad : undefined}>
                {h.power.onBattery
                  ? `On UPS battery${h.power.chargePercent !== null ? ` (${h.power.chargePercent}%)` : ''}`
                  : 'Mains'}
              </Caption1>
            </>
          )}
        </div>
      )}
    </Section>
  );
}
