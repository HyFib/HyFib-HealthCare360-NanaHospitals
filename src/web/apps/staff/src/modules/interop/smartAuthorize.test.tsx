import { Hc360ThemeProvider } from '@hc360/ui';
import { expectNoAxeViolations } from '@hc360/ui/testing';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { stubBrowserApis } from '@/test/appScreen';
import { api } from '@/lib/api';
import { describeScope, SmartAuthorizeScreen } from './smartAuthorize';

vi.mock('@/lib/api', () => ({ api: { get: vi.fn(), post: vi.fn() } }));

const search = {
  client_id: 'hc360-abc',
  redirect_uri: 'https://viewer.test/callback',
  scope: 'launch/patient patient/*.rs',
  state: 'xyz',
  code_challenge: 'c'.repeat(43),
  code_challenge_method: 'S256',
};

function renderScreen(onRedirect = vi.fn()) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  const view = render(
    <Hc360ThemeProvider>
      <QueryClientProvider client={client}>
        <SmartAuthorizeScreen search={search} onRedirect={onRedirect} />
      </QueryClientProvider>
    </Hc360ThemeProvider>,
  );
  return { ...view, onRedirect };
}

beforeEach(() => {
  stubBrowserApis();
});

describe('describeScope', () => {
  it('says a scope in words', () => {
    expect(describeScope('patient/*.rs')).toBe('Read and search every kind of record of the chosen patient');
    expect(describeScope('user/Observation.read')).toBe('Read and search Observation that you may read');
    expect(describeScope('patient/Patient.r')).toBe('Read Patient of the chosen patient');
  });
});

describe('SmartAuthorizeScreen', () => {
  beforeEach(() => {
    vi.mocked(api.get).mockResolvedValue({
      clientName: 'Referral viewer',
      clientId: 'hc360-abc',
      scopes: ['patient/*.rs'],
      needsPatient: true,
    });
    vi.mocked(api.post).mockResolvedValue({ redirect: 'https://viewer.test/callback?code=c1&state=xyz' });
  });

  it('authorizes the app for the patient named, and sends the browser back to it', async () => {
    const { baseElement, onRedirect } = renderScreen();
    expect(await screen.findByText('Read and search every kind of record of the chosen patient')).toBeInTheDocument();
    const allow = screen.getByRole('button', { name: 'Allow' });
    expect(allow).toBeDisabled();
    await userEvent.type(screen.getByRole('textbox', { name: /UHID/ }), 'NH26000123');
    await userEvent.click(allow);
    await waitFor(() => expect(onRedirect).toHaveBeenCalledWith('https://viewer.test/callback?code=c1&state=xyz'));
    expect(api.post).toHaveBeenCalledWith(
      '/api/v1/smart/authorize',
      expect.objectContaining({
        clientId: 'hc360-abc',
        patientUhid: 'NH26000123',
        approve: true,
        codeChallengeMethod: 'S256',
      }),
    );
    await expectNoAxeViolations(baseElement);
  });

  it('turns the app down', async () => {
    vi.mocked(api.post).mockResolvedValue({ redirect: 'https://viewer.test/callback?error=access_denied&state=xyz' });
    const { onRedirect } = renderScreen();
    await userEvent.click(await screen.findByRole('button', { name: 'Deny' }));
    await waitFor(() =>
      expect(onRedirect).toHaveBeenCalledWith('https://viewer.test/callback?error=access_denied&state=xyz'),
    );
    expect(api.post).toHaveBeenCalledWith('/api/v1/smart/authorize', expect.objectContaining({ approve: false }));
  });

  it('says so when the app is not registered here', async () => {
    vi.mocked(api.get).mockRejectedValue(new Error('unknown'));
    const { baseElement } = renderScreen();
    expect(await screen.findByText(/not registered here/)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Allow' })).not.toBeInTheDocument();
    await expectNoAxeViolations(baseElement);
  });
});
