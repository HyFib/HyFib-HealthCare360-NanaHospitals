import { FluentProvider } from '@fluentui/react-components';
import { configureAuth, createHc360QueryClient, startConnectionMonitor } from '@hc360/core';
import { Hc360ThemeProvider } from '@hc360/ui';
import { QueryClientProvider } from '@tanstack/react-query';
import { createMemoryHistory, RouterProvider } from '@tanstack/react-router';
import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { applyDocumentLanguage, useLanguageStore } from '@/i18n';
import { clientType } from '@/lib/api';
import { portalTypeScale } from '@/lib/theme';
import { createPortalRouter } from '@/router';
import '@/styles.css';
import type { StartedApp } from './apps';

// `@/` here is the patient portal's own source, not the staff app's (build.mjs, tsconfig.portal.json).

/**
 * Starts the patient portal as apps/portal/src/main.tsx does, with the staff app's one difference (staff.tsx): it
 * keeps which screen it is on to itself, in a memory history starting at `path` (under the portal's /portal base).
 */
export function startApp(root: HTMLElement, path: string): StartedApp {
  configureAuth({ clientType });
  startConnectionMonitor();
  applyDocumentLanguage(useLanguageStore.getState().language);

  const queryClient = createHc360QueryClient();
  const router = createPortalRouter(queryClient, createMemoryHistory({ initialEntries: [path] }));

  createRoot(root).render(
    <StrictMode>
      <QueryClientProvider client={queryClient}>
        <Hc360ThemeProvider>
          <FluentProvider theme={portalTypeScale} style={{ backgroundColor: 'transparent' }}>
            <RouterProvider router={router} />
          </FluentProvider>
        </Hc360ThemeProvider>
      </QueryClientProvider>
    </StrictMode>,
  );
  return {
    open: (to) => router.navigate({ href: to }),
    // The path with the portal's base (/portal/home), as the address bar would show it.
    here: () => router.state.location.publicHref,
  };
}
