import { FluentProvider } from '@fluentui/react-components';
import { useSession, type AuthUser } from '@hc360/core';
import { Hc360ThemeProvider } from '@hc360/ui';
import { expectNoAxeViolations } from '@hc360/ui/testing';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { createMemoryHistory, RouterProvider } from '@tanstack/react-router';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { useLanguageStore } from '@/i18n';
import { WIDE_LAYOUT_QUERY } from '@/lib/hooks';
import { useProfileStore } from '@/lib/profileStore';
import { portalTypeScale } from '@/lib/theme';
import type {
  OtpChallenge,
  PortalAppointment,
  PortalBillListItem,
  PortalPrescriptionListItem,
  PortalProfile,
  PortalReportItem,
  PortalTelehealthSession,
  PortalWaitlistEntry,
} from '@/lib/types';
import { createPortalRouter } from '@/router';

/**
 * The portal's main pages end to end in jsdom (router, frame, queries, i18n) against a fake server, checked with
 * axe-core for WCAG 2.2 A and AA problems (SRS UX-10). Colour contrast and target sizes need a browser
 * (docs/architecture/FRONTEND-GUIDE.md, "Accessibility").
 */

const PATIENT_ID = '5f0c6d3e-8c1a-4a53-9a3f-2b1d6f7e9a10';
const DOCTOR_ID = '7b8c9d0e-1f2a-4b3c-8d4e-5f6a7b8c9d0e';
const SLOW = { timeout: 8000 };
const hours = (count: number) => new Date(Date.now() + count * 3600_000).toISOString();

const PROFILE: PortalProfile = {
  id: PATIENT_ID,
  uhid: 'NH26000123',
  fullName: 'Asha Verma',
  gender: 'Female',
  dateOfBirth: '1987-03-14',
  age: '39Y',
  phone: '+91******3210',
  bloodGroup: 'BPositive',
  category: 'General',
  isVip: false,
  isDeceased: false,
  photoFileId: null,
};

const USER: AuthUser = {
  id: 'user-1',
  userName: '+919876543210',
  displayName: 'Asha Verma',
  type: 'patient',
  facilityId: null,
  facilityName: null,
  practitionerId: null,
  patientId: PATIENT_ID,
  roles: [],
  permissions: ['portal.access'],
  facilities: [],
  mfaEnabled: false,
  hasPin: false,
  mustChangePassword: false,
  restriction: null,
};

const appointment = (overrides: Partial<PortalAppointment> & Pick<PortalAppointment, 'id' | 'number'>) =>
  ({
    patientId: PATIENT_ID,
    patientName: 'Asha Verma',
    practitionerId: DOCTOR_ID,
    practitionerName: 'Dr. Meera Iyer',
    specialtyName: 'General medicine',
    departmentName: 'General medicine',
    facilityId: 'f1',
    facilityName: 'Nana Hospitals, Pune',
    startsAt: hours(26),
    endsAt: hours(26.25),
    status: 'Booked',
    visitType: 'New',
    isTelehealth: false,
    paymentStatus: 'PayAtHospital',
    feeAmount: 500,
    currency: 'INR',
    reason: 'Knee pain',
    canCancel: true,
    canReschedule: true,
    canPay: false,
    ...overrides,
  }) satisfies PortalAppointment;

const APPOINTMENTS: PortalAppointment[] = [
  appointment({ id: 'a1', number: 'AP2600101' }),
  appointment({
    id: 'a2',
    number: 'AP2600102',
    startsAt: hours(50),
    endsAt: hours(50.25),
    isTelehealth: true,
    paymentStatus: 'Unpaid',
    canPay: true,
    status: 'Confirmed',
  }),
];

const WAITLIST: PortalWaitlistEntry[] = [
  {
    id: 'w1',
    patientId: PATIENT_ID,
    patientName: 'Asha Verma',
    practitionerId: DOCTOR_ID,
    practitionerName: 'Dr. Anil Rao',
    preferredFrom: '2026-10-12',
    preferredTo: '2026-10-19',
    preferredSession: 'Morning',
    isTelehealth: false,
    status: 'Waiting',
    offeredSlotStartsAt: null,
    createdAt: hours(-30),
  },
];

