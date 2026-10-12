import {
  Button,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Field,
  FluentProvider,
  Input,
  useRestoreFocusTarget,
  webLightTheme,
} from '@fluentui/react-components';
import {
  CloseRequestScope,
  confirmLeaving,
  LEAVE_KEEPING_DRAFT,
  useCloseRequests,
  useLeaveGuard,
  useUnsavedChanges,
  useValuesChanged,
  wouldLoseChanges,
  type LeaveQuestion,
} from '@hc360/ui';
import {
  createBrowserHistory,
  createMemoryHistory,
  createRootRoute,
  createRoute,
  createRouter,
  Outlet,
  RouterProvider,
  type RouterHistory,
} from '@tanstack/react-router';
import { act, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { useState, type ReactNode } from 'react';
import { describe, expect, it, vi } from 'vitest';
import { EditorDrawer } from '@/modules/hr/shared/EditorDrawer';
import { LeaveGuard } from '@/shell/LeaveGuard';

// The unsaved-change guard (SRS UX-08) on a dialog form, as the screens use it: closing with changes asks first and
// returns focus to where the user was, leaving the screen asks, and closing the window has the browser ask.

function NoteDialog({ busy = false, onClose }: { busy?: boolean; onClose: () => void }) {
  const [note, setNote] = useState('');
  const guard = useUnsavedChanges({ dirty: useValuesChanged({ note }), busy, onClose });
  return (
    <>
      <Dialog open onOpenChange={guard.onOpenChange}>
        <DialogSurface>
          <DialogBody>
            <DialogTitle>Add a note</DialogTitle>
            <DialogContent>
              <Field label="Note">
                <Input value={note} onChange={(_, data) => setNote(data.value)} />
              </Field>
            </DialogContent>
            <DialogActions>
              <Button onClick={guard.requestClose}>Cancel</Button>
              <Button appearance="primary" onClick={guard.close}>
                Save
              </Button>
            </DialogActions>
          </DialogBody>
        </DialogSurface>
      </Dialog>
      {guard.dialogs}
    </>
  );
}

function NotesScreen({ busy, onClosed }: { busy?: boolean; onClosed?: () => void }) {
  const restoreFocusTarget = useRestoreFocusTarget();
  const [open, setOpen] = useState(false);
  return (
    <>
      <Button {...restoreFocusTarget} onClick={() => setOpen(true)}>
        Add a note
      </Button>
      {open && (
        <NoteDialog
          busy={busy}
          onClose={() => {
            setOpen(false);
            onClosed?.();
          }}
        />
      )}
    </>
  );
}

function renderScreen(props: { busy?: boolean; onClosed?: () => void } = {}) {
  render(
    <FluentProvider theme={webLightTheme}>
      <NotesScreen {...props} />
    </FluentProvider>,
  );
}

/** Opens the dialog from the keyboard, as Tabster returns focus for keyboard users in jsdom. */
async function openDialog() {
  const opener = screen.getByRole('button', { name: 'Add a note' });
  opener.focus();
  await userEvent.keyboard('{Enter}');
  const dialog = await screen.findByRole('dialog', { name: 'Add a note' });
  const field = within(dialog).getByRole('textbox', { name: 'Note' });
  await waitFor(() => expect(field).toHaveFocus());
  return { opener, dialog, field };
}

describe('a dialog form with unsaved changes', () => {
  it('closes without asking when nothing was typed', async () => {
    const onClosed = vi.fn();
    renderScreen({ onClosed });
    const { dialog } = await openDialog();

    await userEvent.click(within(dialog).getByRole('button', { name: 'Cancel' }));

    expect(onClosed).toHaveBeenCalledTimes(1);
    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument();
  });

  it('asks before Cancel throws the typing away, and returns focus to Cancel on keeping it', async () => {
    const onClosed = vi.fn();
    renderScreen({ onClosed });
    const { dialog, field } = await openDialog();
    await userEvent.type(field, 'Allergic to latex');

    const cancel = within(dialog).getByRole('button', { name: 'Cancel' });
    cancel.focus();
    await userEvent.keyboard('{Enter}');
    const question = await screen.findByRole('alertdialog', { name: 'Discard your changes?' });
    expect(question).toHaveTextContent('What you changed here has not been saved.');
    await waitFor(() => expect(within(question).getByRole('button', { name: 'Keep editing' })).toHaveFocus());

    await userEvent.keyboard('{Enter}');
    await waitFor(() => expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument());
    expect(onClosed).not.toHaveBeenCalled();
    expect(field).toHaveValue('Allergic to latex');
    await waitFor(() => expect(cancel).toHaveFocus());
  });

  it('asks before Esc closes it, and returns focus to the field on keeping the typing', async () => {
    renderScreen();
    const { field } = await openDialog();
    await userEvent.type(field, 'Allergic to latex');

    await userEvent.keyboard('{Escape}');
    const question = await screen.findByRole('alertdialog', { name: 'Discard your changes?' });
    expect(screen.getByRole('dialog', { name: 'Add a note' })).toBeInTheDocument();
    await waitFor(() => expect(within(question).getByRole('button', { name: 'Keep editing' })).toHaveFocus());

    await userEvent.keyboard('{Escape}');
    await waitFor(() => expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument());
    await waitFor(() => expect(field).toHaveFocus());
    // The field is marked as a place to return focus to only until focus is back.
    expect(field).not.toHaveAttribute('data-tabster');
  });

  it('closes on "Discard changes" and returns focus to the button that opened the form', async () => {
    const onClosed = vi.fn();
    renderScreen({ onClosed });
    const { opener, field } = await openDialog();
    await userEvent.type(field, 'Allergic to latex');

    await userEvent.keyboard('{Escape}');
    const question = await screen.findByRole('alertdialog', { name: 'Discard your changes?' });
    const discard = within(question).getByRole('button', { name: 'Discard changes' });
    discard.focus();
    await userEvent.keyboard('{Enter}');

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    expect(onClosed).toHaveBeenCalledTimes(1);
    await waitFor(() => expect(opener).toHaveFocus());
  });

  it('asks nothing once saved', async () => {
    const onClosed = vi.fn();
    renderScreen({ onClosed });
    const { dialog, field } = await openDialog();
    await userEvent.type(field, 'Allergic to latex');

    await userEvent.click(within(dialog).getByRole('button', { name: 'Save' }));

    expect(onClosed).toHaveBeenCalledTimes(1);
    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument();
    expect(wouldLoseChanges()).toBe(false);
  });

  it('does not close while the save runs', async () => {
    const onClosed = vi.fn();
    renderScreen({ busy: true, onClosed });
    const { dialog, field } = await openDialog();
    await userEvent.type(field, 'Allergic to latex');

    await userEvent.click(within(dialog).getByRole('button', { name: 'Cancel' }));
    await userEvent.keyboard('{Escape}');

    expect(onClosed).not.toHaveBeenCalled();
    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument();
    expect(screen.getByRole('dialog', { name: 'Add a note' })).toBeInTheDocument();
  });

  it('counts as unsaved for leaving the screen until it is saved', async () => {
    renderScreen();
    const { dialog, field } = await openDialog();
    expect(wouldLoseChanges()).toBe(false);

    await userEvent.type(field, 'x');
    expect(wouldLoseChanges()).toBe(true);
    expect(wouldLoseChanges({ current: { pathname: '/notes' }, next: { pathname: '/patients' } })).toBe(true);
    // A change of search parameters keeps the screen, and the dialog with it.
    expect(wouldLoseChanges({ current: { pathname: '/notes' }, next: { pathname: '/notes', search: {} } })).toBe(false);

    await userEvent.click(within(dialog).getByRole('button', { name: 'Save' }));
    expect(wouldLoseChanges()).toBe(false);
  });
});

// The screens of a small app, with the guard in the root route as in the staff app.
function renderApp(Screen: () => ReactNode, history: RouterHistory = createMemoryHistory({ initialEntries: ['/'] })) {
  const rootRoute = createRootRoute({
    component: () => (
      <>
        <Outlet />
        <LeaveGuard />
      </>
    ),
  });
  const routeTree = rootRoute.addChildren([
    createRoute({ getParentRoute: () => rootRoute, path: '/', component: Screen }),
    createRoute({ getParentRoute: () => rootRoute, path: '/other', component: () => <h1>Another screen</h1> }),
  ]);
  const router = createRouter({ routeTree, history });
  render(
    <FluentProvider theme={webLightTheme}>
      <RouterProvider router={router} />
    </FluentProvider>,
  );
  return router;
}

function RegistrationPage({ busy = false, leaveQuestion }: { busy?: boolean; leaveQuestion?: LeaveQuestion }) {
  const [name, setName] = useState('');
  useLeaveGuard({ dirty: useValuesChanged({ name }), busy, leaveQuestion });
  return (
    <Field label="Patient name">
      <Input value={name} onChange={(_, data) => setName(data.value)} />
    </Field>
  );
}

describe('leaving a screen with unsaved changes', () => {
  it('goes without asking when nothing was typed', async () => {
    const router = renderApp(() => <RegistrationPage />);
    await screen.findByRole('textbox', { name: 'Patient name' });

    await act(() => router.navigate({ to: '/other' as never }));

    expect(await screen.findByRole('heading', { name: 'Another screen' })).toBeInTheDocument();
  });

  it('asks first, stays on "Stay" and goes on "Discard changes"', async () => {
    const router = renderApp(() => <RegistrationPage />);
    const field = await screen.findByRole('textbox', { name: 'Patient name' });
    await userEvent.type(field, 'Ravi');

    void router.navigate({ to: '/other' as never });
    const question = await screen.findByRole('alertdialog', { name: 'Leave without saving?' });
    expect(question).toHaveTextContent('What you changed here has not been saved.');
    await waitFor(() => expect(within(question).getByRole('button', { name: 'Stay' })).toHaveFocus());
    await userEvent.keyboard('{Enter}');
    await waitFor(() => expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument());
    expect(field).toHaveValue('Ravi');
    await waitFor(() => expect(field).toHaveFocus());
    expect(router.state.location.pathname).toBe('/');

    void router.navigate({ to: '/other' as never });
    await userEvent.click(
      within(await screen.findByRole('alertdialog')).getByRole('button', { name: 'Discard changes' }),
    );
    expect(await screen.findByRole('heading', { name: 'Another screen' })).toBeInTheDocument();
  });

  it('lets a save go on to the next screen without asking', async () => {
    const router = renderApp(() => <RegistrationPage busy />);
    await userEvent.type(await screen.findByRole('textbox', { name: 'Patient name' }), 'Ravi');

    await act(() => router.navigate({ to: '/other' as never }));

    expect(await screen.findByRole('heading', { name: 'Another screen' })).toBeInTheDocument();
    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument();
  });

  it('has the browser ask before the window closes or reloads', async () => {
    const router = renderApp(() => <RegistrationPage />, createBrowserHistory());
    const closeWindow = () => {
      const event = new Event('beforeunload', { cancelable: true });
      window.dispatchEvent(event);
      return event;
    };
    try {
      const field = await screen.findByRole('textbox', { name: 'Patient name' });
      expect(closeWindow().defaultPrevented).toBe(false);

      await userEvent.type(field, 'Ravi');

      expect(closeWindow().defaultPrevented).toBe(true);
    } finally {
      router.history.destroy();
    }
  });
});

// A dialog whose form is a child mounted while the dialog is open (as most module dialogs are): the dialog's Esc and
// click outside go through the form's guard.
function ScopedDialog({ open, onClose }: { open: boolean; onClose: () => void }) {
  const closing = useCloseRequests({ onClose });
  return (
    <Dialog open={open} onOpenChange={closing.onOpenChange}>
      <DialogSurface>
        <CloseRequestScope value={closing}>{open && <ScopedBody onClose={onClose} />}</CloseRequestScope>
      </DialogSurface>
    </Dialog>
  );
}

function ScopedBody({ onClose }: { onClose: () => void }) {
  const [reason, setReason] = useState('');
  const guard = useUnsavedChanges({ dirty: reason !== '', onClose });
  return (
    <DialogBody>
      <DialogTitle>Cancel the appointment</DialogTitle>
      <DialogContent>
        <Field label="Reason">
          <Input value={reason} onChange={(_, data) => setReason(data.value)} />
        </Field>
      </DialogContent>
      <DialogActions>
        <Button onClick={guard.requestClose}>Keep appointment</Button>
      </DialogActions>
      {/* The question is nested in the dialog's surface, as the module dialogs render it. */}
      {guard.dialogs}
    </DialogBody>
  );
}

function ScopedScreen({ onClosed }: { onClosed: () => void }) {
  const restoreFocusTarget = useRestoreFocusTarget();
  const [open, setOpen] = useState(false);
  return (
    <>
      <Button {...restoreFocusTarget} onClick={() => setOpen(true)}>
        Cancel appointment
      </Button>
      <ScopedDialog
        open={open}
        onClose={() => {
          setOpen(false);
          onClosed();
        }}
      />
    </>
  );
}

describe('a dialog whose form mounts inside it', () => {
  async function open() {
    const onClosed = vi.fn();
    render(
      <FluentProvider theme={webLightTheme}>
        <ScopedScreen onClosed={onClosed} />
      </FluentProvider>,
    );
    const opener = screen.getByRole('button', { name: 'Cancel appointment' });
    opener.focus();
    await userEvent.keyboard('{Enter}');
    const dialog = await screen.findByRole('dialog', { name: 'Cancel the appointment' });
    const field = within(dialog).getByRole('textbox', { name: 'Reason' });
    await waitFor(() => expect(field).toHaveFocus());
    return { onClosed, opener, field };
  }

  it('closes on Esc when nothing was typed', async () => {
    const { onClosed } = await open();

    await userEvent.keyboard('{Escape}');

    await waitFor(() => expect(onClosed).toHaveBeenCalledTimes(1));
    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument();
  });

  it('asks on Esc once something was typed, keeps it on "Keep editing" and returns focus to the field', async () => {
    const { onClosed, field } = await open();
    await userEvent.type(field, 'Patient travelling');

    await userEvent.keyboard('{Escape}');
    const question = await screen.findByRole('alertdialog', { name: 'Discard your changes?' });
    await waitFor(() => expect(within(question).getByRole('button', { name: 'Keep editing' })).toHaveFocus());
    await userEvent.keyboard('{Enter}');

    await waitFor(() => expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument());
    expect(onClosed).not.toHaveBeenCalled();
    expect(field).toHaveValue('Patient travelling');
    await waitFor(() => expect(field).toHaveFocus());
  });

  it('closes on "Discard changes" and returns focus to the button that opened it', async () => {
    const { onClosed, opener, field } = await open();
    await userEvent.type(field, 'Patient travelling');

    await userEvent.click(screen.getByRole('button', { name: 'Keep appointment' }));
    const question = await screen.findByRole('alertdialog', { name: 'Discard your changes?' });
    within(question).getByRole('button', { name: 'Discard changes' }).focus();
    await userEvent.keyboard('{Enter}');

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    expect(onClosed).toHaveBeenCalledTimes(1);
    await waitFor(() => expect(opener).toHaveFocus());
  });
});

function ShiftDrawer({ busy = false, onClose }: { busy?: boolean; onClose: () => void }) {
  const [name, setName] = useState('Morning');
  return (
    <EditorDrawer
      open
      title="Edit the shift"
      dirty={name !== 'Morning'}
      busy={busy}
      onClose={onClose}
      onSubmit={(event) => event.preventDefault()}
    >
      <Field label="Name">
        <Input value={name} onChange={(_, data) => setName(data.value)} />
      </Field>
    </EditorDrawer>
  );
}

describe('the shared editor drawer', () => {
  function renderDrawer(busy = false) {
    const onClose = vi.fn();
    render(
      <FluentProvider theme={webLightTheme}>
        <ShiftDrawer busy={busy} onClose={onClose} />
      </FluentProvider>,
    );
    return onClose;
  }

  it('closes without asking while nothing was changed', async () => {
    const onClose = renderDrawer();
    await screen.findByRole('dialog', { name: 'Edit the shift' });

    await userEvent.click(screen.getByRole('button', { name: 'Close' }));

    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it('asks before the close button, Cancel or Esc throw a change away', async () => {
    const onClose = renderDrawer();
    const field = await screen.findByRole('textbox', { name: 'Name' });
    await userEvent.type(field, ' shift');

    const closes = [
      () => userEvent.click(screen.getByRole('button', { name: 'Close' })),
      () => userEvent.click(screen.getByRole('button', { name: 'Cancel' })),
      async () => {
        field.focus();
        await userEvent.keyboard('{Escape}');
      },
    ];
    for (const close of closes) {
      await close();
      const question = await screen.findByRole('alertdialog', { name: 'Discard your changes?' });
      // From the keyboard: in jsdom, Tabster returns focus on closing a dialog only for keyboard users.
      within(question).getByRole('button', { name: 'Keep editing' }).focus();
      await userEvent.keyboard('{Enter}');
      await waitFor(() => expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument());
      await waitFor(() =>
        expect(screen.getByRole('dialog', { name: 'Edit the shift' })).toContainElement(
          document.activeElement as HTMLElement,
        ),
      );
    }
    expect(onClose).not.toHaveBeenCalled();
    expect(field).toHaveValue('Morning shift');

    await userEvent.click(screen.getByRole('button', { name: 'Cancel' }));
    await userEvent.click(
      within(await screen.findByRole('alertdialog')).getByRole('button', { name: 'Discard changes' }),
    );
    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it('does not close while the save runs', async () => {
    const onClose = renderDrawer(true);
    const field = await screen.findByRole('textbox', { name: 'Name' });
    await userEvent.type(field, ' shift');

    await userEvent.keyboard('{Escape}');

    expect(onClose).not.toHaveBeenCalled();
    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument();
  });
});

describe('leaving in a way the router cannot stop first (signing out, switching facility)', () => {
  it('asks first, and lets the navigation that follows "Discard changes" go', async () => {
    const router = renderApp(() => <RegistrationPage />);
    await userEvent.type(await screen.findByRole('textbox', { name: 'Patient name' }), 'Ravi');

    let left = confirmLeaving();
    await userEvent.click(within(await screen.findByRole('alertdialog')).getByRole('button', { name: 'Stay' }));
    await expect(left).resolves.toBe(false);
    expect(wouldLoseChanges()).toBe(true);

    left = confirmLeaving();
    const question = await screen.findByRole('alertdialog', { name: 'Leave without saving?' });
    await userEvent.click(within(question).getByRole('button', { name: 'Discard changes' }));
    await expect(left).resolves.toBe(true);

    await act(() => router.navigate({ to: '/other' as never }));
    expect(await screen.findByRole('heading', { name: 'Another screen' })).toBeInTheDocument();
    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument();
  });
});

describe('a form that keeps a draft on this computer', () => {
  it('says the draft is kept when leaving', async () => {
    const router = renderApp(() => <RegistrationPage leaveQuestion={LEAVE_KEEPING_DRAFT} />);
    await userEvent.type(await screen.findByRole('textbox', { name: 'Patient name' }), 'Ravi');

    void router.navigate({ to: '/other' as never });
    const question = await screen.findByRole('alertdialog', { name: 'Leave without saving?' });
    expect(question).toHaveTextContent('A draft is kept on this computer');
    await userEvent.click(within(question).getByRole('button', { name: 'Leave' }));

    expect(await screen.findByRole('heading', { name: 'Another screen' })).toBeInTheDocument();
  });

  it('does not speak for another form that would lose its changes for good', async () => {
    const router = renderApp(() => (
      <>
        <RegistrationPage leaveQuestion={LEAVE_KEEPING_DRAFT} />
        <NotesScreen />
      </>
    ));
    await userEvent.type(await screen.findByRole('textbox', { name: 'Patient name' }), 'Ravi');
    const { field } = await openDialog();
    await userEvent.type(field, 'x');

    void router.navigate({ to: '/other' as never });
    const question = await screen.findByRole('alertdialog', { name: 'Leave without saving?' });
    expect(question).toHaveTextContent('What you changed here has not been saved.');
    expect(within(question).getByRole('button', { name: 'Discard changes' })).toBeInTheDocument();
  });
});
