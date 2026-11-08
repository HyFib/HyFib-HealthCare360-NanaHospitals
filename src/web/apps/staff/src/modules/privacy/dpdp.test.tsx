import type * as Core from '@hc360/core';
import { useSession } from '@hc360/core';
import { Hc360ThemeProvider } from '@hc360/ui';
import { expectNoAxeViolations } from '@hc360/ui/testing';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fakeServer, openScreen, SCREEN_TESTS, SLOW, STAFF_USER, stubBrowserApis } from '@/test/appScreen';
import { AUDITS, CONSENT_MANAGERS, DPIAS, SDF } from '@/test/roleAccess/privacy';
import { isChild } from './childAge';
import type { GuardianConsentView } from './dpdpApi';
import { GuardianConsentCard } from './GuardianConsentCard';

// The DPDP duties' staff screens: the Significant Data Fiduciary register (a DPIA written as a draft, then sent for a
// second person's approval), the consent managers (registered, the signing secret shown once), and a child's record,
// where a parent's or guardian's consent is recorded and verified by a code to their mobile number.

vi.mock('@hc360/core', async (importOriginal) => ({
  ...(await importOriginal<typeof Core>()),
  RealtimeProvider: ({ children }: { children: ReactNode }) => children,
}));

const CHILD_ID = '0192a000-0000-7000-8000-0000000c0001';
const CONSENT_ID = '0192a000-0000-7000-8000-0000000c0002';

const NOT_YET: GuardianConsentView = {
  patientId: CHILD_ID,
  isChild: true,
  dateOfBirth: '2017-06-01',
  verified: false,
  consents: [],
};

let server: ReturnType<typeof fakeServer>;

beforeEach(() => {
  stubBrowserApis();
  server = fakeServer({
    'GET /api/v1/privacy/sdf': SDF,
    'GET /api/v1/privacy/sdf/dpias': DPIAS,
    'GET /api/v1/privacy/sdf/audits': AUDITS,
    'POST /api/v1/privacy/sdf/dpias': {
      ...DPIAS[1],
      id: '0192a000-0000-7000-8000-0000000d0009',
      number: 'DPIA26003',
      version: 1,
    },
    [`GET /api/v1/privacy/sdf/dpias/${DPIAS[1]!.id}`]: { ...DPIAS[1], version: 4 },
    [`POST /api/v1/privacy/sdf/dpias/${DPIAS[1]!.id}/submit`]: { id: '0192a000-0000-7000-8000-0000000e0001' },
    'GET /api/v1/privacy/consent-managers': CONSENT_MANAGERS,
    'POST /api/v1/privacy/consent-managers': {
      id: '0192a000-0000-7000-8000-0000000c0009',
      code: 'cm-two',
      secret: 'shown-once-secret-value',
    },
    [`GET /api/v1/privacy/patients/${CHILD_ID}/guardian-consent`]: NOT_YET,
    [`GET /api/v1/privacy/patients/${CHILD_ID}/consent-artefacts`]: [],
    [`POST /api/v1/privacy/patients/${CHILD_ID}/guardian-consent`]: {
      id: CONSENT_ID,
      guardianName: 'Lakshmi Parent',
      relationship: 'Parent',
      guardianPhone: '+91******3210',
      guardianIsAdult: true,
      method: 'Otp',
      documentType: null,
      documentLast4: null,
      status: 'Pending',
      createdAt: new Date().toISOString(),
      codeExpiresAt: new Date(Date.now() + 600_000).toISOString(),
      attemptsLeft: 5,
      verifiedAt: null,
      revokedAt: null,
      revokedReason: null,
    },
  });
});

