import type { openScreen } from './appScreen';

type AppRouter = ReturnType<typeof openScreen>['router'];

/**
 * The screens a journey passes through (SRS UX-06): every route the router settles on, by route (its file under
 * src/routes), in order. A dialog or a drawer opens on the screen it belongs to and is not a screen; a change of the
 * search (a filter, the patient chosen, a bill opened on the same counter) keeps the route and is not a new screen
 * either.
 */
export function watchScreens(router: AppRouter) {
  const visited: string[] = [];
  const note = () => {
    const route = router.state.matches.at(-1)?.routeId;
    if (route && router.state.status === 'idle' && visited.at(-1) !== route) {
      visited.push(route);
    }
  };
  const stop = router.subscribe('onResolved', note);
  return {
    /** Each distinct screen once, in the order first reached. */
    screens(): string[] {
      note();
      return [...new Set(visited)];
    },
    /** Every move from one screen to another, including going back to an earlier one. */
    visits(): string[] {
      note();
      return [...visited];
    },
    stop,
  };
}
