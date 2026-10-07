import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { useSession, type AuthUser } from '@hc360/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { createMemoryHistory, RouterProvider } from '@tanstack/react-router';
import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { useLanguageStore } from '@/i18n';
import { useProfileStore } from '@/lib/profileStore';
import type { PortalFamily, PortalProfile } from '@/lib/types';
import { createPortalRouter } from '@/router';

/**
 * Family on other numbers end to end in jsdom (router, signed-in layout, queries, i18n) against a fake server, with a
 * patient already signed in. Adding a relative is in the sheet's own tests.
 */

const PATIENT_ID = '5f0c6d3e-8c1a-4a53-9a3f-2b1d6f7e9a10';
const MOTHER_ID = '6a1d7e4f-9d2b-4b64-8b4f-3c2e7f8a0b21';
const LINK_ID = '0b9d2f4a-6c1e-4d8b-9f3a-7e5c1a2b3c4d';
const SHARING_ID = '2d3e4f5a-6b7c-4d8e-9f0a-1b2c3d4e5f6a';
const SLOW = { timeout: 8000 };

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

const MOTHER: PortalProfile = { ...PROFILE, id: MOTHER_ID, uhid: 'NH26000124', fullName: 'Kamala Devi', phone: null };

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

const FAMILY: PortalFamily = {
  linked: [
    {
      linkId: LINK_ID,
      patientId: MOTHER_ID,
      fullName: 'Kamala Devi',
      uhid: 'NH26000124',
      relation: 'Parent',
      source: 'PortalCode',
      linkedAt: '2026-10-01T06:00:00Z',
    },
  ],
  sharedWith: [
    {
      linkId: SHARING_ID,
      patientId: PATIENT_ID,
      patientName: 'Asha Verma',
      accountPhoneMasked: '+91******4455',
      relation: 'Child',
      linkedAt: '2026-09-15T06:00:00Z',
    },
  ],
};

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'content-type': 'application/json' } });
}

const calls: { method: string; path: string }[] = [];
let family: PortalFamily;

const fakeServer = vi.fn(async (input: RequestInfo | URL, init?: RequestInit): Promise<Response> => {
  const raw = typeof input === 'string' ? input : input instanceof URL ? input.href : input.url;
  const method = (init?.method ?? 'GET').toUpperCase();
  const { pathname } = new URL(raw, 'https://portal.test');
  calls.push({ method, path: pathname });
  switch (`${method} ${pathname}`) {
    case 'GET /api/v1/system/info':
      return json({ facilityName: 'Nana Hospitals' });
    case 'GET /api/v1/portal/profiles':
      return json(family.linked.length > 0 ? [PROFILE, MOTHER] : [PROFILE]);
    case 'GET /api/v1/portal/family/':
      return json(family);
    case `POST /api/v1/portal/family/links/${LINK_ID}/end`:
      family = { ...family, linked: [] };
      return new Response(null, { status: 204 });
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
  calls.length = 0;
  family = FAMILY;
  useProfileStore.getState().select(PATIENT_ID);
  vi.stubGlobal('fetch', fakeServer);
  useSession.getState().setAuth({ accessToken: 'test-token', expiresAt: '2099-01-01T00:00:00Z', user: USER });
});

afterEach(() => {
  useLanguageStore.getState().setLanguage('en');
  vi.unstubAllGlobals();
  sessionStorage.clear();
});

function openAt(path: string) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const history = createMemoryHistory({ initialEntries: [path] });
  render(
    <QueryClientProvider client={queryClient}>
      <FluentProvider theme={webLightTheme}>
        <RouterProvider router={createPortalRouter(queryClient, history)} />
      </FluentProvider>
    </QueryClientProvider>,
  );
}

/** A titled card of the page, by its heading. */
async function card(title: string): Promise<HTMLElement> {
  const heading = await screen.findByRole('heading', { name: title, level: 2 }, SLOW);
  const section = heading.closest('section');
  if (!section) {
    throw new Error(`No card titled ${title}`);
  }
  return section;
}

describe('Family on other numbers', () => {
  it('is linked from the profile', async () => {
    openAt('/portal/profile');
    const link = await screen.findByRole('link', { name: /Family on other numbers/ }, SLOW);
    expect(link).toHaveAttribute('href', '/portal/profile/family');
  });

  it('lists the relatives the account sees and who else sees its records', async () => {
    openAt('/portal/profile/family');
    expect(await screen.findByRole('heading', { name: 'Family on other numbers', level: 1 }, SLOW)).toBeInTheDocument();

    const seen = await card('Family members you see');
    expect(within(seen).getByText('Kamala Devi')).toBeInTheDocument();
    expect(within(seen).getByText('Your parent')).toBeInTheDocument();
    expect(within(seen).getByText(/UHID NH26000124/)).toBeInTheDocument();
    expect(within(seen).getByRole('button', { name: 'Remove Kamala Devi' })).toBeInTheDocument();
    expect(within(seen).getByRole('button', { name: 'Add a family member' })).toBeInTheDocument();

    const shared = await card('Who else sees your records');
    expect(within(shared).getByText('+91******4455')).toBeInTheDocument();
    expect(within(shared).getByText('Sees the records of Asha Verma (their son or daughter)')).toBeInTheDocument();
    expect(within(shared).getByRole('button', { name: 'Stop sharing with +91******4455' })).toBeInTheDocument();
  });

  it('removes a relative after asking', async () => {
    const user = userEvent.setup();
    openAt('/portal/profile/family');
    await user.click(await screen.findByRole('button', { name: 'Remove Kamala Devi' }, SLOW));
    const confirm = await screen.findByRole('alertdialog', { name: 'Remove Kamala Devi?' }, SLOW);
    await user.click(within(confirm).getByRole('button', { name: 'Remove' }));

    expect(
      await screen.findByText(
        'Everyone registered with your mobile number is already in your portal. To see the records of a family member who has their own number, add them.',
        {},
        SLOW,
      ),
    ).toBeInTheDocument();
    expect(calls).toContainEqual({ method: 'POST', path: `/api/v1/portal/family/links/${LINK_ID}/end` });
  });
});
