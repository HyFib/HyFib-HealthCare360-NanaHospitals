import { useSession, type AuthUser } from '@hc360/core';
import { Hc360ThemeProvider } from '@hc360/ui';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { createMemoryHistory, RouterProvider } from '@tanstack/react-router';
import { render } from '@testing-library/react';
import { vi } from 'vitest';
import { createAppRouter } from '@/router';
import { createFakeFetch, STAFF_USER, type FakeAnswers } from './fakeApi';

// Whole screens in jsdom: the real router, shell and screen, a signed-in user, and a fake server answering the API
// with canned JSON. A test file that uses this must also replace the realtime connection, which jsdom cannot open:
//
//   vi.mock('@hc360/core', async (importOriginal) => ({
//     ...(await importOriginal<typeof import('@hc360/core')>()),
//     RealtimeProvider: ({ children }: { children: ReactNode }) => children,
//   }));

export { FACILITY_ID, json, paged, STAFF_USER, type FakeAnswer, type FakeRequest } from './fakeApi';

/**
 * How long to wait for a screen's data. The first screen a test file opens also loads the route's code, split from the
 * rest, and that takes tens of seconds on a busy machine.
 */
export const SLOW = { timeout: 60_000 };

/** For a `describe` of whole screens: a test may wait for its screen (SLOW) more than once. */
export const SCREEN_TESTS = { timeout: 180_000 };

/**
 * Answers the API from `answers` for the rest of the test, keyed by method and path ("GET /api/v1/patients"); the
 * query string is ignored. A key may end in "/*" to answer every path below it. Anything else is a 404, and is listed
 * in `unanswered`.
 */
export function fakeServer(answers: FakeAnswers) {
  const server = createFakeFetch(answers);
  const fetch = vi.fn(server.fetch);
  vi.stubGlobal('fetch', fetch);
  return { fetch, requests: server.requests, unanswered: server.unanswered };
}

/** jsdom lacks what the shell and Fluent use for layout; harmless stand-ins. */
export function stubBrowserApis() {
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
  vi.stubGlobal(
    'ResizeObserver',
    class {
      observe() {}
      unobserve() {}
      disconnect() {}
    },
  );
  vi.stubGlobal(
    'IntersectionObserver',
    class {
      observe() {}
      unobserve() {}
      disconnect() {}
      takeRecords() {
        return [];
      }
    },
  );
  // The router scrolls after each navigation, which jsdom cannot do.
  vi.spyOn(window, 'scrollTo').mockImplementation(() => undefined);
}

/** Opens the staff app at `path`, signed in as `user` (signed out for null), in the HC360 light theme. */
export function openScreen(path: string, user: AuthUser | null = STAFF_USER) {
  useSession.setState(
    user
      ? {
          status: 'authenticated',
          accessToken: 'test-token',
          expiresAt: Date.now() + 8 * 3600_000,
          user,
          locked: false,
        }
      : { status: 'anonymous', accessToken: null, expiresAt: null, user: null, locked: false },
  );
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const router = createAppRouter(queryClient, createMemoryHistory({ initialEntries: [path] }));
  render(
    <QueryClientProvider client={queryClient}>
      <Hc360ThemeProvider>
        <RouterProvider router={router} />
      </Hc360ThemeProvider>
    </QueryClientProvider>,
  );
  return { router, queryClient };
}
