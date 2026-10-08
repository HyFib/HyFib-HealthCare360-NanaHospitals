import { configureAuth, createHc360QueryClient, startConnectionMonitor, startSerialScans } from '@hc360/core';
import { Hc360ThemeProvider } from '@hc360/ui';
import { QueryClientProvider } from '@tanstack/react-query';
import { RouterProvider } from '@tanstack/react-router';
import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { clientType } from './lib/api';
import { keepFocusInOpenDialogs } from './lib/dialogFocus';
import { createAppRouter } from './router';
import './styles.css';

configureAuth({ clientType });
startConnectionMonitor();
startSerialScans();
keepFocusInOpenDialogs();

const queryClient = createHc360QueryClient();
const router = createAppRouter(queryClient);

const root = document.getElementById('root');
if (!root) {
  throw new Error('Root element missing');
}

createRoot(root).render(
  <StrictMode>
    <QueryClientProvider client={queryClient}>
      <Hc360ThemeProvider>
        <RouterProvider router={router} />
      </Hc360ThemeProvider>
    </QueryClientProvider>
  </StrictMode>,
);
