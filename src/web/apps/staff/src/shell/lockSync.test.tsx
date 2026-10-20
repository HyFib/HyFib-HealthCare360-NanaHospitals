import { auth, markActivity, useIdle, useSession, type AuthUser } from '@hc360/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, render, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { idleLockMinutes, useLockSync } from './lockSync';

const user = (restriction: AuthUser['restriction'] = null): AuthUser => ({
  id: 'u1',
  userName: 'asha',
  displayName: 'Asha Rao',
  type: 'staff',
  facilityId: null,
  facilityName: null,
  practitionerId: null,
  patientId: null,
  roles: ['Nurse'],
  permissions: [],
  facilities: [],
  mfaEnabled: false,
  hasPin: true,
  mustChangePassword: false,
  restriction,
});

describe('idle lock', () => {
  afterEach(() => {
    vi.useRealTimers();
    vi.restoreAllMocks();
    useSession.setState({ status: 'unknown', accessToken: null, user: null, locked: false });
  });

  it("reads the facility's lock time as the server does", () => {
    expect(idleLockMinutes(undefined)).toBe(10);
    expect(idleLockMinutes(15)).toBe(15);
    expect(idleLockMinutes('20')).toBe(20);
    expect(idleLockMinutes(0)).toBe(0);
    expect(idleLockMinutes(-5)).toBe(0);
    expect(idleLockMinutes('soon')).toBe(10);
    expect(idleLockMinutes(7.5)).toBe(10);
  });

  it('locks once after the time without use, counting use reported from any window', () => {
    vi.useFakeTimers();
    const onIdle = vi.fn();
    function Shell() {
      useIdle(10 * 60_000, onIdle);
      return null;
    }
    render(<Shell />);

    act(() => vi.advanceTimersByTime(9 * 60_000));
    // Someone works in another of the app's windows (or a video call reports use).
    act(() => markActivity());
    act(() => vi.advanceTimersByTime(9 * 60_000));
    expect(onIdle).not.toHaveBeenCalled();

    act(() => vi.advanceTimersByTime(2 * 60_000));
    expect(onIdle).toHaveBeenCalledTimes(1);
    act(() => vi.advanceTimersByTime(30 * 60_000));
    expect(onIdle).toHaveBeenCalledTimes(1);
  });

  it("locks and unlocks the app's other windows with this one", async () => {
    useSession.setState({ status: 'authenticated', accessToken: 'token', user: user(), locked: false });
    const client = new QueryClient();
    const invalidate = vi.spyOn(client, 'invalidateQueries');
    const refresh = vi.spyOn(auth, 'refresh').mockImplementation(async () => {
      useSession.getState().setAuth({ accessToken: 'unlocked-token', expiresAt: null, user: user() });
      return 'unlocked-token';
    });
    function Window() {
      useLockSync();
      return null;
    }
    render(
      <QueryClientProvider client={client}>
        <Window />
      </QueryClientProvider>,
    );
    const otherWindow = new BroadcastChannel('hc360:lock');
    const heard: unknown[] = [];
    otherWindow.onmessage = (event: MessageEvent) => heard.push(event.data);

    // Locked in another window: this one locks at once.
    otherWindow.postMessage({ userId: 'u1', locked: true });
    await waitFor(() => expect(useSession.getState().locked).toBe(true));

    // Unlocked there: this one asks the server, which unlocked the session, and asks again for its screens' data.
    otherWindow.postMessage({ userId: 'u1', locked: false });
    await waitFor(() => expect(useSession.getState().locked).toBe(false));
    expect(refresh).toHaveBeenCalledTimes(1);
    expect(useSession.getState().accessToken).toBe('unlocked-token');
    expect(invalidate).toHaveBeenCalled();

    // Neither change came from this window, so it told the others nothing; locking here does.
    expect(heard).toEqual([]);
    act(() => useSession.getState().setLocked(true));
    await waitFor(() => expect(heard).toEqual([{ userId: 'u1', locked: true }]));
    otherWindow.close();
  });

  it("ignores another account's lock", async () => {
    useSession.setState({ status: 'authenticated', accessToken: 'token', user: user(), locked: false });
    function Window() {
      useLockSync();
      return null;
    }
    render(
      <QueryClientProvider client={new QueryClient()}>
        <Window />
      </QueryClientProvider>,
    );
    const otherWindow = new BroadcastChannel('hc360:lock');

    otherWindow.postMessage({ userId: 'someone-else', locked: true });
    await new Promise((resolve) => setTimeout(resolve, 50));

    expect(useSession.getState().locked).toBe(false);
    otherWindow.close();
  });
});