afterEach(() => {
  expect(server.unanswered).toEqual([]);
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('the Significant Data Fiduciary register', SCREEN_TESTS, () => {
  it('lists the assessments with their review dates and sends a draft for someone else to approve', async () => {
    const user = userEvent.setup();
    openScreen('/privacy/sdf');
    expect(await screen.findByText('Lab reports by WhatsApp', {}, SLOW)).toBeInTheDocument();
    expect(screen.getByRole('table', { name: 'Data audits' })).toBeInTheDocument();
    await expectNoAxeViolations();

    await user.click(screen.getByRole('button', { name: 'Send DPIA26002 for approval' }));
    expect(await screen.findByText('Asked for approval', {}, SLOW)).toBeInTheDocument();
    expect(server.requests.find((r) => r.method === 'POST')?.body).toEqual({
      reason: 'Ready for approval.',
      version: 4,
    });
  });

  it('checks a new assessment has risks and measures before saving it as a draft', async () => {
    const user = userEvent.setup();
    openScreen('/privacy/sdf');
    await user.click(await screen.findByRole('button', { name: 'New assessment' }, SLOW));
    const form = screen.getByRole('form', { name: 'New impact assessment' });
    await user.click(within(form).getByRole('button', { name: 'Save as draft' }));
    expect(await within(form).findByText('Name the processing activity.')).toBeInTheDocument();
    expect(within(form).getByText('Describe the risk.')).toBeInTheDocument();
    await expectNoAxeViolations();
    expect(server.requests.some((r) => r.method === 'POST')).toBe(false);
  });
});

describe('consent managers', SCREEN_TESTS, () => {
  it('registers one and shows its signing secret once', async () => {
    const user = userEvent.setup();
    openScreen('/privacy/consent-managers');
    expect(await screen.findByText('Consent Manager One', {}, SLOW)).toBeInTheDocument();
    const form = screen.getByRole('form', { name: 'Register a consent manager' });
    await user.type(within(form).getByRole('textbox', { name: /^Name/ }), 'Consent Manager Two');
    await user.type(within(form).getByRole('textbox', { name: /Registration number/ }), 'DPB-CM-0011');
    await user.type(within(form).getByRole('textbox', { name: /^Code/ }), 'cm-two');
    await user.type(within(form).getByRole('textbox', { name: /Address for withdrawals/ }), 'https://cm2.example.in');
    await user.click(within(form).getByRole('button', { name: 'Register and make the secret' }));
    expect(await screen.findByText('shown-once-secret-value', {}, SLOW)).toBeInTheDocument();
    await expectNoAxeViolations();
    expect(server.requests.find((r) => r.method === 'POST')?.body).toEqual({
      code: 'cm-two',
      name: 'Consent Manager Two',
      registrationNumber: 'DPB-CM-0011',
      baseUrl: 'https://cm2.example.in',
      isSimulator: false,
    });
  });
});

describe("a child's record", SCREEN_TESTS, () => {
  function showCard(dateOfBirth: string | null) {
    useSession.setState({
      status: 'authenticated',
      accessToken: 't',
      expiresAt: Date.now() + 3600_000,
      user: STAFF_USER,
      locked: false,
    });
    render(
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <Hc360ThemeProvider>
          <GuardianConsentCard patientId={CHILD_ID} dateOfBirth={dateOfBirth} />
        </Hc360ThemeProvider>
      </QueryClientProvider>,
    );
  }

  it('records a guardian and sends the code to their number, once they are confirmed an adult', async () => {
    const user = userEvent.setup();
    showCard('2017-06-01');
    expect(await screen.findByText('No parent’s or guardian’s consent is verified yet.', {}, SLOW)).toBeInTheDocument();
    const form = screen.getByRole('form', { name: 'Record a guardian’s consent' });
    await user.type(within(form).getByRole('textbox', { name: /Parent’s or guardian’s name/ }), 'Lakshmi Parent');
    await user.type(within(form).getByRole('textbox', { name: /Guardian’s mobile number/ }), '9876543210');
    await user.click(within(form).getByRole('button', { name: 'Send the code' }));
    expect(await within(form).findByText('Confirm the parent or guardian is an adult.')).toBeInTheDocument();
    expect(server.requests.some((r) => r.method === 'POST')).toBe(false);

    await user.click(within(form).getByRole('checkbox', { name: 'I checked the parent or guardian is an adult' }));
    await user.click(within(form).getByRole('button', { name: 'Send the code' }));
    await vi.waitFor(() => expect(server.requests.some((r) => r.method === 'POST')).toBe(true));
    expect(server.requests.find((r) => r.method === 'POST')?.body).toMatchObject({
      guardianName: 'Lakshmi Parent',
      relationship: 'Parent',
      method: 'Otp',
      guardianIsAdult: true,
      documentLast4: null,
    });
    await expectNoAxeViolations();
  });

  it('asks nothing of the server for an adult', () => {
    showCard('1980-01-01');
    expect(server.requests).toEqual([]);
  });
});

describe('who is a child', () => {
  it('is under 18 on the day', () => {
    const today = new Date('2026-11-08T06:00:00Z');
    expect(isChild('2008-11-08', today)).toBe(false);
    expect(isChild('2008-11-09', today)).toBe(true);
    expect(isChild(null, today)).toBe(false);
  });
});
