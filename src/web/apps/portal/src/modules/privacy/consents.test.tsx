import { FluentProvider } from '@fluentui/react-components';
import { useSession } from '@hc360/core';
import { Hc360ThemeProvider } from '@hc360/ui';
import { expectNoAxeViolations } from '@hc360/ui/testing';
import { createFakeFetch } from '@hc360/ui/testing/fake-api';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { createMemoryHistory, RouterProvider } from '@tanstack/react-router';
import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { useProfileStore } from '@/lib/profileStore';
import { portalTypeScale } from '@/lib/theme';
import type { PortalConsentArtefact, PublicPrivacyInfo } from '@/lib/types';
import { createPortalRouter } from '@/router';
import { PORTAL_ANSWERS, PROFILE, USER } from '@/test/fixtures';

// DPDP Act s.6(7) and s.6(4): consents a patient gave through a consent manager, shown with what they cover and until
// when, and withdrawn here as easily as given. The privacy notice says how long erased data stays in backups, what
// applies to children, and names the Data Protection Officer once there is one. The server is a fake.

const SLOW = { timeout: 8000 };
const ARTEFACT_ID = '0192a000-0000-7000-8000-00000000ca01';

const ARTEFACT: PortalConsentArtefact = {
  id: ARTEFACT_ID,
  consentManagerName: 'Consent Manager One',
  artefactId: 'art-1',
  patientId: PROFILE.id,
  purpose: 'DataSharing',
  dataItems: ['lab-reports', 'discharge-summary'],
  validFrom: '2026-10-01T00:00:00Z',
  validUntil: '2027-04-01T00:00:00Z',
  status: 'Active',
  inForce: true,
  receivedAt: '2026-10-01T00:00:00Z',
  withdrawnAt: null,
};

const INFO: PublicPrivacyInfo = {
  hospital: 'Nana Hospitals',
  contact: {
    title: 'Grievance Officer',
    name: 'Anita Rao',
    email: 'privacy@nanahospitals.example',
    phone: null,
    address: null,
  },
  responseDays: 30,
  accessLogYears: null,
  dpo: {
    name: 'Neha Kulkarni',
    designation: 'Data Protection Officer',
    email: 'dpo@nanahospitals.example',
    phone: null,
    address: null,
    basedInIndia: true,
  },
  backupWindowDays: 31,
};

let server: ReturnType<typeof createFakeFetch>;

beforeEach(() => {
  server = createFakeFetch({
    ...PORTAL_ANSWERS,
    'GET /api/v1/public/privacy': INFO,
    'GET /api/v1/portal/privacy/consent-artefacts': [ARTEFACT],
    [`POST /api/v1/portal/privacy/consent-artefacts/${ARTEFACT_ID}/withdraw`]: {
      ...ARTEFACT,
      status: 'Withdrawn',
      inForce: false,
    },
  });
  useProfileStore.getState().select(null);
  vi.stubGlobal('fetch', vi.fn(server.fetch));
  vi.stubGlobal(
    'ResizeObserver',
    class {
      observe() {}
      unobserve() {}
      disconnect() {}
    },
  );
  vi.stubGlobal(
    'matchMedia',
    (query: string): MediaQueryList =>
      ({
        matches: false,
        media: query,
        onchange: null,
        addEventListener: () => undefined,
        removeEventListener: () => undefined,
        addListener: () => undefined,
        removeListener: () => undefined,
        dispatchEvent: () => false,
      }) as MediaQueryList,
  );
  vi.spyOn(window, 'scrollTo').mockImplementation(() => undefined);
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  sessionStorage.clear();
});

function open(path: string, signedIn = true) {
  if (signedIn) {
    useSession.getState().setAuth({ accessToken: 'test-token', expiresAt: '2099-01-01T00:00:00Z', user: USER });
  }
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={queryClient}>
      <Hc360ThemeProvider>
        <FluentProvider theme={portalTypeScale} style={{ backgroundColor: 'transparent' }}>
          <RouterProvider router={createPortalRouter(queryClient, createMemoryHistory({ initialEntries: [path] }))} />
        </FluentProvider>
      </Hc360ThemeProvider>
    </QueryClientProvider>,
  );
}

describe('consents through a consent manager', () => {
  it('shows what each consent is for, what it covers and until when, and withdraws it after asking', async () => {
    const user = userEvent.setup();
    open('/portal/my-privacy/consents');
    expect(await screen.findByRole('heading', { name: 'Sharing your records' }, SLOW)).toBeInTheDocument();
    expect(screen.getByText('lab-reports, discharge-summary')).toBeInTheDocument();
    expect(screen.getByText('Consent Manager One')).toBeInTheDocument();
    expect(screen.getByText('In force')).toBeInTheDocument();
    await expectNoAxeViolations();

    await user.click(screen.getByRole('button', { name: 'Withdraw this consent' }));
    const dialog = await screen.findByRole(
      'alertdialog',
      { name: 'Withdraw your consent for Sharing your records?' },
      SLOW,
    );
    await user.click(within(dialog).getByRole('button', { name: 'Withdraw this consent' }));
    expect(
      await screen.findByText('Consent withdrawn. We have told the consent manager.', {}, SLOW),
    ).toBeInTheDocument();
    expect(server.requests.filter((r) => r.method === 'POST').map((r) => r.path)).toEqual([
      `/api/v1/portal/privacy/consent-artefacts/${ARTEFACT_ID}/withdraw`,
    ]);
  });

  it('says so when no consent manager sent any', async () => {
    server = createFakeFetch({ ...PORTAL_ANSWERS, 'GET /api/v1/portal/privacy/consent-artefacts': [] });
    vi.stubGlobal('fetch', vi.fn(server.fetch));
    open('/portal/my-privacy/consents');
    expect(await screen.findByText('No consents from a consent manager', {}, SLOW)).toBeInTheDocument();
    await expectNoAxeViolations();
  });
});

describe('the privacy notice', () => {
  it('says how long erased data stays in backups, what applies to children, and names the DPO', async () => {
    open('/portal/privacy', false);
    expect(await screen.findByText(/within 31 days/, {}, SLOW)).toBeInTheDocument();
    expect(screen.getByText(/never send health offers or targeted messages to children/)).toBeInTheDocument();
    expect(await screen.findByText('Neha Kulkarni', {}, SLOW)).toBeInTheDocument();
    await expectNoAxeViolations();
  });
});
