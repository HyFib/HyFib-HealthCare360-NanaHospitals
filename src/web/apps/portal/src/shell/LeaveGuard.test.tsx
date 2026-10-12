import { Field, FluentProvider, Input, webLightTheme } from '@fluentui/react-components';
import { useLeaveGuard, useValuesChanged } from '@hc360/ui';
import {
  createMemoryHistory,
  createRootRoute,
  createRoute,
  createRouter,
  Outlet,
  RouterProvider,
} from '@tanstack/react-router';
import { act, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { useState } from 'react';
import { describe, expect, it } from 'vitest';
import { LeaveGuard } from './LeaveGuard';

// The portal's question before leaving a page with unsaved changes (SRS UX-08), in the patient's words.

function RequestPage() {
  const [details, setDetails] = useState('');
  useLeaveGuard({ dirty: useValuesChanged(details) });
  return (
    <Field label="What do you need?">
      <Input value={details} onChange={(_, data) => setDetails(data.value)} />
    </Field>
  );
}

function renderApp() {
  const rootRoute = createRootRoute({
    component: () => (
      <>
        <Outlet />
        <LeaveGuard />
      </>
    ),
  });
  const routeTree = rootRoute.addChildren([
    createRoute({ getParentRoute: () => rootRoute, path: '/', component: RequestPage }),
    createRoute({ getParentRoute: () => rootRoute, path: '/home', component: () => <h1>Home</h1> }),
  ]);
  const router = createRouter({ routeTree, history: createMemoryHistory({ initialEntries: ['/'] }) });
  render(
    <FluentProvider theme={webLightTheme}>
      <RouterProvider router={router} />
    </FluentProvider>,
  );
  return router;
}

describe('LeaveGuard', () => {
  it('lets the patient leave a page they did not type on', async () => {
    const router = renderApp();
    await screen.findByRole('textbox', { name: 'What do you need?' });

    await act(() => router.navigate({ to: '/home' as never }));

    expect(await screen.findByRole('heading', { name: 'Home' })).toBeInTheDocument();
  });

  it('asks before leaving what was typed, stays on "Stay" and leaves on "Discard changes"', async () => {
    const router = renderApp();
    const field = await screen.findByRole('textbox', { name: 'What do you need?' });
    await userEvent.type(field, 'A copy of my discharge summary');

    void router.navigate({ to: '/home' as never });
    let question = await screen.findByRole('alertdialog', { name: 'Leave without saving?' });
    expect(question).toHaveTextContent('What you changed here has not been saved.');
    await userEvent.click(within(question).getByRole('button', { name: 'Stay' }));
    await waitFor(() => expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument());
    expect(router.state.location.pathname).toBe('/');
    expect(field).toHaveValue('A copy of my discharge summary');

    void router.navigate({ to: '/home' as never });
    question = await screen.findByRole('alertdialog', { name: 'Leave without saving?' });
    await userEvent.click(within(question).getByRole('button', { name: 'Discard changes' }));

    expect(await screen.findByRole('heading', { name: 'Home' })).toBeInTheDocument();
  });
});
