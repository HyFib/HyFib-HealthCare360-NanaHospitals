import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { useSession, type AuthUser } from '@hc360/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { createMemoryHistory, RouterProvider } from '@tanstack/react-router';
import { render, screen } from '@testing-library/react';
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { useLanguageStore } from '@/i18n';
import { useProfileStore } from '@/lib/profileStore';
import type { PortalProfile, TelehealthJoinInfo, TelehealthMessageDto, TelehealthStatus } from '@/lib/types';
import { createPortalRouter } from '@/router';

/** A video consultation that has ended shows its chat to read again (the doctor's instructions, a dose). */

const PATIENT_ID = '5f0c6d3e-8c1a-4a53-9a3f-2b1d6f7e9a10';
const SESSION_ID = '0192a000-0000-7000-8000-0000000000aa';
const SLOW = { timeout: 8000 };

const PROFILE: PortalProfile = {
  id: PATIENT_ID,
  uhid: 'NH26000123',
  fullName: 'Asha Verma',
  gender: 'Female',
  dateOfBirth: '1987-03-14',
  isDateOfBirthEstimated: false,
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

const MESSAGES: TelehealthMessageDto[] = [
  {
    id: 'm1',
    senderRole: 'Patient',
    senderName: 'Asha Verma',
    text: 'Should I take it after food?',
    sentAt: '2026-10-23T05:02:00Z',
  },
  {
    id: 'm2',
    senderRole: 'Clinician',
    senderName: 'Dr. Meera Iyer',
    text: 'Yes, after breakfast.',
    sentAt: '2026-10-23T05:03:00Z',
  },
];

let status: TelehealthStatus;
let messages: TelehealthMessageDto[];
const paths: string[] = [];

const json = (body: unknown, code = 200) =>
  new Response(JSON.stringify(body), { status: code, headers: { 'content-type': 'application/json' } });

const fakeServer = vi.fn(async (input: RequestInfo | URL): Promise<Response> => {
  const raw = typeof input === 'string' ? input : input instanceof URL ? input.href : input.url;
  const { pathname } = new URL(raw, 'https://portal.test');
  paths.push(pathname);
  switch (pathname) {
    case '/api/v1/system/info':
      return json({ facilityName: 'Nana Hospitals', features: { patientPortal: true, telehealth: true } });
    case '/api/v1/portal/profiles':
      return json([PROFILE]);
    case `/api/v1/portal/telehealth/sessions/${SESSION_ID}/join`:
      return json({
        sessionId: SESSION_ID,
        status,
        scheduledStartAt: '2026-10-23T05:00:00Z',
        scheduledEndAt: '2026-10-23T05:15:00Z',
        practitionerName: 'Dr. Meera Iyer',
        departmentName: 'General Medicine',
        patientName: 'Asha Verma',
        canJoinNow: false,
        joinOpensAt: '2026-10-23T04:50:00Z',
        hubPath: '/hubs/telehealth',
        iceServers: [],
      } satisfies TelehealthJoinInfo);
    case `/api/v1/portal/telehealth/sessions/${SESSION_ID}/messages`:
      return json(messages);
    default:
      return json({ title: 'Not found', status: 404 }, 404);
  }
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
  paths.length = 0;
  status = 'Completed';
  messages = MESSAGES;
  useProfileStore.getState().select(null);
  vi.stubGlobal('fetch', fakeServer);
  useSession.getState().setAuth({ accessToken: 'test-token', expiresAt: '2099-01-01T00:00:00Z', user: USER });
});

afterEach(() => {
  useLanguageStore.getState().setLanguage('en');
  vi.unstubAllGlobals();
  sessionStorage.clear();
});

function openSession() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const history = createMemoryHistory({ initialEntries: [`/portal/video/${SESSION_ID}`] });
  render(
    <QueryClientProvider client={queryClient}>
      <FluentProvider theme={webLightTheme}>
        <RouterProvider router={createPortalRouter(queryClient, history)} />
      </FluentProvider>
    </QueryClientProvider>,
  );
}

describe('the chat of an ended video consultation', () => {
  it('shows what the patient and the doctor wrote', async () => {
    openSession();

    expect(await screen.findByRole('heading', { name: 'Chat from this consultation' }, SLOW)).toBeInTheDocument();
    expect(await screen.findByText('Yes, after breakfast.')).toBeInTheDocument();
    expect(screen.getByText('Should I take it after food?')).toBeInTheDocument();
    expect(screen.getByText(/^Dr\. Meera Iyer ·/)).toBeInTheDocument();
    expect(screen.getByText(/^You ·/)).toBeInTheDocument();
  });

  it('says so when nobody wrote anything', async () => {
    messages = [];
    openSession();

    expect(await screen.findByText('There were no chat messages in this consultation.', {}, SLOW)).toBeInTheDocument();
  });

  it('is in the language the patient chose', async () => {
    useLanguageStore.getState().setLanguage('ta');
    openSession();

    expect(await screen.findByRole('heading', { name: 'இந்த ஆலோசனையின் சாட்' }, SLOW)).toBeInTheDocument();
  });

  it('is not asked for when the consultation did not take place', async () => {
    status = 'Cancelled';
    openSession();

    await screen.findByText(/cancelled/i, {}, SLOW);
    expect(screen.queryByRole('heading', { name: 'Chat from this consultation' })).not.toBeInTheDocument();
    expect(paths).not.toContain(`/api/v1/portal/telehealth/sessions/${SESSION_ID}/messages`);
  });
});
