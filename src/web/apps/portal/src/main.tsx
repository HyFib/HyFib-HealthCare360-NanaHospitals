import { FluentProvider } from '@fluentui/react-components';
import { configureAuth, createHc360QueryClient, startConnectionMonitor } from '@hc360/core';
import { Hc360ThemeProvider } from '@hc360/ui';
import { QueryClientProvider } from '@tanstack/react-query';
import { RouterProvider } from '@tanstack/react-router';
import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { applyDocumentLanguage, useLanguageStore } from '@/i18n';
import { clientType } from '@/lib/api';
import { portalTypeScale } from '@/lib/theme';
import { createPortalRouter } from './router';
import './styles.css';

configureAuth({ clientType });
startConnectionMonitor();
applyDocumentLanguage(useLanguageStore.getState().language);

const queryClient = createHc360QueryClient();
const router = createPortalRouter(queryClient);

const root = document.getElementById('root');
if (!root) {
  throw new Error('Root element missing');
}

createRoot(root).render(
  <StrictMode>
    <QueryClientProvider client={queryClient}>
      <Hc360ThemeProvider>
        {/* Patient-facing text is one step larger than the staff app: easier to read on phones, and 16 px inputs
            stop iOS from zooming into a field. The partial theme is merged with the HC360 theme above. */}
        <FluentProvider theme={portalTypeScale} style={{ backgroundColor: 'transparent' }}>
          <RouterProvider router={router} />
        </FluentProvider>
      </Hc360ThemeProvider>
    </QueryClientProvider>
  </StrictMode>,
);
