import { FluentProvider } from '@fluentui/react-components';
import { useSession } from '@hc360/core';
import { Hc360ThemeProvider } from '@hc360/ui';
import { expectNoAxeViolations } from '@hc360/ui/testing';
import { createFakeFetch } from '@hc360/ui/testing/fake-api';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { createMemoryHistory, RouterProvider } from '@tanstack/react-router';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { useLanguageStore } from '@/i18n';
import { WIDE_LAYOUT_QUERY } from '@/lib/hooks';
import { useProfileStore } from '@/lib/profileStore';
import { portalTypeScale } from '@/lib/theme';
import { createPortalRouter } from '@/router';
import { PORTAL_ANSWERS, USER } from './test/fixtures';

/**
 * The portal's main pages end to end in jsdom (router, frame, queries, i18n) against a fake server, checked with
 * axe-core for WCAG 2.2 A and AA problems (SRS UX-10). Colour contrast and target sizes need a browser: a11y/*.spec.ts
 * checks the built portal in Chromium with the same canned data (docs/architecture/FRONTEND-GUIDE.md, "Accessibility").
 */

const SLOW = { timeout: 8000 };

let server: ReturnType<typeof createFakeFetch>;

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
  server = createFakeFetch(PORTAL_ANSWERS);
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
  vi.spyOn(window, 'scrollTo').mockImplementation(() => undefined);
  stubScreen(false);
});

afterEach(() => {
  // Every page asked for must have been answered: a page showing an error instead would hide its real content.
  expect(server.unanswered).toEqual([]);
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
