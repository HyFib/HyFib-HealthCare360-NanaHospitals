import {
  Button,
  Caption1,
  makeStyles,
  MessageBar,
  MessageBarActions,
  MessageBarBody,
  MessageBarTitle,
  Popover,
  PopoverSurface,
  PopoverTrigger,
  Text,
  tokens,
} from '@fluentui/react-components';
import {
  ArrowClockwiseRegular,
  PlugDisconnectedRegular,
  Wifi1Regular,
  Wifi3Regular,
  WifiWarningRegular,
} from '@fluentui/react-icons';
import { connectionHealth, formatRelative, useConnection } from '@hc360/core';
import { useQueryClient } from '@tanstack/react-query';
import { useEffect, useRef } from 'react';

const useStyles = makeStyles({
  pill: { minWidth: 'auto' },
  good: { color: tokens.colorPaletteGreenForeground1 },
  degraded: { color: tokens.colorPaletteMarigoldForeground1 },
  offline: { color: tokens.colorPaletteRedForeground1 },
  details: { display: 'grid', gridTemplateColumns: 'auto 1fr', gap: '6px 16px', minWidth: '260px' },
  label: { color: tokens.colorNeutralForeground3 },
  banner: { borderRadius: 0 },
});

const LABELS = { good: 'Connected', degraded: 'Connection is weak', offline: 'Offline' } as const;

/** Top-bar pill: green/amber/red with details about the WiFi, the server and live updates (SRS DED-07). */
export function ConnectionIndicator() {
  const styles = useStyles();
  const state = useConnection();
  const health = connectionHealth(state);
  const Icon = health === 'good' ? Wifi3Regular : health === 'degraded' ? Wifi1Regular : WifiWarningRegular;

  return (
    <Popover withArrow positioning="below-end">
      <PopoverTrigger disableButtonEnhancement>
        <Button
          appearance="subtle"
          className={styles.pill}
          icon={<Icon className={styles[health]} />}
          aria-label={`Connection: ${LABELS[health]}`}
        />
      </PopoverTrigger>
      <PopoverSurface aria-label="Connection details">
        <div className={styles.details}>
          <Text weight="semibold" style={{ gridColumn: '1 / -1' }} className={styles[health]}>
            {LABELS[health]}
          </Text>
          <Caption1 className={styles.label}>Server</Caption1>
          <Caption1>{state.serverReachable ? 'Reachable' : 'Not reachable'}</Caption1>
          <Caption1 className={styles.label}>Live updates</Caption1>
          <Caption1>
            {state.realtime === 'connected'
              ? 'On'
              : state.realtime === 'disconnected'
                ? 'Off (retrying)'
                : 'Connecting…'}
          </Caption1>
          {state.wifi && (
            <>
              <Caption1 className={styles.label}>WiFi</Caption1>
              <Caption1>
                {state.wifi.wifiConnected
                  ? `${state.wifi.ssid ?? 'Connected'}${state.wifi.detailsAvailable && state.wifi.signalQuality !== null ? ` · ${state.wifi.signalQuality}% signal` : ''}`
                  : 'Not connected'}
              </Caption1>
            </>
          )}
          {state.lastOkAt && (
            <>
              <Caption1 className={styles.label}>Last contact</Caption1>
              <Caption1>{formatRelative(new Date(state.lastOkAt).toISOString())}</Caption1>
            </>
          )}
        </div>
      </PopoverSurface>
    </Popover>
  );
}

/**
 * Full-width banner while the server is unreachable. Work in progress stays on screen; saves are retried safely
 * with idempotency keys, so users are told they can simply try again.
 */
export function OfflineBanner() {
  const styles = useStyles();
  const state = useConnection();
  const queryClient = useQueryClient();
  const offline = !state.browserOnline || !state.serverReachable;
  const wasOffline = useRef(false);

  // Back online: refresh everything on screen so nobody works from stale data.
  useEffect(() => {
    if (wasOffline.current && !offline) {
      void queryClient.invalidateQueries();
    }
    wasOffline.current = offline;
  }, [offline, queryClient]);

  if (!offline) {
    return null;
  }

  return (
    <MessageBar intent="warning" layout="multiline" className={styles.banner} icon={<PlugDisconnectedRegular />}>
      <MessageBarBody>
        <MessageBarTitle>
          {state.browserOnline ? 'Cannot reach the HealthCare360 server' : 'This computer is offline'}
        </MessageBarTitle>
        Check the WiFi connection. Your unsaved work stays on this screen and nothing will be duplicated when you retry.
      </MessageBarBody>
      <MessageBarActions>
        <Button
          icon={<ArrowClockwiseRegular />}
          onClick={() =>
            void fetch('/health/live', { cache: 'no-store' }).then(
              (r) => state.setReachable(r.ok),
              () => state.setReachable(false),
            )
          }
        >
          Check again
        </Button>
      </MessageBarActions>
    </MessageBar>
  );
}
