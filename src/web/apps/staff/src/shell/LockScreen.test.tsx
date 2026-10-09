import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { ApiError, useSession, type AuthUser } from '@hc360/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { api } from '@/lib/api';
import { LockScreen } from './LockScreen';

// The server is outside the test: what the lock screen sends and does with the answers is what is tested.
vi.mock('@/lib/api', () => ({ api: { post: vi.fn() } }));
vi.mock('@tanstack/react-router', () => ({ useNavigate: () => vi.fn() }));

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

function renderLocked() {
  useSession.setState({ status: 'authenticated', accessToken: 'locked-token', user: user('locked'), locked: true });
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

describe('LockScreen', () => {
  beforeEach(() => vi.mocked(api.post).mockReset());
  afterEach(() => useSession.setState({ status: 'unknown', accessToken: null, user: null, locked: false }));

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
});
