import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { createMemoryHistory, RouterProvider } from '@tanstack/react-router';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPortalRouter } from './router';

/**
 * The public pages end to end in jsdom (router, layouts, queries, i18n), with the server replaced by a fake fetch:
 * the links the hospital sends or prints must open without signing in, and personal tokens must leave the address bar.
 */

const TOKEN = 'Abcdefghijklmnop_123';
const RX_TOKEN = 'Rx_token-0123456789ab';
const LAB_CODE = 'ABCDEFGHJKLMNPQRSTUV';

const FORM = {
  title: 'Your stay with us',
  introText: null,
  aspects: ['Overall'],
  includesNps: true,
  allowsComment: true,
  hospital: 'Nana Hospitals',
};

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'content-type': 'application/json' } });
}

const requests: { method: string; url: string }[] = [];

/** The first page of a run also loads its lazily split route code. */
const SLOW = { timeout: 8000 };

const fakeServer = vi.fn(async (input: RequestInfo | URL, init?: RequestInit): Promise<Response> => {
  const url = typeof input === 'string' ? input : input instanceof URL ? input.href : input.url;
  const method = (init?.method ?? (input instanceof Request ? input.method : 'GET')).toUpperCase();
  requests.push({ method, url });
  if (url.includes('/api/v1/auth/')) {
    return json({ title: 'Sign in' }, 401);
  }
  if (url.includes('/api/v1/system/info')) {
    return json({ facilityName: 'Nana Hospitals' });
  }
  if (url.includes(`/api/v1/public/feedback/invitations/${TOKEN}`)) {
    return method === 'GET' ? json(FORM) : json({ thankYou: 'Thank you for your feedback.', complaintNumber: null });
  }
  if (url.includes(`/api/v1/public/prescriptions/verify/${RX_TOKEN}`)) {
    return json({
      found: true,
      status: 'Valid',
      doctor: 'Dr. Meera Iyer',
      qualification: 'MBBS, MD',
      registrationNumber: 'KMC 12345',
      facility: 'Nana Hospitals',
      issuedOn: '2026-09-20',
      itemCount: 3,
    });
  }
  if (url.includes(`/api/v1/public/unsubscribe/${TOKEN}`)) {
    return method === 'GET' ? json({ alreadyOptedOut: false }) : new Response(null, { status: 204 });
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
  requests.length = 0;
  vi.stubGlobal('fetch', fakeServer);
});

afterEach(() => {
  vi.unstubAllGlobals();
  sessionStorage.clear();
});

function openAt(path: string) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const history = createMemoryHistory({ initialEntries: [path] });
  const router = createPortalRouter(queryClient, history);
  render(
    <QueryClientProvider client={queryClient}>
      <FluentProvider theme={webLightTheme}>
        <RouterProvider router={router} />
      </FluentProvider>
    </QueryClientProvider>,
  );
  return history;
}

describe('links the hospital sends', () => {
  it('opens a personal feedback link without sign-in and takes the token out of the address bar', async () => {
    const history = openAt(`/portal/feedback/${TOKEN}`);
    expect(await screen.findByRole('heading', { name: 'Your stay with us', level: 1 }, SLOW)).toBeInTheDocument();
    expect(history.location.pathname).toBe('/portal/feedback/_');
    expect(sessionStorage.getItem('hc360:portal:link:feedback')).toBe(TOKEN);
    expect(requests.some((request) => request.url.endsWith(`/api/v1/public/feedback/invitations/${TOKEN}`))).toBe(true);

    await userEvent.click(screen.getByRole('radio', { name: '10 = extremely likely' }));
    await userEvent.click(screen.getByRole('button', { name: 'Send feedback' }));
    expect(await screen.findByText('Thank you for your feedback.', {}, SLOW)).toBeInTheDocument();
    expect(
      requests.some((request) => request.method === 'POST' && request.url.includes('/feedback/invitations/')),
    ).toBe(true);
  });

  it('says so when a feedback link is not a link at all', async () => {
    openAt('/portal/feedback/not-a-token');
    expect(await screen.findByText('This link does not work', {}, SLOW)).toBeInTheDocument();
    expect(requests.some((request) => request.url.includes('/public/feedback/'))).toBe(false);
  });

  it('asks before stopping campaign messages, and hides the token', async () => {
    const history = openAt(`/portal/unsubscribe/${TOKEN}`);
    const confirm = await screen.findByRole('button', { name: 'Stop these messages' }, SLOW);
    expect(history.location.pathname).toBe('/portal/unsubscribe/_');
    expect(requests.some((request) => request.method === 'POST')).toBe(false);
    await userEvent.click(confirm);
    expect(await screen.findByText('You will not get these messages any more', {}, SLOW)).toBeInTheDocument();
    expect(
      requests.some(
        (request) => request.method === 'POST' && request.url.endsWith(`/api/v1/public/unsubscribe/${TOKEN}`),
      ),
    ).toBe(true);
  });
});

describe('printed QR codes', () => {
  it('confirms a genuine prescription with authenticity details only', async () => {
    openAt(`/portal/verify/prescription/${RX_TOKEN}`);
    expect(await screen.findByText('Genuine document', {}, SLOW)).toBeInTheDocument();
    expect(screen.getByText('Dr. Meera Iyer')).toBeInTheDocument();
    expect(screen.getByText('KMC 12345')).toBeInTheDocument();
  });

  it('says "not found" for an unknown report code', async () => {
    openAt(`/portal/verify/lab/${LAB_CODE.toLowerCase()}`);
    expect(await screen.findByText('Not found', {}, SLOW)).toBeInTheDocument();
    expect(requests.some((request) => request.url.endsWith(`/api/v1/lab/reports/verify/${LAB_CODE}`))).toBe(true);
  });
});

describe('public pages', () => {
  it('shows the privacy notice without sign-in', async () => {
    openAt('/portal/privacy');
    expect(await screen.findByRole('heading', { name: 'Privacy notice', level: 1 }, SLOW)).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Sign in' })).toBeInTheDocument();
  });

  it('sends signed-out visitors of a signed-in page to sign-in', async () => {
    const history = openAt('/portal/bills');
    expect(await screen.findByRole('button', { name: /code/i }, SLOW)).toBeInTheDocument();
    expect(history.location.pathname).toBe('/portal/sign-in');
  });
});
