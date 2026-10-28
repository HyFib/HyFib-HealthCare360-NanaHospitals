import { FluentProvider } from '@fluentui/react-components';
import { useSession } from '@hc360/core';
import { Hc360ThemeProvider } from '@hc360/ui';
import { createFakeFetch } from '@hc360/ui/testing/fake-api';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { createMemoryHistory, RouterProvider } from '@tanstack/react-router';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { WIDE_LAYOUT_QUERY } from '@/lib/hooks';
import { useProfileStore } from '@/lib/profileStore';
import { portalTypeScale } from '@/lib/theme';
import { createPortalRouter } from '@/router';
import { PORTAL_ANSWERS, USER } from '@/test/fixtures';

/** The signed-in frame when the session ends: the patient is taken to the sign-in page once, and told why. */

const SLOW = { timeout: 8000 };

beforeEach(() => {
  useProfileStore.getState().select(null);
  // A computer: the menu, with its sign-out button, is a column down the left.
  vi.stubGlobal(
    'matchMedia',
    (query: string): MediaQueryList =>
      ({
        matches: query === WIDE_LAYOUT_QUERY,
        media: query,
        onchange: null,
        addEventListener: () => undefined,
        removeEventListener: () => undefined,
        addListener: () => undefined,
        removeListener: () => undefined,
        dispatchEvent: () => false,
      }) as MediaQueryList,
  );
  vi.stubGlobal(
    'ResizeObserver',
    class {
      observe() {}
      unobserve() {}
      disconnect() {}
    },
  );
  vi.spyOn(window, 'scrollTo').mockImplementation(() => undefined);
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  sessionStorage.clear();
});

describe('signing out', () => {
  it('goes to the sign-in page once and says so, instead of sending the sign-in page to itself', async () => {
    const server = createFakeFetch({ ...PORTAL_ANSWERS, 'POST /api/v1/auth/logout': null });
    vi.stubGlobal('fetch', vi.fn(server.fetch));
    useSession.getState().setAuth({ accessToken: 'test-token', expiresAt: '2099-01-01T00:00:00Z', user: USER });
    const history = createMemoryHistory({ initialEntries: ['/portal/home'] });
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
      <QueryClientProvider client={queryClient}>
        <Hc360ThemeProvider>
          <FluentProvider theme={portalTypeScale}>
            <RouterProvider router={createPortalRouter(queryClient, history)} />
          </FluentProvider>
        </Hc360ThemeProvider>
      </QueryClientProvider>,
    );
    expect(await screen.findByRole('heading', { level: 1, name: /Asha/ }, SLOW)).toBeInTheDocument();
    const entries = history.length;

    await userEvent.click(screen.getByRole('button', { name: 'Sign out' }));

    expect(await screen.findByText('You have signed out.', {}, SLOW)).toBeInTheDocument();
    expect(screen.getByRole('textbox', { name: /Mobile number/ })).toBeInTheDocument();
    // Settled where it went: no second move from the sign-in page to itself ("?redirect=/sign-in?…").
    await new Promise((settle) => setTimeout(settle, 500));
    expect(`${history.location.pathname}${history.location.search}`).toBe('/portal/sign-in?ended=signed-out');
    expect(history.length).toBe(entries);
    expect(server.requests.filter((r) => r.path === '/api/v1/auth/logout')).toHaveLength(1);
  });
});
