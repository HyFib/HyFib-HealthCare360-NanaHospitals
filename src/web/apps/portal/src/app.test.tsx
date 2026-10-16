import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { createMemoryHistory, RouterProvider } from '@tanstack/react-router';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { useFeatureRefusals } from './lib/featureSwitches';
import { createPortalRouter } from './router';

/**
 * The public pages end to end in jsdom (router, layouts, queries, i18n), with the server replaced by a fake fetch:
 * the links the hospital sends or prints must open without signing in, and personal tokens must leave the address bar.
 */

const TOKEN = 'Abcdefghijklmnop_123';
const SESSION_ID = '0199b0c4-4b1c-7c3e-9d2f-2a1b3c4d5e6f';
const RX_TOKEN = 'Rx_token-0123456789ab';
const DSC_TOKEN = 'Dsc_token-0123456789ab';
const DSC_HASH = '2eacf6b6cd60daa5cf8e87fa9a5709f8f7d3316e3921e7f6b4783c60d027569d';
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

/** Set by a test to make the anonymous privacy contact endpoint fail (it is rate-limited in production). */
let privacyContactFails = false;

/** The hospital's feature switches, as `GET /api/v1/system/info` reports them. */
let switches = { patientPortal: true, telehealth: true };

/** Set by a test: the portal was turned off after the page loaded, so sign-in is refused. */
let signInRefused = false;

/** Set by a test: the server's re-check finds the DSC-signed prescription changed. */
let dscTampered = false;

/** Set by a test: the certificate does not chain to a CCA India root installed on the server. */
let dscUntrusted = false;

/** The first page of a run also loads its lazily split route code. */
const SLOW = { timeout: 8000 };

