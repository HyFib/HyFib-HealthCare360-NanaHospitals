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
  DismissRegular,
  PlugDisconnectedRegular,
  Wifi1Regular,
  Wifi3Regular,
  WifiWarningRegular,
} from '@fluentui/react-icons';
import { connectionHealth, formatRelative, useConnection, weakWifiSignal } from '@hc360/core';
import { useQueryClient } from '@tanstack/react-query';
import { useEffect, useRef, useState } from 'react';

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

/** How long the connection must stay weak before the banner says so: a reconnect that takes a moment is not news. */
export const DEGRADED_NOTICE_AFTER_MS = 15_000;

/**
 * Banner while the connection stays weak (SRS DED-07): live updates are off, or the WiFi signal is weak. Work goes on,
 * so it only says what may be out of date or slow. Hidden by the user, it stays hidden until the connection is good
 * again. While the server is unreachable, the offline banner speaks instead.
 */
export function DegradedBanner() {
  const styles = useStyles();
  const state = useConnection();
  const health = connectionHealth(state);
  const [due, setDue] = useState(false);
  const [hidden, setHidden] = useState(false);
  const [lastHealth, setLastHealth] = useState(health);

  // A new spell of weak connection waits its own time; one that ends in a good connection forgets the dismissal.
  if (health !== lastHealth) {
    setLastHealth(health);
    setDue(false);
    if (health === 'good') {
      setHidden(false);
    }
  }

  useEffect(() => {
    if (health !== 'degraded') {
      return;
    }
    const timer = window.setTimeout(() => setDue(true), DEGRADED_NOTICE_AFTER_MS);
    return () => window.clearTimeout(timer);
  }, [health]);

  if (health !== 'degraded' || !due || hidden) {
    return null;
  }

  const liveUpdatesOff = state.realtime !== 'connected';
  const signal = weakWifiSignal(state);
  const weakWifi = signal !== null ? `This computer's WiFi signal is weak (${signal}%).` : '';
  return (
    <MessageBar intent="info" layout="multiline" className={styles.banner} icon={<Wifi1Regular />}>
      <MessageBarBody>
        <MessageBarTitle>{liveUpdatesOff ? 'Live updates are paused' : 'The WiFi signal is weak'}</MessageBarTitle>
        {liveUpdatesOff
          ? `Queues, bed boards and alerts on this screen may be out of date until the connection recovers. HC360 keeps trying, and you can keep working. ${weakWifi}`.trim()
          : `${weakWifi} Screens may be slow and saving may take longer.`}
      </MessageBarBody>
      <MessageBarActions
        containerAction={
          <Button
            appearance="transparent"
            aria-label="Hide this message"
            icon={<DismissRegular />}
            onClick={() => setHidden(true)}
          />
        }
      />
    </MessageBar>
  );
}
