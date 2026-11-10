import { FluentProvider } from '@fluentui/react-components';
import { useSession } from '@hc360/core';
import { Hc360ThemeProvider } from '@hc360/ui';
import { expectNoAxeViolations } from '@hc360/ui/testing';
import { createFakeFetch } from '@hc360/ui/testing/fake-api';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { createMemoryHistory, RouterProvider } from '@tanstack/react-router';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { useProfileStore } from '@/lib/profileStore';
import { portalTypeScale } from '@/lib/theme';
import { createPortalRouter } from '@/router';
import { APPOINTMENTS, DOCTOR_ID, PORTAL_ANSWERS, SLOT_DAYS, USER } from '@/test/fixtures';

// Booking a video consultation on the portal: the patient agrees to be seen by video (Telemedicine Practice Guidelines
// 2020) before it is booked, and the agreement goes with the booking for the hospital to record. The server is a fake.

const SLOW = { timeout: 8000 };
const SLOT = SLOT_DAYS[0]!.slots[0]!.startsAt;

let server: ReturnType<typeof createFakeFetch>;

beforeEach(() => {
  server = createFakeFetch({
    ...PORTAL_ANSWERS,
    'POST /api/v1/portal/appointments': { appointment: APPOINTMENTS[0], payment: null, message: null },
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

function openBooking(video: boolean) {
  useSession.getState().setAuth({ accessToken: 'test-token', expiresAt: '2099-01-01T00:00:00Z', user: USER });
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const path = `/portal/doctors/${DOCTOR_ID}?${video ? 'mode=video&' : ''}slot=${encodeURIComponent(SLOT)}`;
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

const booked = () => server.requests.filter((r) => r.method === 'POST' && r.path === '/api/v1/portal/appointments');

describe('consent to a video consultation', () => {
  it('is asked before a video consultation is booked, and sent with the booking', async () => {
    const user = userEvent.setup();
    openBooking(true);
    await user.click(await screen.findByRole('button', { name: 'Continue' }, SLOW));
    const sheet = await screen.findByRole('dialog', {}, SLOW);
    const consent = within(sheet).getByRole('checkbox', { name: /I agree to be seen by video/ });
    await expectNoAxeViolations();

    await user.click(within(sheet).getByRole('button', { name: 'Book appointment' }));
    expect(await within(sheet).findByText('Please agree to a video consultation to book it.')).toBeInTheDocument();
    expect(booked()).toHaveLength(0);

    await user.click(consent);
    await user.click(within(sheet).getByRole('button', { name: 'Book appointment' }));
    await waitFor(() => expect(booked()).toHaveLength(1));
    expect(booked()[0]!.body).toMatchObject({ isTelehealth: true, telehealthConsent: true });
  });

  it('is not asked for a visit to the clinic', async () => {
    const user = userEvent.setup();
    openBooking(false);
    await user.click(await screen.findByRole('button', { name: 'Continue' }, SLOW));
    const sheet = await screen.findByRole('dialog', {}, SLOW);

    expect(within(sheet).queryByRole('checkbox', { name: /I agree to be seen by video/ })).not.toBeInTheDocument();
  });
});