const REPORTS: PortalReportItem[] = [
  {
    kind: 'Laboratory',
    id: 'r1',
    patientId: PATIENT_ID,
    patientName: 'Asha Verma',
    title: 'Complete blood count',
    reference: 'LAB2600123',
    releasedAt: hours(-20),
    isAmended: false,
    isPartial: true,
  },
  {
    kind: 'Radiology',
    id: 'r2',
    patientId: PATIENT_ID,
    patientName: 'Asha Verma',
    title: 'Chest X-ray',
    reference: 'RAD2600045',
    releasedAt: hours(-200),
    isAmended: true,
    isPartial: false,
  },
  {
    kind: 'Cardiology',
    id: 'r3',
    patientId: PATIENT_ID,
    patientName: 'Asha Verma',
    title: 'Resting ECG',
    reference: 'CAR2600007',
    releasedAt: hours(-400),
    isAmended: false,
    isPartial: false,
  },
];

const BILLS: PortalBillListItem[] = [
  {
    id: 'b1',
    billNumber: 'OP2600001',
    type: 'OutPatient',
    status: 'PartiallyPaid',
    patientId: PATIENT_ID,
    patientName: 'Asha Verma',
    billedAt: hours(-48),
    netAmount: 1250,
    patientAmount: 1250,
    paidAmount: 500,
    dueAmount: 750,
  },
];

const PRESCRIPTIONS: PortalPrescriptionListItem[] = [
  {
    id: 'p1',
    prescriptionNumber: 'RX2600123',
    patientId: PATIENT_ID,
    status: 'Signed',
    signedAt: hours(-24),
    doctor: 'Dr. Meera Iyer',
    medicines: ['Paracetamol 500 mg', 'Pantoprazole 40 mg'],
    refillsRemaining: 1,
  },
];

const VIDEO: PortalTelehealthSession[] = [
  {
    id: 'v1',
    patientId: PATIENT_ID,
    patientName: 'Asha Verma',
    practitionerId: DOCTOR_ID,
    practitionerName: 'Dr. Meera Iyer',
    appointmentNumber: 'AP2600102',
    status: 'Scheduled',
    scheduledStartAt: hours(50),
    scheduledEndAt: hours(50.25),
    joinOpensAt: hours(49.75),
    canJoinNow: false,
  },
];

const CHALLENGE: OtpChallenge = { challengeId: 'c1', expiresAt: hours(0.1), maskedPhone: '+91******3210' };

const unanswered: string[] = [];

const fakeServer = vi.fn(async (input: RequestInfo | URL, init?: RequestInit): Promise<Response> => {
  const raw = typeof input === 'string' ? input : input instanceof URL ? input.href : input.url;
  const method = (init?.method ?? 'GET').toUpperCase();
  const { pathname, search } = new URL(raw, 'https://portal.test');
  const answer = (body: unknown) =>
    new Response(JSON.stringify(body), { status: 200, headers: { 'content-type': 'application/json' } });
  switch (`${method} ${pathname}`) {
    case 'GET /api/v1/system/info':
      return answer({ facilityName: 'Nana Hospitals', features: { patientPortal: true, telehealth: true } });
    case 'POST /api/v1/public/challenge':
      return answer({ required: true, challenge: '1.otp.1.1.00.sig', bits: 1, expiresAt: null });
    case 'POST /api/v1/auth/otp/request':
      return answer(CHALLENGE);
    case 'GET /api/v1/portal/profiles':
      return answer([PROFILE]);
    case 'GET /api/v1/portal/appointments':
      return answer(APPOINTMENTS);
    case 'GET /api/v1/portal/waitlist':
      return answer(WAITLIST);
    case 'GET /api/v1/portal/reports':
      return answer(REPORTS);
    case 'GET /api/v1/portal/bills':
      return answer({ items: BILLS, page: 1, pageSize: 50, totalCount: BILLS.length });
    case 'GET /api/v1/portal/prescriptions':
      return answer({ items: PRESCRIPTIONS, page: 1, pageSize: 20, totalCount: PRESCRIPTIONS.length });
    case 'GET /api/v1/portal/telehealth/sessions':
      return answer(VIDEO);
    case 'GET /api/v1/portal/privacy/notices':
      return answer([]);
  }
  unanswered.push(`${method} ${pathname}${search}`);
  return new Response(JSON.stringify({ title: 'Not found', status: 404 }), {
    status: 404,
    headers: { 'content-type': 'application/problem+json' },
  });
});

