import type { QueryClient } from '@tanstack/react-query';
import { createRouter, type RouterHistory } from '@tanstack/react-router';
import { PageSpinner } from '@/components/States';
import { NotFound, RouteError } from '@/shell/RouteError';
import { routeTree } from './routeTree.gen';

export interface RouterContext {
  queryClient: QueryClient;
}

/** `history` is for tests (memory history); the app uses the browser's. */
export function createPortalRouter(queryClient: QueryClient, history?: RouterHistory) {
  return createRouter({
    routeTree,
    ...(history ? { history } : {}),
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
