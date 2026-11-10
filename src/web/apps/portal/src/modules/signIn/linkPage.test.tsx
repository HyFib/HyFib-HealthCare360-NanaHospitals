import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { useSession } from '@hc360/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { createMemoryHistory, RouterProvider } from '@tanstack/react-router';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { useLanguageStore } from '@/i18n';
import { useFeatureRefusals } from '@/lib/featureSwitches';
import { clearPreparedProofs } from '@/lib/proofOfWork';
import { createPortalRouter } from '@/router';

/**
 * A link to the portal sent to the patient's registered number, end to end in jsdom (router, layouts, queries, i18n)
 * against a fake server: the page shows the hospital and the number masked and changes nothing; "Send code" asks for a
 * code through the link with a proof of work; the code is checked by the portal's own sign-in, and then the patient is
 * where the sign-in goes, with the token forgotten. A link that does not work offers the sign-in page instead.
 */

const TOKEN = 'Lnk_0123456789abcdefgh';
const GONE = 'Old_0123456789abcdefgh';
const CHALLENGE_ID = '0199b0c4-4b1c-7c3e-9d2f-2a1b3c4d5e6f';
const PATIENT_ID = '5f0c6d3e-8c1a-4a53-9a3f-2b1d6f7e9a10';
const SLOW = { timeout: 8000 };

/** The challenge the fake server hands out: almost no work, and it does not check the answer. */
const PROOF_CHALLENGE = '1.otp.1.1.00.sig';
const SOLVED_PROOF = /^1\.otp\.1\.1\.00\.sig:\d+$/;

const calls: { method: string; path: string; proof: string | null; body: string | null }[] = [];

/** Set by a test: the link stopped working after the page opened (a newer one was sent). */
let replacedMeanwhile = false;

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'content-type': status >= 400 ? 'application/problem+json' : 'application/json' },
  });
}

const notFound = () => json({ title: 'Not found', status: 404, detail: 'This link no longer works.' }, 404);

const fakeServer = vi.fn(async (input: RequestInfo | URL, init?: RequestInit): Promise<Response> => {
  const url = new URL(
    typeof input === 'string' ? input : input instanceof URL ? input.href : input.url,
    'https://portal.test',
  );
  const method = (init?.method ?? 'GET').toUpperCase();
  const headers = new Headers(init?.headers);
  calls.push({
    method,
    path: url.pathname,
    proof: headers.get('X-HC360-Proof'),
    body: typeof init?.body === 'string' ? init.body : null,
  });
  const path = url.pathname;
  if (path === '/api/v1/public/challenge') {
    return json({ required: true, challenge: PROOF_CHALLENGE, bits: 1, expiresAt: null });
  }
  if (path === '/api/v1/system/info') {
    return json({ facilityName: 'Nana Hospitals', features: { patientPortal: true, telehealth: true } });
  }
  if (path === `/api/v1/public/portal-links/${TOKEN}` && method === 'GET') {
    return json({ hospital: 'Nana Hospitals, Pune', maskedPhone: '+91******3210', expiresAt: '2099-01-01T00:00:00Z' });
  }
  if (path === `/api/v1/public/portal-links/${TOKEN}/otp` && method === 'POST') {
    return replacedMeanwhile
      ? notFound()
      : json({
          challengeId: CHALLENGE_ID,
          expiresAt: new Date(Date.now() + 5 * 60_000).toISOString(),
          maskedPhone: '+91******3210',
        });
  }
  if (path.startsWith('/api/v1/public/portal-links/')) {
    return notFound();
  }
  if (path === '/api/v1/auth/otp/verify') {
    return json({
      accessToken: 'portal-token',
      expiresAt: new Date(Date.now() + 15 * 60_000).toISOString(),
      user: {
        id: 'user-1',
        userName: 'p919876543210',
        displayName: 'Asha Verma',
        type: 'patient',
        facilityId: null,
        facilityName: null,
        practitionerId: null,
        patientId: PATIENT_ID,
        roles: ['Patient'],
        permissions: ['portal.access'],
        facilities: [],
        mfaEnabled: false,
        hasPin: false,
        mustChangePassword: false,
        restriction: null,
      },
    });
  }
  if (path === '/api/v1/portal/profiles') {
    return json([]);
  }
  if (path.startsWith('/api/v1/auth/')) {
    return json({ title: 'Sign in', status: 401 }, 401);
  }
  return json({ title: 'Not found', status: 404 }, 404);
});

beforeAll(() => {
  window.matchMedia ??= ((query: string) => ({
    matches: false,
    media: query,
    onchange: null,
    addEventListener: () => undefined,
    removeEventListener: () => undefined,
    addListener: () => undefined,
    removeListener: () => undefined,
    dispatchEvent: () => false,
  })) as typeof window.matchMedia;
  window.scrollTo = (() => undefined) as typeof window.scrollTo;
});