/** Phones by default (the portal is mobile-first); `wide` for a computer. */
function stubScreen(wide: boolean) {
  vi.stubGlobal(
    'matchMedia',
    (query: string): MediaQueryList =>
      ({
        matches: wide && query === WIDE_LAYOUT_QUERY,
        media: query,
        onchange: null,
        addEventListener: () => undefined,
        removeEventListener: () => undefined,
        addListener: () => undefined,
        removeListener: () => undefined,
        dispatchEvent: () => false,
      }) as MediaQueryList,
  );
}

beforeEach(() => {
  unanswered.length = 0;
  useProfileStore.getState().select(null);
  vi.stubGlobal('fetch', fakeServer);
  vi.stubGlobal(
    'ResizeObserver',
    class {
      observe() {}
      unobserve() {}
      disconnect() {}
    },
  );
  vi.spyOn(window, 'scrollTo').mockImplementation(() => undefined);
  stubScreen(false);
});

afterEach(() => {
  // Every page asked for must have been answered: a page showing an error instead would hide its real content.
  expect(unanswered).toEqual([]);
  useLanguageStore.getState().setLanguage('en');
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  sessionStorage.clear();
});

/** Opens the portal at `path`, as the app does (HC360 theme, the portal's type scale), signed in or not. */
function openAt(path: string, signedIn = true) {
  if (signedIn) {
    // As after a successful sign-in with a code: the access token lives in memory only.
    useSession.getState().setAuth({ accessToken: 'test-token', expiresAt: '2099-01-01T00:00:00Z', user: USER });
  } else {
    useSession.setState({ status: 'anonymous', accessToken: null, expiresAt: null, user: null, locked: false });
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

describe('portal pages', () => {
  it('sign-in, then the code sent to the phone', async () => {
    openAt('/portal/sign-in', false);
    const mobile = await screen.findByRole('textbox', { name: /Mobile number/ }, SLOW);
    await expectNoAxeViolations();

    await userEvent.type(mobile, '9876543210');
    await userEvent.click(screen.getByRole('button', { name: 'Send code' }));
    expect(await screen.findByText(/\+91\*{6}3210/, {}, SLOW)).toBeInTheDocument();
    await expectNoAxeViolations();
  });

  it('home on a phone, and its More menu', async () => {
    openAt('/portal/home');
    expect(await screen.findByRole('heading', { level: 1, name: /Asha/ }, SLOW)).toBeInTheDocument();
    expect((await screen.findAllByText(/Dr\. Meera Iyer/, {}, SLOW)).length).toBeGreaterThan(0);
    await expectNoAxeViolations();

    await userEvent.click(screen.getByRole('button', { name: 'More' }));
    expect(await screen.findByRole('dialog', {}, SLOW)).toBeInTheDocument();
    await expectNoAxeViolations();
  });

  it('home on a computer, in Hindi', async () => {
    stubScreen(true);
    useLanguageStore.getState().setLanguage('hi');
    openAt('/portal/home');
    expect(await screen.findByRole('heading', { level: 1, name: /Asha/ }, SLOW)).toBeInTheDocument();
    expect((await screen.findAllByText(/Dr\. Meera Iyer/, {}, SLOW)).length).toBeGreaterThan(0);
    await expectNoAxeViolations();
  });

  it('appointments', async () => {
    openAt('/portal/appointments');
    expect(await screen.findByRole('heading', { level: 1, name: 'Appointments' }, SLOW)).toBeInTheDocument();
    expect((await screen.findAllByText(/Dr\. Anil Rao/, {}, SLOW)).length).toBeGreaterThan(0);
    await expectNoAxeViolations();
  });

  it('reports', async () => {
    openAt('/portal/reports');
    expect(await screen.findByText('Complete blood count', {}, SLOW)).toBeInTheDocument();
    await expectNoAxeViolations();
  });

  it('profile', async () => {
    openAt('/portal/profile');
    expect(await screen.findByRole('heading', { level: 1, name: 'Profile' }, SLOW)).toBeInTheDocument();
    expect((await screen.findAllByText(/NH26000123/, {}, SLOW)).length).toBeGreaterThan(0);
    await expectNoAxeViolations();
  });
});
