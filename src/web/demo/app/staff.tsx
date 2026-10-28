import { configureAuth, createHc360QueryClient, startConnectionMonitor, startSerialScans } from '@hc360/core';
import { Hc360ThemeProvider } from '@hc360/ui';
import { QueryClientProvider } from '@tanstack/react-query';
import { createMemoryHistory, RouterProvider } from '@tanstack/react-router';
import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { clientType } from '@/lib/api';
import { keepFocusInOpenDialogs } from '@/lib/dialogFocus';
import { createAppRouter } from '@/router';
import '@/styles.css';
import type { StartedApp } from './apps';

/**
 * Starts the staff app as apps/staff/src/main.tsx does, with one difference: it keeps which screen it is on to itself
 * (a memory history, starting at `path`) instead of in the address bar, since the page that holds the demo serves only
 * itself, not a path per screen, and its address belongs to the page around it.
 */
export function startApp(root: HTMLElement, path: string): StartedApp {
  configureAuth({ clientType });
  startConnectionMonitor();
  startSerialScans();
  keepFocusInOpenDialogs();

  const queryClient = createHc360QueryClient();
  const router = createAppRouter(queryClient, createMemoryHistory({ initialEntries: [path] }));

  createRoot(root).render(
    <StrictMode>
      <QueryClientProvider client={queryClient}>
        <Hc360ThemeProvider>
          <RouterProvider router={router} />
        </Hc360ThemeProvider>
      </QueryClientProvider>
    </StrictMode>,
  );
  return {
    open: (to) => router.navigate({ href: to }),
    here: () => router.state.location.publicHref,
  };
}
