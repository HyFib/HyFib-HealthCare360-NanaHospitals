import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { useConnection, type NetworkStatus } from '@hc360/core';
import { act, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { DEGRADED_NOTICE_AFTER_MS, DegradedBanner } from './ConnectionIndicator';

const wifi = (signalQuality: number): NetworkStatus => ({
  wifiConnected: true,
  ssid: 'NH-Staff',
  signalQuality,
  receiveRateMbps: 144,
  security: 'Wpa2Enterprise',
  detailsAvailable: true,
  note: null,
  serverReachable: true,
  serverLatencyMs: 12,
});

function renderBanner() {
  render(
    <FluentProvider theme={webLightTheme}>
      <DegradedBanner />
    </FluentProvider>,
  );
}

const wait = (ms: number) =>
  act(() => {
    vi.advanceTimersByTime(ms);
  });

describe('DegradedBanner', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    useConnection.setState({ browserOnline: true, serverReachable: true, realtime: 'connected', wifi: null });
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('says live updates are paused once they have been off for a while', () => {
    useConnection.setState({ realtime: 'disconnected' });
    renderBanner();

    wait(DEGRADED_NOTICE_AFTER_MS - 1000);
    expect(screen.queryByText('Live updates are paused')).not.toBeInTheDocument();

    wait(1000);
    expect(screen.getByText('Live updates are paused')).toBeInTheDocument();
    expect(screen.getByText(/may be out of date until the connection recovers/)).toBeInTheDocument();
  });

  it('says nothing about a reconnect that takes a moment', () => {
    useConnection.setState({ realtime: 'reconnecting' });
    renderBanner();

    wait(5000);
    act(() => useConnection.setState({ realtime: 'connected' }));
    wait(DEGRADED_NOTICE_AFTER_MS);

    expect(screen.queryByText('Live updates are paused')).not.toBeInTheDocument();
  });

  it('names a weak WiFi signal', () => {
    useConnection.setState({ wifi: wifi(28) });
    renderBanner();

    wait(DEGRADED_NOTICE_AFTER_MS);

    expect(screen.getByText('The WiFi signal is weak')).toBeInTheDocument();
    expect(screen.getByText(/WiFi signal is weak \(28%\)/)).toBeInTheDocument();
  });

  it('stays hidden once dismissed, until the connection has been good again', () => {
    useConnection.setState({ realtime: 'disconnected' });
    renderBanner();
    wait(DEGRADED_NOTICE_AFTER_MS);

    fireEvent.click(screen.getByRole('button', { name: 'Hide this message' }));
    expect(screen.queryByText('Live updates are paused')).not.toBeInTheDocument();
    act(() => useConnection.setState({ realtime: 'reconnecting' }));
    wait(DEGRADED_NOTICE_AFTER_MS);
    expect(screen.queryByText('Live updates are paused')).not.toBeInTheDocument();

    act(() => useConnection.setState({ realtime: 'connected' }));
    act(() => useConnection.setState({ realtime: 'disconnected' }));
    wait(DEGRADED_NOTICE_AFTER_MS);
    expect(screen.getByText('Live updates are paused')).toBeInTheDocument();
  });

  it('leaves an unreachable server to the offline banner', () => {
    useConnection.setState({ serverReachable: false, realtime: 'disconnected' });
    renderBanner();

    wait(DEGRADED_NOTICE_AFTER_MS);

    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    expect(screen.queryByText('Live updates are paused')).not.toBeInTheDocument();
  });
});
