import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  auth,
  lastActivityAt,
  markActivity,
  useSession,
  whenUnlocked,
  type AuthResult,
  type AuthUser,
} from './session';

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
  permissions: ['patients.read'],
  facilities: [],
  mfaEnabled: false,
  hasPin: true,
  mustChangePassword: false,
  restriction,
});

function refreshAnswer(result: AuthResult): Response {
  return new Response(JSON.stringify(result), { status: 200, headers: { 'content-type': 'application/json' } });
}

describe('session', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    vi.useRealTimers();
    useSession.setState({ status: 'unknown', accessToken: null, expiresAt: null, user: null, locked: false });
  });

  it('tells the server how long nobody has used the app when it refreshes in the background', async () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date('2026-10-09T08:00:00Z'));
    markActivity();
    vi.setSystemTime(new Date('2026-10-09T08:04:30Z'));
    const fetchMock = vi.fn().mockResolvedValue(refreshAnswer({ accessToken: 't2', expiresAt: null, user: user() }));
    vi.stubGlobal('fetch', fetchMock);

    await auth.refresh();

    const headers = new Headers((fetchMock.mock.calls[0]![1] as RequestInit).headers);
    expect(headers.get('X-HC360-Idle')).toBe('270');
  });

  it('asks for and checks a portal sign-in code in the language given, and names none when none is given', async () => {
    const fetchMock = vi
      .fn()
      .mockImplementation(async () =>
        refreshAnswer({ challengeId: 'c1', expiresAt: null, maskedPhone: '******3210' } as AuthResult),
      );
    vi.stubGlobal('fetch', fetchMock);

    await auth.requestOtp('+919876543210', { language: 'hi' });
    await auth.verifyOtp('c1', '123456', { language: 'hi' });
    await auth.requestOtp('+919876543210');

    const sent = fetchMock.mock.calls.map(([url, init]) => [
      url,
      new Headers((init as RequestInit).headers).get('X-HC360-Language'),
    ]);
    expect(sent).toEqual([
      ['/api/v1/auth/otp/request', 'hi'],
      ['/api/v1/auth/otp/verify', 'hi'],
      ['/api/v1/auth/otp/request', null],
    ]);
  });

  it('locks the screen when the server says the session is locked, and keeps the screens behind it', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue(refreshAnswer({ accessToken: 't2', expiresAt: null, user: user('locked') })),
    );

    await auth.refresh();

    const state = useSession.getState();
    expect(state.locked).toBe(true);
    expect(state.status).toBe('authenticated');
    expect(state.user?.permissions).toEqual(['patients.read']);
  });

  it('lets a refused read wait for the unlock, and gives up when the session ends or the read is cancelled', async () => {
    useSession.setState({ status: 'authenticated', locked: true, user: user('locked') });
    const unlocked = whenUnlocked();
    const signedOut = whenUnlocked();
    const cancel = new AbortController();
    const cancelled = whenUnlocked(cancel.signal);

    cancel.abort(new DOMException('gone', 'AbortError'));
    await expect(cancelled).rejects.toMatchObject({ name: 'AbortError' });
    useSession.getState().setLocked(false);
    await expect(unlocked).resolves.toBe(true);
    await expect(signedOut).resolves.toBe(true);

    useSession.setState({ locked: true });
    const ended = whenUnlocked();
    useSession.getState().clear();
    await expect(ended).resolves.toBe(false);
  });

  it('counts use in another of the app windows as use of this one', async () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date('2026-10-09T08:00:00Z'));
    markActivity();
    vi.setSystemTime(new Date('2026-10-09T08:09:00Z'));
    expect(lastActivityAt()).toBe(Date.parse('2026-10-09T08:00:00Z'));

    // Another window (an HC360 Desktop second window, another tab) tells the others someone is using it.
    const otherWindow = new BroadcastChannel('hc360:activity');
    otherWindow.postMessage(null);

    await vi.waitFor(() => expect(lastActivityAt()).toBeGreaterThanOrEqual(Date.parse('2026-10-09T08:09:00Z')));
    otherWindow.close();
  });

  it("takes turns with the app's other windows to refresh, so the browser never keeps a cookie the server retired", async () => {
    // The server as AuthService answers a refresh: it replaces the cookie; one presented again (an answer lost on the
    // WiFi) gets another, and the replacement it gave before is retired; a retired one ends the session.
    let cookie = 'rt-1';
    let issued = 1;
    let ended = false;
    const replacedBy = new Map<string, string>();
    const retired = new Set<string>();
    const presented: string[] = [];
    // The first request reaches the server first but its answer comes back last, as on a busy WiFi link.
    const delays = [
      [1, 30],
      [5, 1],
    ];
    const sleep = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms));
    const fetchMock = vi.fn(async () => {
      const [toServer, back] = delays[presented.length] ?? [1, 1];
      const sent = cookie;
      presented.push(sent);
      await sleep(toServer!);
      if (ended || retired.has(sent)) {
        ended = true;
        return new Response(JSON.stringify({ code: 'auth.session_ended' }), { status: 401 });
      }
      const before = replacedBy.get(sent);
      if (before) {
        retired.add(before);
      }
      const next = `rt-${++issued}`;
      replacedBy.set(sent, next);
      await sleep(back!);
      cookie = next; // Set-Cookie, as the answer arrives
      return refreshAnswer({ accessToken: `token-${next}`, expiresAt: null, user: user() });
    });
    vi.stubGlobal('fetch', fetchMock);
    // Web Locks as the browser gives them: one holder of a name at a time, among all of this site's windows.
    const held = new Map<string, Promise<unknown>>();
    vi.stubGlobal('navigator', {
      locks: {
        request: (name: string, callback: () => Promise<unknown>) => {
          const turn = (held.get(name) ?? Promise.resolve()).then(callback);
          const released = turn.catch(() => undefined);
          held.set(name, released);
          return turn;
        },
      },
    });
    // Two of the app's windows: each has its own copy of the session code, as each page does.
    vi.resetModules();
    const first = await import('./session');
    vi.resetModules();
    const second = await import('./session');

    // Both refresh at once, as every window does when the screen is unlocked in another.
    await Promise.all([first.auth.refresh(), second.auth.refresh()]);
    const later = await first.auth.refresh();

    expect(presented).toEqual(['rt-1', 'rt-2', 'rt-3']);
    expect(ended).toBe(false);
    expect(later).toBe('token-rt-4');
    expect(first.useSession.getState().status).toBe('authenticated');
    expect(second.useSession.getState().accessToken).toBe('token-rt-3');
  });

  it('does not unlock a locked screen by itself: only the PIN does', () => {
    useSession.setState({ locked: true, user: user() });

    useSession.getState().setAuth({ accessToken: 't3', expiresAt: null, user: user() });

    expect(useSession.getState().locked).toBe(true);
  });
});
