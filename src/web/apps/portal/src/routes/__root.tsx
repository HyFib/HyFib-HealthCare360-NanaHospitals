import { Toaster } from '@fluentui/react-components';
import { TOASTER_ID } from '@hc360/ui';
import { createRootRouteWithContext, Outlet } from '@tanstack/react-router';
import type { RouterContext } from '@/router';
import { PortalFrame } from '@/shell/PortalFrame';
import { NotFound, RouteError } from '@/shell/RouteError';

export const Route = createRootRouteWithContext<RouterContext>()({
  component: Root,
  notFoundComponent: RootNotFound,
  errorComponent: RouteError,
});

function Root() {
  return (
    <>
      <Outlet />
      <Toaster toasterId={TOASTER_ID} position="top" pauseOnHover pauseOnWindowBlur />
    </>
  );
}

/** Unknown address: shown in the plain frame (no signed-in navigation, nothing personal). */
function RootNotFound() {
  return (
    <PortalFrame navigation={false} brandTarget="/sign-in">
      <NotFound />
    </PortalFrame>
  );
}
