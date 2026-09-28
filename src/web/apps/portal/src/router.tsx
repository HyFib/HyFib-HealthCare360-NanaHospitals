import type { QueryClient } from '@tanstack/react-query';
import { createRouter } from '@tanstack/react-router';
import { PageSpinner } from '@/components/States';
import { NotFound, RouteError } from '@/shell/RouteError';
import { routeTree } from './routeTree.gen';

export interface RouterContext {
  queryClient: QueryClient;
}

export function createPortalRouter(queryClient: QueryClient) {
  return createRouter({
    routeTree,
    // Served by the HC360 Server at /portal (the staff app owns "/").
    basepath: '/portal',
    context: { queryClient },
    defaultPreload: 'intent',
    // React Query owns caching; the router always asks it.
    defaultPreloadStaleTime: 0,
    scrollRestoration: true,
    defaultPendingMs: 400,
    defaultPendingComponent: () => <PageSpinner />,
    defaultErrorComponent: RouteError,
    defaultNotFoundComponent: NotFound,
  });
}

declare module '@tanstack/react-router' {
  interface Register {
    router: ReturnType<typeof createPortalRouter>;
  }
}
