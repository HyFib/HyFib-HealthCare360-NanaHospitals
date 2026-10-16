import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import {
  rememberedUnlockKeys,
  rememberUnlockKey,
  useSession,
  type AuthUser,
  type WebAuthnCredentialInfo,
} from '@hc360/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { api } from '@/lib/api';
import { DeviceUnlock } from './DeviceUnlock';

vi.mock('@/lib/api', () => ({ api: { get: vi.fn(), post: vi.fn(), delete: vi.fn() } }));

const user = (biometricUnlock = true): AuthUser => ({
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
  restriction: null,
  biometricUnlock,
});

const buffer = (...values: number[]) => new Uint8Array(values).buffer;

const registered: WebAuthnCredentialInfo = {
  id: 'k1',
  name: 'OPD Reception 2',
  credentialId: 'AQID',
  serverName: 'localhost',
  createdAt: '2026-10-16T08:00:00Z',
  lastUsedAt: null,
  usableHere: true,
};

/** A device with Windows Hello on a secure page: `create` answers as the device would once it recognised the person. */
function deviceWithWindowsHello() {
  vi.stubGlobal('isSecureContext', true);
  const PublicKeyCredential = Object.assign(function PublicKeyCredential() {}, {
    isUserVerifyingPlatformAuthenticatorAvailable: async () => true,
  });
  vi.stubGlobal('PublicKeyCredential', PublicKeyCredential);
  const create = vi.fn(async () => ({
    id: 'AQID',
    rawId: buffer(1, 2, 3),
    type: 'public-key',
    response: { clientDataJSON: buffer(9), attestationObject: buffer(8), getTransports: () => ['internal'] },
    getClientExtensionResults: () => ({}),
  }));
  Object.defineProperty(navigator, 'credentials', { configurable: true, value: { create } });
  return create;
}

function renderPage(who = user()) {
  useSession.setState({ status: 'authenticated', accessToken: 'token', user: who, locked: false });
  render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <FluentProvider theme={webLightTheme}>
        <DeviceUnlock />
      </FluentProvider>
    </QueryClientProvider>,
  );
}

describe('DeviceUnlock', () => {
  beforeEach(() => {
    vi.mocked(api.get).mockReset();
    vi.mocked(api.post).mockReset();
    vi.mocked(api.delete).mockReset();
  });
  afterEach(() => {
    vi.unstubAllGlobals();
    Reflect.deleteProperty(navigator, 'credentials');
    localStorage.clear();
    useSession.setState({ status: 'unknown', accessToken: null, user: null, locked: false });
  });

  it('sets up this device after the password, and remembers that it holds the key', async () => {
    const create = deviceWithWindowsHello();
    vi.mocked(api.get).mockResolvedValue([]);
    vi.mocked(api.post).mockImplementation(async (path: string) => {
      if (path === '/api/v1/auth/webauthn/credentials/options') {
        return {
          challengeId: 'c1',
          options: {
            rp: { id: 'localhost', name: 'HealthCare360' },
            user: { id: 'AQID', name: 'asha', displayName: 'Asha Rao' },
            challenge: 'CQgH',
            pubKeyCredParams: [{ type: 'public-key', alg: -7 }],
            authenticatorSelection: { authenticatorAttachment: 'platform', userVerification: 'required' },
            excludeCredentials: [],
          },
        };
      }
      return registered;
    });
    renderPage();

    await userEvent.click(await screen.findByRole('button', { name: 'Set up this device' }));
    const dialog = await screen.findByRole('alertdialog');
    await userEvent.clear(within(dialog).getByLabelText('Name for this device'));
    await userEvent.type(within(dialog).getByLabelText('Name for this device'), 'OPD Reception 2');
    await userEvent.type(within(dialog).getByLabelText(/Your password/), 'Correct-Horse-1');
    await userEvent.click(within(dialog).getByRole('button', { name: 'Set up' }));

    await waitFor(() => expect(rememberedUnlockKeys('u1')).toEqual(['AQID']));
    expect(api.post).toHaveBeenCalledWith('/api/v1/auth/webauthn/credentials/options', { password: 'Correct-Horse-1' });
    const publicKey = (create.mock.calls[0] as unknown as [CredentialCreationOptions])[0].publicKey!;
    expect(publicKey.authenticatorSelection?.userVerification).toBe('required');
    expect(new Uint8Array(publicKey.user.id as ArrayBuffer)).toEqual(new Uint8Array([1, 2, 3]));
    expect(api.post).toHaveBeenCalledWith('/api/v1/auth/webauthn/credentials', {
      challengeId: 'c1',
      name: 'OPD Reception 2',
      credential: expect.objectContaining({
        rawId: 'AQID',
        response: expect.objectContaining({ attestationObject: 'CA' }),
      }),
    });
  });

  it('lists the devices set up, and removes one, which this browser then forgets', async () => {
    deviceWithWindowsHello();
    rememberUnlockKey('u1', 'AQID');
    vi.mocked(api.get).mockResolvedValue([registered]);
    vi.mocked(api.delete).mockResolvedValue(undefined);
    renderPage();

    expect(await screen.findByText('OPD Reception 2')).toBeInTheDocument();
    expect(screen.getByText('This device')).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Remove' }));
    await userEvent.click(within(await screen.findByRole('alertdialog')).getByRole('button', { name: 'Remove' }));

    await waitFor(() => expect(api.delete).toHaveBeenCalledWith('/api/v1/auth/webauthn/credentials/k1'));
    await waitFor(() => expect(rememberedUnlockKeys('u1')).toEqual([]));
  });

  it('says so, and offers nothing to set up, where the hospital turned it off', async () => {
    deviceWithWindowsHello();
    vi.mocked(api.get).mockResolvedValue([]);
    renderPage(user(false));

    expect(await screen.findByText(/Your hospital has turned this off/)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Set up this device' })).not.toBeInTheDocument();
  });
});
