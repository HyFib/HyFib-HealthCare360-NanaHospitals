import { LoadingState } from '@hc360/ui';
import type { QueryClient } from '@tanstack/react-query';
import { createRouter } from '@tanstack/react-router';
import { RouteError } from './shell/RouteError';
import { routeTree } from './routeTree.gen';

export interface RouterContext {
  queryClient: QueryClient;
}

export function createAppRouter(queryClient: QueryClient) {
  return createRouter({
    routeTree,
    context: { queryClient },
    defaultPreload: 'intent',
    // React Query owns caching; the router always asks it.
    defaultPreloadStaleTime: 0,
    scrollRestoration: true,
    defaultPendingMs: 300,
    defaultPendingComponent: () => <LoadingState />,
    defaultErrorComponent: RouteError,
  });
}

declare module '@tanstack/react-router' {
  interface Register {
    router: ReturnType<typeof createAppRouter>;
  }
}
