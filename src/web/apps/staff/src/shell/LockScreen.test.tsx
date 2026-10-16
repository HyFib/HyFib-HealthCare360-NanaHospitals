import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { ApiError, rememberedUnlockKeys, rememberUnlockKey, useSession, type AuthUser } from '@hc360/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { useState } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { api } from '@/lib/api';
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

function renderLocked(who: AuthUser = user('locked')) {
  useSession.setState({ status: 'authenticated', accessToken: 'locked-token', user: who, locked: true });
  const client = new QueryClient();
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