const fakeServer = vi.fn(async (input: RequestInfo | URL, init?: RequestInit): Promise<Response> => {
  const url = typeof input === 'string' ? input : input instanceof URL ? input.href : input.url;
  const method = (init?.method ?? (input instanceof Request ? input.method : 'GET')).toUpperCase();
  requests.push({ method, url });
  if (url.includes('/api/v1/auth/otp/request') && signInRefused) {
    return new Response(
      JSON.stringify({
        title: 'The patient portal is turned off',
        status: 503,
        detail: 'The patient portal is not available right now. Please contact the hospital.',
        code: 'feature.disabled',
        data: { feature: 'patientPortal' },
      }),
      { status: 503, headers: { 'content-type': 'application/problem+json' } },
    );
  }
  if (url.includes('/api/v1/auth/')) {
    return json({ title: 'Sign in' }, 401);
  }
  if (url.includes('/api/v1/system/info')) {
    return json({ facilityName: 'Nana Hospitals', features: switches });
  }
  if (url.includes(`/api/v1/public/feedback/invitations/${TOKEN}`)) {
    return method === 'GET' ? json(FORM) : json({ thankYou: 'Thank you for your feedback.', complaintNumber: null });
  }
  if (url.includes(`/api/v1/public/prescriptions/verify/${DSC_TOKEN}`)) {
    return json({
      found: true,
      status: 'Valid',
      doctor: 'Dr. Meera Iyer',
      qualification: 'MBBS, MD',
      registrationNumber: 'KMC 12345',
      facility: 'Nana Hospitals',
      issuedOn: '2026-09-20',
      itemCount: 3,
      prescriptionNumber: 'NHRX260000123',
      digitalSignature: {
        state: dscTampered ? 'Invalid' : dscUntrusted ? 'Untrusted' : 'Valid',
        signerName: 'Meera Iyer',
        certificateSerialNumber: '18B3D635546B372D',
        certificateIssuer: 'CN=e-Mudhra Sub CA for Class 3 Individual 2022',
        signedAt: '2026-09-20T05:30:00Z',
        contentHash: DSC_HASH,
        chainTrusted: !dscUntrusted,
        message: dscTampered ? 'The prescription was changed after it was signed.' : 'The signature is valid.',
      },
      signaturePath: `/api/v1/public/prescriptions/verify/${DSC_TOKEN}/signature.p7s`,
    });
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
  if (url.endsWith('/api/v1/public/privacy')) {
    if (privacyContactFails) {
      return json({ title: 'Too many requests', status: 429 }, 429);
    }
    return json({
      hospital: 'Nana Hospitals',
      contact: {
        title: 'Grievance Officer',
        name: 'Anita Rao',
        email: 'privacy@nanahospitals.example',
        phone: '+919876543210',
        address: 'Nana Hospitals, Pune 411001',
      },
      responseDays: 30,
    });
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
  privacyContactFails = false;
  switches = { patientPortal: true, telehealth: true };
  signInRefused = false;
  useFeatureRefusals.getState().clear();
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

  it('shows the doctor DSC signature, its certificate and hash, and offers the .p7s', async () => {
    dscTampered = false;
    dscUntrusted = false;
    openAt(`/portal/verify/prescription/${DSC_TOKEN}`);
    expect(await screen.findByText('Genuine document', {}, SLOW)).toBeInTheDocument();
    expect(screen.getByText('NHRX260000123')).toBeInTheDocument();
    expect(screen.getByText('Digital signature')).toBeInTheDocument();
    expect(screen.getByText(/from a licensed Certifying Authority. The signature checks out/)).toBeInTheDocument();
    expect(screen.getByText('Licensed (chains to the CCA India root)')).toBeInTheDocument();
    expect(
      screen.getByText(/does not ask the Certifying Authority whether the certificate was revoked/),
    ).toBeInTheDocument();
    expect(screen.getByText('18B3D635546B372D')).toBeInTheDocument();
    expect(screen.getByText(DSC_HASH)).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Download the signature (.p7s)' })).toHaveAttribute(
      'href',
      `/api/v1/public/prescriptions/verify/${DSC_TOKEN}/signature.p7s`,
    );
  });

  it('does not present a certificate the server could not tie to a licensed CA as a DSC', async () => {
    dscTampered = false;
    dscUntrusted = true;
    openAt(`/portal/verify/prescription/${DSC_TOKEN}`);
    expect(
      await screen.findByText(/could not confirm that the certificate is a Digital Signature Certificate/, {}, SLOW),
    ).toBeInTheDocument();
    expect(screen.getByText('Not confirmed')).toBeInTheDocument();
    expect(screen.queryByText(/The signature checks out/)).not.toBeInTheDocument();
  });

  it('warns not to dispense when the DSC signature no longer checks out', async () => {
    dscTampered = true;
    dscUntrusted = false;
    openAt(`/portal/verify/prescription/${DSC_TOKEN}`);
    expect(await screen.findAllByText(/Do not dispense from this prescription/, {}, SLOW)).not.toHaveLength(0);
    expect(screen.queryByText('Genuine document')).not.toBeInTheDocument();
  });

  it('says "not found" for an unknown report code', async () => {
    openAt(`/portal/verify/lab/${LAB_CODE.toLowerCase()}`);
    expect(await screen.findByText('Not found', {}, SLOW)).toBeInTheDocument();
    expect(requests.some((request) => request.url.endsWith(`/api/v1/lab/reports/verify/${LAB_CODE}`))).toBe(true);
  });
});

describe('public pages', () => {
  it('shows the privacy notice without sign-in, with the grievance officer the hospital publishes', async () => {
    openAt('/portal/privacy');
    expect(await screen.findByRole('heading', { name: 'Privacy notice', level: 1 }, SLOW)).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Sign in' })).toBeInTheDocument();
    expect(await screen.findByText('Anita Rao', {}, SLOW)).toBeInTheDocument();
    expect(screen.getByText('Grievance Officer')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'privacy@nanahospitals.example' })).toHaveAttribute(
      'href',
      'mailto:privacy@nanahospitals.example',
    );
    expect(screen.getByRole('link', { name: '+91 98765 43210' })).toHaveAttribute('href', 'tel:+919876543210');
    expect(screen.getByText('We reply within 30 days.')).toBeInTheDocument();
    expect(screen.getByText(/Data Protection Board of India/)).toBeInTheDocument();
    // "Your privacy" needs a signed-in patient.
    expect(screen.queryByRole('link', { name: 'Go to Your privacy' })).not.toBeInTheDocument();
    expect(requests.some((request) => request.url.endsWith('/api/v1/public/privacy'))).toBe(true);
  });

  it('keeps the privacy notice when the grievance officer cannot be loaded', async () => {
    privacyContactFails = true;
    openAt('/portal/privacy');
    expect(await screen.findByRole('heading', { name: 'Privacy notice', level: 1 }, SLOW)).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'Your rights' })).toBeInTheDocument();
    await waitFor(() => expect(requests.some((request) => request.url.endsWith('/api/v1/public/privacy'))).toBe(true));
    expect(screen.queryByRole('heading', { name: 'Whom to contact' })).not.toBeInTheDocument();
  });

  it('sends signed-out visitors of a signed-in page to sign-in', async () => {
    const history = openAt('/portal/bills');
    expect(await screen.findByRole('button', { name: /code/i }, SLOW)).toBeInTheDocument();
    expect(history.location.pathname).toBe('/portal/sign-in');
  });
});

