import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import {
  ApiError,
  clearDrafts,
  draftStorage,
  draftStorageKey,
  openDrafts,
  rememberedUnlockKeys,
  rememberUnlockKey,
  useConnection,
  useSession,
  type AuthUser,
} from '@hc360/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { useState } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { api } from '@/lib/api';
import { PUBLIC_SETTINGS_KEY } from '@/lib/lookups';
import { useOfflineCopy } from '@/lib/offline/offlineCopy';
import { forgetOfflineUnlock, openOfflineUnlock, rememberOfflineSecret } from '@/lib/offline/offlineUnlock';
import type { DowntimePack } from '@/modules/downtime/continuityApi';
import { LockScreen } from './LockScreen';

// The server is outside the test: what the lock screen sends and does with the answers is what is tested.
vi.mock('@/lib/api', () => ({ api: { post: vi.fn() } }));
vi.mock('@tanstack/react-router', () => ({ useNavigate: () => vi.fn() }));

const user = (restriction: AuthUser['restriction'] = null, biometricUnlock = true): AuthUser => ({
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
  biometricUnlock,
});

const buffer = (...values: number[]) => new Uint8Array(values).buffer;

/** The server's options for unlocking with the device: this browser's key (AQID) and one set up on another (BAU). */
const unlockCeremony = {
  challengeId: 'c1',
  options: {
    challenge: 'CQgH',
    rpId: 'localhost',
    timeout: 60000,
    userVerification: 'required',
    allowCredentials: [
      { type: 'public-key', id: 'AQID', transports: ['internal'] },
      { type: 'public-key', id: 'BAU', transports: ['internal'] },
    ],
  },
};

/** What Windows Hello gives back once it has recognised the person. */
const signed = {
  id: 'AQID',
  rawId: buffer(1, 2, 3),
  type: 'public-key',
  response: { clientDataJSON: buffer(9), authenticatorData: buffer(8), signature: buffer(7), userHandle: null },
  getClientExtensionResults: () => ({}),
};

function renderLocked(who: AuthUser = user('locked'), settings?: Record<string, unknown>) {
  useSession.setState({ status: 'authenticated', accessToken: 'locked-token', user: who, locked: true });
  const client = new QueryClient();
  if (settings) {
    // The hospital's settings as the app last had them from the server.
    client.setQueryData(PUBLIC_SETTINGS_KEY, settings);
  }
  const invalidate = vi.spyOn(client, 'invalidateQueries');
  render(
    <QueryClientProvider client={client}>
      <FluentProvider theme={webLightTheme}>
        <LockScreen />
      </FluentProvider>
    </QueryClientProvider>,
  );
  return { invalidate };
}

/** A browser whose device has Windows Hello (WebAuthn with a platform authenticator), on a secure page. */
function deviceWithWindowsHello(get: (options: CredentialRequestOptions) => Promise<unknown>) {
  vi.stubGlobal('isSecureContext', true);
  vi.stubGlobal('PublicKeyCredential', function PublicKeyCredential() {});
  Object.defineProperty(navigator, 'credentials', { configurable: true, value: { get: vi.fn(get) } });
  return navigator.credentials.get as ReturnType<typeof vi.fn>;
}