beforeEach(() => {
  calls.length = 0;
  replacedMeanwhile = false;
  useFeatureRefusals.getState().clear();
  clearPreparedProofs();
  useSession.getState().clear();
  vi.stubGlobal('fetch', fakeServer);
});

afterEach(async () => {
  // The page finishes loading before it goes. The first test ends once the address is the home page, while the home page
  // is still loading: left running, that load reaches the real fetch once the fake server is unstubbed, and can answer
  // after the test file's window is gone, which fails the run ("window is not defined") although every test passed.
  const opened = router;
  router = null;
  if (opened) {
    await waitFor(() => expect([opened.state.status, opened.state.isLoading]).toEqual(['idle', false]), SLOW);
  }
  vi.unstubAllGlobals();
  sessionStorage.clear();
  useLanguageStore.getState().setLanguage('en');
});

/** The router of the page the test opened. */
let router: ReturnType<typeof createPortalRouter> | null = null;

function openAt(path: string) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const history = createMemoryHistory({ initialEntries: [path] });
  render(
    <QueryClientProvider client={queryClient}>
      <FluentProvider theme={webLightTheme}>
        <RouterProvider router={(router = createPortalRouter(queryClient, history))} />
      </FluentProvider>
    </QueryClientProvider>,
  );
  return history;
}

const linkCalls = () => calls.filter((call) => call.path.startsWith('/api/v1/public/portal-links/'));

describe('a link to the portal', () => {
  it('sends a code to the registered number, signs the patient in with it and forgets the link', async () => {
    const history = openAt(`/portal/link/${TOKEN}`);

    expect(await screen.findByText('Nana Hospitals, Pune', {}, SLOW)).toBeInTheDocument();
    expect(screen.getByText(/We will send a sign-in code by SMS to \+91\*{6}3210/)).toBeInTheDocument();
    // The token leaves the address bar before anything is shown, and opening the page sends nothing.
    expect(history.location.pathname).toBe('/portal/link/_');
    expect(sessionStorage.getItem('hc360:portal:link:portal')).toBe(TOKEN);
    expect(linkCalls().map((call) => call.method)).toEqual(['GET']);

    await userEvent.click(screen.getByRole('button', { name: 'Send code' }));
    await userEvent.type(await screen.findByRole('textbox', { name: '6-digit code' }, SLOW), '482913');

    await waitFor(() => expect(history.location.pathname).toBe('/portal/home'), SLOW);
    const requested = calls.find(
      (call) => call.method === 'POST' && call.path === `/api/v1/public/portal-links/${TOKEN}/otp`,
    );
    expect(requested?.proof).toMatch(SOLVED_PROOF);
    const verified = calls.find((call) => call.path === '/api/v1/auth/otp/verify');
    expect(verified?.proof).toMatch(SOLVED_PROOF);
    expect(JSON.parse(verified?.body ?? '{}')).toEqual({ challengeId: CHALLENGE_ID, code: '482913' });
    expect(sessionStorage.getItem('hc360:portal:link:portal')).toBeNull();
  });

  it('says in Hindi what it will do', async () => {
    useLanguageStore.getState().setLanguage('hi');
    openAt(`/portal/link/${TOKEN}`);

    expect(await screen.findByText(/हम \+91\*{6}3210 पर SMS से साइन इन का कोड भेजेंगे/, {}, SLOW)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'कोड भेजें' })).toBeInTheDocument();
  });

  it('offers the sign-in page when the link no longer works', async () => {
    openAt(`/portal/link/${GONE}`);

    expect(await screen.findByText('This link no longer works', {}, SLOW)).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Sign in with your mobile number' })).toHaveAttribute(
      'href',
      '/portal/sign-in',
    );
    expect(screen.queryByRole('button', { name: 'Send code' })).not.toBeInTheDocument();
  });

  it('says so when the link stopped working after the page opened', async () => {
    replacedMeanwhile = true;
    openAt(`/portal/link/${TOKEN}`);

    await userEvent.click(await screen.findByRole('button', { name: 'Send code' }, SLOW));

    expect(await screen.findByText('This link no longer works', {}, SLOW)).toBeInTheDocument();
    expect(calls.some((call) => call.path === '/api/v1/auth/otp/verify')).toBe(false);
  });

  it('does not ask the server about something that is not a link', async () => {
    openAt('/portal/link/not-a-token');

    expect(await screen.findByText('This link no longer works', {}, SLOW)).toBeInTheDocument();
    expect(linkCalls()).toEqual([]);
  });
});
