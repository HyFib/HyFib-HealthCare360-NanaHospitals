import { Toaster } from '@fluentui/react-components';
import { TOASTER_ID } from '@hc360/ui';
import { createRootRouteWithContext, Outlet } from '@tanstack/react-router';
import type { RouterContext } from '@/router';
import { NotFound, RouteError } from '@/shell/RouteError';

export const Route = createRootRouteWithContext<RouterContext>()({
  component: Root,
  notFoundComponent: NotFound,
  errorComponent: RouteError,
});

function Root() {
  return (
    <>
      <Outlet />
      <Toaster toasterId={TOASTER_ID} position="bottom-end" pauseOnHover pauseOnWindowBlur />
    </>
  );
}
