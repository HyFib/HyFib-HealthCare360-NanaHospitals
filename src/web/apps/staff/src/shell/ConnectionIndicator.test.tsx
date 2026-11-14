import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { useConnection, type NetworkStatus } from '@hc360/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, fireEvent, render, screen } from '@testing-library/react';
import type { ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ConnectionIndicator, DEGRADED_NOTICE_AFTER_MS, DegradedBanner, OfflineBanner } from './ConnectionIndicator';

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

/** A workstation on its network cable, its WiFi also on (and weak) beside it. */
const cabled = (signalQuality = 28): NetworkStatus => ({ ...wifi(signalQuality), wired: true, cableSpeedMbps: 1000 });

function renderBanner() {
  renderShell(<DegradedBanner />);
}

function renderShell(ui: ReactNode) {
  render(
    <QueryClientProvider client={new QueryClient()}>
      <FluentProvider theme={webLightTheme}>{ui}</FluentProvider>
    </QueryClientProvider>,
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

  it('says nothing about a weak WiFi signal beside a network cable, which HC360 goes over', () => {
    useConnection.setState({ wifi: cabled(28) });
    renderBanner();

    wait(DEGRADED_NOTICE_AFTER_MS);

    expect(screen.queryByText('The WiFi signal is weak')).not.toBeInTheDocument();
    expect(screen.queryByText(/WiFi signal is weak/)).not.toBeInTheDocument();
  });

  it('leaves an unreachable server to the offline banner', () => {
    useConnection.setState({ serverReachable: false, realtime: 'disconnected' });
    renderBanner();

    wait(DEGRADED_NOTICE_AFTER_MS);

    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    expect(screen.queryByText('Live updates are paused')).not.toBeInTheDocument();
  });
});

// DED-07 on a cabled LAN, as at Nana Hospitals: the pill names the link a workstation is on.
describe('ConnectionIndicator', () => {
  beforeEach(() =>
    useConnection.setState({ browserOnline: true, serverReachable: true, realtime: 'connected', lastOkAt: null }),
  );

  it('says a cabled workstation is on its cable, not that its WiFi is or is not connected', () => {
    useConnection.setState({ wifi: { ...cabled(), wifiConnected: false, ssid: null } });
    renderShell(<ConnectionIndicator />);

    fireEvent.click(screen.getByRole('button', { name: 'Connection: Connected' }));

    expect(screen.getByText('Cable · 1 Gbps')).toBeInTheDocument();
    expect(screen.getByText('Network')).toBeInTheDocument();
    expect(screen.queryByText('WiFi')).not.toBeInTheDocument();
    expect(screen.queryByText('Not connected')).not.toBeInTheDocument();
  });

  it('shows the WiFi of a workstation that is not on a cable, as before', () => {
    useConnection.setState({ wifi: wifi(82) });
    renderShell(<ConnectionIndicator />);

    fireEvent.click(screen.getByRole('button', { name: 'Connection: Connected' }));

    expect(screen.getByText('WiFi')).toBeInTheDocument();
    expect(screen.getByText('NH-Staff · 82% signal')).toBeInTheDocument();
    expect(screen.queryByText(/^Cable/)).not.toBeInTheDocument();
  });

  it('says the database is down when the server answers without it, not that all is connected', () => {
    useConnection.setState({ wifi: cabled(), databaseDown: true });
    renderShell(<ConnectionIndicator />);

    fireEvent.click(screen.getByRole('button', { name: 'Connection: HealthCare360 database down' }));

    expect(screen.getByText('Answering, but its database is not')).toBeInTheDocument();
    act(() => useConnection.setState({ databaseDown: false }));
  });
});

describe('OfflineBanner', () => {
  beforeEach(() =>
    useConnection.setState({
      browserOnline: true,
      serverReachable: false,
      databaseDown: false,
      realtime: 'disconnected',
    }),
  );

  it('speaks of the database, not the network, when the server answers but its database is down', () => {
    useConnection.setState({ serverReachable: true, databaseDown: true, wifi: null });
    renderShell(<OfflineBanner />);

    expect(screen.getByText('HealthCare360 cannot reach its database')).toBeInTheDocument();
    expect(screen.getByText(/nothing can be opened or saved until it is back: tell IT/)).toBeInTheDocument();
    expect(screen.queryByText(/Check this computer's network cable or WiFi/)).not.toBeInTheDocument();
    act(() => useConnection.setState({ databaseDown: false }));
  });

  it("does not send a cabled workstation's user to its WiFi: the fault is beyond this computer", () => {
    useConnection.setState({ wifi: cabled() });
    renderShell(<OfflineBanner />);

    expect(screen.getByText('Cannot reach the HealthCare360 server')).toBeInTheDocument();
    expect(
      screen.getByText(/network cable is connected, so the server or the hospital network may be down/),
    ).toBeInTheDocument();
    expect(screen.queryByText(/Check this computer's network cable or WiFi/)).not.toBeInTheDocument();
  });

  it('asks a workstation not known to be on a cable to check its cable or WiFi', () => {
    useConnection.setState({ wifi: null });
    renderShell(<OfflineBanner />);

    expect(screen.getByText(/Check this computer's network cable or WiFi/)).toBeInTheDocument();
  });

  it('says that the same save made again is not saved twice, and to check first after changing anything', () => {
    useConnection.setState({ wifi: null });
    renderShell(<OfflineBanner />);

    expect(screen.getByText(/Saving it again with the same details will not save it twice/)).toBeInTheDocument();
    expect(screen.getByText(/if you change anything, first check whether it was saved/)).toBeInTheDocument();
    expect(screen.queryByText(/nothing will be duplicated/)).not.toBeInTheDocument();
  });
});