describe('when the hospital turns the portal or video consultations off', () => {
  const unavailable = 'The patient portal is not available right now';

  it('explains, instead of offering sign-in, that the portal is not available', async () => {
    switches = { patientPortal: false, telehealth: true };
    openAt('/portal/sign-in');

    expect(await screen.findByText(unavailable, {}, SLOW)).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'Nana Hospitals patient portal', level: 1 })).toBeInTheDocument();
    expect(screen.getByText('Please contact the hospital.')).toBeInTheDocument();
    expect(screen.queryByRole('textbox', { name: /Mobile number/ })).not.toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Check a document' })).toBeInTheDocument();
    expect(requests.some((request) => request.url.includes('/otp/'))).toBe(false);
  });

  it('gives a signed-out visitor of a signed-in page the same explanation', async () => {
    switches = { patientPortal: false, telehealth: true };
    const history = openAt('/portal/bills');

    expect(await screen.findByText(unavailable, {}, SLOW)).toBeInTheDocument();
    expect(history.location.pathname).toBe('/portal/sign-in');
  });

  it('keeps online booking closed', async () => {
    switches = { patientPortal: false, telehealth: true };
    openAt('/portal/doctors');

    expect(await screen.findByText(unavailable, {}, SLOW)).toBeInTheDocument();
    expect(requests.some((request) => request.url.includes('/api/v1/public/doctors'))).toBe(false);
  });

  it('still checks the QR code printed on a prescription', async () => {
    switches = { patientPortal: false, telehealth: false };
    openAt(`/portal/verify/prescription/${RX_TOKEN}`);

    expect(await screen.findByText('Genuine document', {}, SLOW)).toBeInTheDocument();
    expect(screen.queryByText(unavailable)).not.toBeInTheDocument();
  });

  it('explains when sign-in is refused because the portal was just turned off', async () => {
    signInRefused = true;
    openAt('/portal/sign-in');

    await userEvent.type(await screen.findByRole('textbox', { name: /Mobile number/ }, SLOW), '9876543210');
    await userEvent.click(screen.getByRole('button', { name: 'Send code' }));

    expect(await screen.findByText(unavailable, {}, SLOW)).toBeInTheDocument();
    expect(screen.queryByRole('textbox', { name: /Mobile number/ })).not.toBeInTheDocument();
  });

  it('offers sign-in again once the hospital turns the portal back on', async () => {
    switches = { patientPortal: false, telehealth: true };
    openAt('/portal/sign-in');
    expect(await screen.findByText(unavailable, {}, SLOW)).toBeInTheDocument();

    switches = { patientPortal: true, telehealth: true };
    await userEvent.click(screen.getByRole('button', { name: 'Try again' }));

    expect(await screen.findByRole('textbox', { name: /Mobile number/ }, SLOW)).toBeInTheDocument();
    expect(screen.queryByText(unavailable)).not.toBeInTheDocument();
  });

  it('says video consultations are not available on a join link while they are off', async () => {
    switches = { patientPortal: true, telehealth: false };
    openAt(`/portal/telehealth/${SESSION_ID}?t=${TOKEN}`);

    expect(await screen.findByText('Video consultations are not available right now', {}, SLOW)).toBeInTheDocument();
    expect(screen.getByText('Please contact the hospital to see the doctor in person.')).toBeInTheDocument();
  });
});