describe('LockScreen', () => {
  beforeEach(() => vi.mocked(api.post).mockReset());
  afterEach(() => {
    vi.unstubAllGlobals();
    Reflect.deleteProperty(navigator, 'credentials');
    localStorage.clear();
    useSession.setState({ status: 'unknown', accessToken: null, user: null, locked: false });
  });

  it('locks the session on the server too', async () => {
    vi.mocked(api.post).mockResolvedValue(undefined);

    renderLocked();

    await waitFor(() => expect(api.post).toHaveBeenCalledWith('/api/v1/auth/lock'));
  });

  it('takes the new token the server gives on unlocking, and asks again for what was refused while locked', async () => {
    vi.mocked(api.post).mockImplementation(async (path: string) =>
      path === '/api/v1/auth/unlock' ? { accessToken: 'fresh-token', expiresAt: null, user: user() } : undefined,
    );
    const { invalidate } = renderLocked();

    await userEvent.type(screen.getByLabelText('PIN'), '2468');
    await userEvent.click(screen.getByRole('button', { name: 'Unlock' }));

    await waitFor(() => expect(useSession.getState().locked).toBe(false));
    expect(api.post).toHaveBeenCalledWith('/api/v1/auth/unlock', { pin: '2468' });
    expect(useSession.getState().accessToken).toBe('fresh-token');
    expect(useSession.getState().user?.restriction).toBeNull();
    expect(invalidate).toHaveBeenCalled();
  });

  it('stays locked after a wrong PIN', async () => {
    vi.mocked(api.post).mockImplementation(async (path: string) => {
      if (path === '/api/v1/auth/unlock') {
        throw new ApiError({
          status: 400,
          title: 'Please correct the highlighted fields.',
          errors: { pin: ['That did not match. Try again.'] },
        });
      }
      return undefined;
    });
    renderLocked();

    await userEvent.type(screen.getByLabelText('PIN'), '1357');
    await userEvent.click(screen.getByRole('button', { name: 'Unlock' }));

    expect(await screen.findByText('That did not match. Try again.')).toBeInTheDocument();
    expect(useSession.getState().locked).toBe(true);
  });

  it("unlocks with the device's Windows Hello, asking it only for the key this browser holds", async () => {
    rememberUnlockKey('u1', 'AQID');
    const get = deviceWithWindowsHello(async () => signed);
    vi.mocked(api.post).mockImplementation(async (path: string) => {
      if (path === '/api/v1/auth/unlock/webauthn/options') {
        return unlockCeremony;
      }
      return path === '/api/v1/auth/unlock/webauthn'
        ? { accessToken: 'fresh-token', expiresAt: null, user: user() }
        : undefined;
    });
    const { invalidate } = renderLocked();

    await userEvent.click(screen.getByRole('button', { name: /^Unlock with/ }));

    await waitFor(() => expect(useSession.getState().locked).toBe(false));
    const publicKey = (get.mock.calls[0]![0] as CredentialRequestOptions).publicKey!;
    expect(publicKey.userVerification).toBe('required');
    expect(publicKey.allowCredentials).toHaveLength(1);
    expect(new Uint8Array(publicKey.allowCredentials![0]!.id as ArrayBuffer)).toEqual(new Uint8Array([1, 2, 3]));
    expect(api.post).toHaveBeenCalledWith('/api/v1/auth/unlock/webauthn', {
      challengeId: 'c1',
      credential: expect.objectContaining({
        id: 'AQID',
        rawId: 'AQID',
        response: expect.objectContaining({ signature: 'Bw' }),
      }),
    });
    expect(useSession.getState().accessToken).toBe('fresh-token');
    expect(invalidate).toHaveBeenCalled();
  });

  it('stays locked, and the PIN still works, when Windows Hello is cancelled', async () => {
    rememberUnlockKey('u1', 'AQID');
    deviceWithWindowsHello(async () => {
      throw new DOMException('The operation either timed out or was not allowed.', 'NotAllowedError');
    });
    vi.mocked(api.post).mockImplementation(async (path: string) => {
      if (path === '/api/v1/auth/unlock/webauthn/options') {
        return unlockCeremony;
      }
      return path === '/api/v1/auth/unlock' ? { accessToken: 'fresh-token', expiresAt: null, user: user() } : undefined;
    });
    renderLocked();

    await userEvent.click(screen.getByRole('button', { name: /^Unlock with/ }));

    expect(await screen.findByRole('alert')).toHaveTextContent(/cancelled or timed out/);
    expect(useSession.getState().locked).toBe(true);
    expect(api.post).not.toHaveBeenCalledWith('/api/v1/auth/unlock/webauthn', expect.anything());

    await userEvent.type(screen.getByLabelText('PIN'), '2468');
    await userEvent.click(screen.getByRole('button', { name: 'Unlock' }));
    await waitFor(() => expect(useSession.getState().locked).toBe(false));
  });

  it('offers only the PIN and password where the hospital turned Windows Hello off or the device has no key', () => {
    rememberUnlockKey('u1', 'AQID');
    deviceWithWindowsHello(async () => signed);
    vi.mocked(api.post).mockResolvedValue(undefined);

    renderLocked(user('locked', false));

    expect(screen.queryByRole('button', { name: /^Unlock with/ })).not.toBeInTheDocument();
    expect(screen.getByLabelText('PIN')).toBeInTheDocument();
  });

  it('stops offering the device where the server says the computer is a kiosk or a display board', async () => {
    rememberUnlockKey('u1', 'AQID');
    const get = deviceWithWindowsHello(async () => signed);
    vi.mocked(api.post).mockImplementation(async (path: string) => {
      if (path === '/api/v1/auth/unlock/webauthn/options') {
        throw new ApiError({
          status: 403,
          title: 'Forbidden',
          detail: 'This computer is enrolled as a kiosk or a display that everyone uses. Use your PIN or password.',
          code: 'auth.biometric_unlock_shared_device',
        });
      }
      return undefined;
    });
    renderLocked();

    await userEvent.click(screen.getByRole('button', { name: /^Unlock with/ }));

    expect(await screen.findByRole('alert')).toHaveTextContent(/kiosk/);
    expect(get).not.toHaveBeenCalled();
    expect(screen.queryByRole('button', { name: /^Unlock with/ })).not.toBeInTheDocument();
    expect(useSession.getState().locked).toBe(true);
    expect(screen.getByLabelText('PIN')).toBeInTheDocument();
  });

  it('forgets a key removed since, and falls back to the PIN', async () => {
    rememberUnlockKey('u1', 'CQ');
    const get = deviceWithWindowsHello(async () => signed);
    vi.mocked(api.post).mockImplementation(async (path: string) =>
      path === '/api/v1/auth/unlock/webauthn/options' ? unlockCeremony : undefined,
    );
    renderLocked();

    await userEvent.click(screen.getByRole('button', { name: /^Unlock with/ }));

    expect(await screen.findByRole('alert')).toHaveTextContent(/no longer set up/);
    expect(get).not.toHaveBeenCalled();
    expect(rememberedUnlockKeys('u1')).toEqual([]);
    expect(screen.queryByRole('button', { name: /^Unlock with/ })).not.toBeInTheDocument();
  });

  it('ends a session the server says is over without signing out, so the drafts typed in it stay', async () => {
    const noteDraft = draftStorageKey('u1', 'nursing-note:adm-1');
    await openDrafts('u1', btoa(String.fromCharCode(...new Uint8Array(32).fill(7))));
    draftStorage.setItem(noteDraft, JSON.stringify({ value: { note: 'BP 150/95' }, savedAt: 1 }));
    await waitFor(() => expect(localStorage.getItem(noteDraft)).toMatch(/^\{"v":2,/));
    const fetchMock = vi.fn().mockResolvedValue(new Response(null, { status: 204 }));
    vi.stubGlobal('fetch', fetchMock);
    vi.mocked(api.post).mockImplementation(async (path: string) => {
      if (path === '/api/v1/auth/unlock') {
        // It expired while the screen was locked, through an outage.
        throw new ApiError({ status: 401, title: 'Please sign in again', code: 'auth.session_ended' });
      }
      return undefined;
    });
    renderLocked();

    await userEvent.type(screen.getByLabelText('PIN'), '2468');
    await userEvent.click(screen.getByRole('button', { name: 'Unlock' }));

    await waitFor(() => expect(useSession.getState().status).toBe('anonymous'));
    // Nobody signed out: no sign-out is sent, and the draft stays, encrypted, for Asha's next sign-in.
    expect(fetchMock).not.toHaveBeenCalledWith('/api/v1/auth/logout', expect.anything());
    expect(localStorage.getItem(noteDraft)).toMatch(/^\{"v":2,/);
    clearDrafts();
  });

  it('keeps what was typed and not yet saved on the screen behind the lock', async () => {
    vi.mocked(api.post).mockImplementation(async (path: string) =>
      path === '/api/v1/auth/unlock' ? { accessToken: 'fresh-token', expiresAt: null, user: user() } : undefined,
    );
    function Notes() {
      const [text, setText] = useState('');
      return <textarea aria-label="Clinical notes" value={text} onChange={(e) => setText(e.target.value)} />;
    }
    useSession.setState({ status: 'authenticated', accessToken: 'token', user: user(), locked: false });
    render(
      <QueryClientProvider client={new QueryClient()}>
        <FluentProvider theme={webLightTheme}>
          <Notes />
          <LockScreen />
        </FluentProvider>
      </QueryClientProvider>,
    );
    await userEvent.type(screen.getByLabelText('Clinical notes'), 'Fever since two days');

    act(() => useSession.getState().setLocked(true));
    await userEvent.type(await screen.findByLabelText('PIN'), '2468');
    await userEvent.click(screen.getByRole('button', { name: 'Unlock' }));

    await waitFor(() => expect(useSession.getState().locked).toBe(false));
    expect(screen.getByLabelText('Clinical notes')).toHaveValue('Fever since two days');
  });
});

/** Asha's key from the server (AuthResult.draftKey), the same in each of her sessions. */
const SERVER_KEY = btoa(String.fromCharCode(...Array.from({ length: 32 }, (_, i) => i + 1)));

const PACK: DowntimePack = {
  id: 'p1',
  kind: 'Refreshed',
  generatedAt: new Date(Date.now() - 20 * 60_000).toISOString(),
  windowEndsAt: new Date(Date.now() + 86_400_000).toISOString(),
  facilityName: 'Nana Hospitals',
  generatedByName: null,
  patients: 1,
  sections: [
    {
      key: 'census',
      title: 'Census by ward',
      note: null,
      pagePerTable: false,
      tables: [
        {
          heading: 'Ward A',
          note: null,
          alert: null,
          columns: ['Bed', 'Patient (UHID)'],
          rows: [['A-1', 'Meena Iyer (NH26000001)']],
        },
      ],
    },
  ],
};

/** Asha's session before the outage: the server accepted her PIN once on this computer, and her copy was taken. */
async function readyForAnOutage() {
  openOfflineUnlock('u1', SERVER_KEY);
  await rememberOfflineSecret('u1', 'pin', '2468');
  useOfflineCopy.setState({ userId: 'u1', takenAt: Date.now() - 10 * 60_000, pack: PACK, worklists: [] });
}

const offlineCopy = () => screen.queryByRole('dialog', { name: 'Offline copy (read only)' });

/**
 * DED-07 with UX-11: the screen locks during an outage, when nothing can unlock it and signing out would wipe the
 * offline copy. The PIN or password, checked on this computer, opens the copy read only over the lock, for the user
 * whose screen it is.
 */
describe('LockScreen during an outage', () => {
  beforeEach(() => vi.mocked(api.post).mockReset().mockResolvedValue(undefined));
  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
    forgetOfflineUnlock();
    localStorage.clear();
    useOfflineCopy.setState({ userId: null, takenAt: null, pack: null, worklists: [] });
    useConnection.setState({ browserOnline: true, serverReachable: true, databaseDown: false });
    useSession.setState({ status: 'unknown', accessToken: null, user: null, locked: false });
  });

  it('opens the offline copy after the right PIN, read only, sending nothing to the server', async () => {
    await readyForAnOutage();
    useConnection.setState({ serverReachable: false });
    renderLocked();
    expect(screen.getByText('Cannot reach the HealthCare360 server')).toBeInTheDocument();
    await waitFor(() => expect(api.post).toHaveBeenCalledWith('/api/v1/auth/lock'));
    vi.mocked(api.post).mockClear();
    const fetchMock = vi.fn();
    vi.stubGlobal('fetch', fetchMock);

    await userEvent.type(screen.getByLabelText('PIN'), '2468');
    await userEvent.click(screen.getByRole('button', { name: 'Open the offline copy' }));

    const copy = await screen.findByRole('dialog', { name: 'Offline copy (read only)' });
    expect(within(copy).getByRole('region', { name: 'Census by ward' })).toHaveTextContent('Meena Iyer (NH26000001)');
    // Read only, over a screen that stays locked: nothing to save, and nothing sent.
    expect(
      within(copy)
        .getAllByRole('button')
        .map((b) => b.textContent),
    ).toEqual(['Close']);
    expect(useSession.getState().locked).toBe(true);
    expect(api.post).not.toHaveBeenCalled();
    expect(fetchMock).not.toHaveBeenCalled();

    // Closed, the PIN is asked for again.
    await userEvent.click(within(copy).getByRole('button', { name: 'Close' }));
    expect(await screen.findByLabelText('PIN')).toHaveValue('');
    expect(offlineCopy()).not.toBeInTheDocument();
  });

  it('refuses a wrong PIN and says how many tries are left', async () => {
    await readyForAnOutage();
    useConnection.setState({ serverReachable: false });
    renderLocked();

    await userEvent.type(screen.getByLabelText('PIN'), '1357');
    await userEvent.click(screen.getByRole('button', { name: 'Open the offline copy' }));

    expect(await screen.findByText(/That did not match\. 4 more tries/)).toBeInTheDocument();
    expect(offlineCopy()).not.toBeInTheDocument();
    expect(useSession.getState().locked).toBe(true);
    expect(api.post).not.toHaveBeenCalledWith('/api/v1/auth/unlock', expect.anything());
  });

  it("never opens one user's copy on another user's locked screen, even with that user's PIN", async () => {
    await readyForAnOutage();
    useConnection.setState({ serverReachable: false });
    renderLocked({ ...user('locked'), id: 'u2', userName: 'ravi', displayName: 'Ravi Kumar' });

    expect(screen.getByText(/There is no offline copy on this computer/)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Open the offline copy' })).not.toBeInTheDocument();
    // Asha's PIN on Ravi's screen is only an unlock, which the server must answer: nothing opens here.
    await userEvent.type(screen.getByLabelText('PIN'), '2468');
    await userEvent.keyboard('{Enter}');
    await waitFor(() => expect(api.post).toHaveBeenCalledWith('/api/v1/auth/unlock', { pin: '2468' }));
    expect(offlineCopy()).not.toBeInTheDocument();
    expect(useSession.getState().locked).toBe(true);
  });

  it('is offered when the server answers but its database is down', async () => {
    await readyForAnOutage();
    useConnection.setState({ serverReachable: true, databaseDown: true });
    renderLocked();

    expect(screen.getByText('HealthCare360 cannot reach its database')).toBeInTheDocument();
    await userEvent.type(screen.getByLabelText('PIN'), '2468');
    await userEvent.keyboard('{Enter}');

    expect(await screen.findByRole('dialog', { name: 'Offline copy (read only)' })).toBeInTheDocument();
  });

  it('keeps the PIN typed when unlocking fails for the server, so it opens the copy once the outage shows', async () => {
    await readyForAnOutage();
    vi.mocked(api.post).mockImplementation(async (path: string) => {
      if (path === '/api/v1/auth/unlock') {
        // The answer while the database is down; the app then finds it down (`noteServerFailure`).
        throw new ApiError({ status: 500, title: 'Something went wrong' });
      }
      return undefined;
    });
    renderLocked();

    await userEvent.type(screen.getByLabelText('PIN'), '2468');
    await userEvent.click(screen.getByRole('button', { name: 'Unlock' }));
    expect(await screen.findByText(/could not unlock the screen: try again once it is back/)).toBeInTheDocument();
    act(() => useConnection.setState({ databaseDown: true }));
    await userEvent.click(screen.getByRole('button', { name: 'Open the offline copy' }));

    expect(await screen.findByRole('dialog', { name: 'Offline copy (read only)' })).toBeInTheDocument();
    expect(useSession.getState().locked).toBe(true);
  });

  it('is not offered while the server can unlock the screen', async () => {
    await readyForAnOutage();
    renderLocked();

    expect(screen.queryByRole('button', { name: 'Open the offline copy' })).not.toBeInTheDocument();
    expect(screen.queryByText('Cannot reach the HealthCare360 server')).not.toBeInTheDocument();
  });

  it('closes the copy again on the idle lock timer, and asks for the PIN', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    const typist = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    await readyForAnOutage();
    useConnection.setState({ serverReachable: false });
    renderLocked(user('locked'), { 'ui.idleLockMinutes': 2 });

    await typist.type(screen.getByLabelText('PIN'), '2468');
    await typist.click(screen.getByRole('button', { name: 'Open the offline copy' }));
    await screen.findByRole('dialog', { name: 'Offline copy (read only)' });
    // The copy opened once the check of the PIN was done, after the click: let it start its idle timer.
    await act(() => Promise.resolve());

    act(() => vi.advanceTimersByTime(110_000));
    expect(offlineCopy()).toBeInTheDocument();
    act(() => vi.advanceTimersByTime(15_000));

    expect(await screen.findByLabelText('PIN')).toHaveValue('');
    expect(offlineCopy()).not.toBeInTheDocument();
  });

  it('opens with the PIN the server last accepted on this computer, once the server is down', async () => {
    openOfflineUnlock('u1', SERVER_KEY);
    useOfflineCopy.setState({ userId: 'u1', takenAt: Date.now(), pack: PACK, worklists: [] });
    vi.mocked(api.post).mockImplementation(async (path: string) =>
      path === '/api/v1/auth/unlock' ? { accessToken: 'fresh-token', expiresAt: null, user: user() } : undefined,
    );
    renderLocked();
    await userEvent.type(screen.getByLabelText('PIN'), '2468');
    await userEvent.click(screen.getByRole('button', { name: 'Unlock' }));
    await waitFor(() => expect(useSession.getState().locked).toBe(false));
    await waitFor(() => expect(localStorage.getItem('hc360:offline-unlock')).toContain('"pin"'));
    expect(localStorage.getItem('hc360:offline-unlock')).not.toContain('2468');

    // Later the server goes down, and the screen locks again.
    act(() => {
      useConnection.setState({ serverReachable: false });
      useSession.getState().setLocked(true);
    });
    await userEvent.type(await screen.findByLabelText('PIN'), '2468');
    await userEvent.click(screen.getByRole('button', { name: 'Open the offline copy' }));

    expect(await screen.findByRole('dialog', { name: 'Offline copy (read only)' })).toBeInTheDocument();
  });

  it('sends a PIN it has not checked here since sign-in to the password instead', async () => {
    openOfflineUnlock('u1', SERVER_KEY);
    await rememberOfflineSecret('u1', 'password', 'Morning rounds at seven');
    useOfflineCopy.setState({ userId: 'u1', takenAt: Date.now(), pack: PACK, worklists: [] });
    useConnection.setState({ serverReachable: false });
    renderLocked();

    await userEvent.type(screen.getByLabelText('PIN'), '2468');
    await userEvent.click(screen.getByRole('button', { name: 'Open the offline copy' }));

    expect(await screen.findByText(/Use your password: your PIN has not been used/)).toBeInTheDocument();
    await userEvent.type(screen.getByLabelText('Password'), 'Morning rounds at seven');
    await userEvent.click(screen.getByRole('button', { name: 'Open the offline copy' }));
    expect(await screen.findByRole('dialog', { name: 'Offline copy (read only)' })).toBeInTheDocument();
  });
});
