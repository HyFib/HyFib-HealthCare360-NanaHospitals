import {
  Button,
  Dialog,
  DialogActions,
  DialogBody,
  DialogSurface,
  DialogTitle,
  DrawerBody,
  DrawerHeader,
  DrawerHeaderTitle,
  FluentProvider,
  Input,
  Menu,
  MenuItem,
  MenuList,
  MenuPopover,
  MenuTrigger,
  OverlayDrawer,
  webLightTheme,
} from '@fluentui/react-components';
import { useHotkey } from '@hc360/core';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { useState, type ReactNode } from 'react';
import { describe, expect, it, vi } from 'vitest';
import { ReasonDialog } from '@/modules/inventory/ReasonDialog';
import { MasterDrawer } from '@/modules/masters/MasterDrawer';
import { useEditorGuard } from '@/modules/masters/useEditorGuard';

// Shortcuts listen on the window, so keys pressed in a dialog reach the page's shortcuts too. These use Fluent's real
// dialogs and drawers, which focus as in a browser thanks to src/test/setup.ts.

/** The pharmacy's prescription screen in miniature: F9 dispenses; More actions › Close prescription asks why. */
function PrescriptionScreen({ dispense }: { dispense: () => void }) {
  const [closing, setClosing] = useState(false);
  useHotkey('f9', dispense, { allowInInputs: true });
  return (
    <>
      <Input aria-label="Counselling notes" />
      <Menu>
        <MenuTrigger disableButtonEnhancement>
          <Button>More actions</Button>
        </MenuTrigger>
        <MenuPopover>
          <MenuList>
            <MenuItem onClick={() => setClosing(true)}>Close prescription</MenuItem>
          </MenuList>
        </MenuPopover>
      </Menu>
      {closing && (
        <ReasonDialog
          title="Close this prescription?"
          confirmLabel="Close prescription"
          onSubmit={() => setClosing(false)}
          onClose={() => setClosing(false)}
        />
      )}
    </>
  );
}

/** A masters editor: Ctrl+S saves the drawer's form, and closing it with unsaved changes asks first. */
function SpecialtyEditor({ save }: { save: () => void }) {
  const [name, setName] = useState('');
  const guard = useEditorGuard({ dirty: name !== '', busy: false, onClose: () => undefined });
  return (
    <MasterDrawer
      title="Edit specialty"
      guard={guard}
      busy={false}
      submitLabel="Save"
      onSubmit={(event) => {
        event.preventDefault();
        save();
      }}
    >
      <Input aria-label="Name" value={name} onChange={(_, data) => setName(data.value)} />
    </MasterDrawer>
  );
}

function Shortcut({ combo, run, overDialogs }: { combo: string; run: () => void; overDialogs?: boolean }) {
  useHotkey(combo, run, { allowInInputs: true, overDialogs });
  return null;
}

function OpenDialog() {
  return (
    <Dialog open>
      <DialogSurface>
        <DialogBody>
          <DialogTitle>Book an appointment</DialogTitle>
          <DialogActions>
            <Button>Book</Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}

function renderInFluent(ui: ReactNode) {
  return render(<FluentProvider theme={webLightTheme}>{ui}</FluentProvider>);
}

describe('useHotkey', () => {
  it('runs a page shortcut when no dialog is open', async () => {
    const dispense = vi.fn();
    renderInFluent(<PrescriptionScreen dispense={dispense} />);

    await userEvent.type(screen.getByRole('textbox', { name: 'Counselling notes' }), 'Take after food');
    await userEvent.keyboard('{F9}');

    expect(dispense).toHaveBeenCalledTimes(1);
  });

  it('leaves a key pressed in a dialog over the page alone, and works again once the dialog closes', async () => {
    const dispense = vi.fn();
    renderInFluent(<PrescriptionScreen dispense={dispense} />);

    await userEvent.click(screen.getByRole('button', { name: 'More actions' }));
    await userEvent.click(await screen.findByRole('menuitem', { name: 'Close prescription' }));
    const dialog = await screen.findByRole('alertdialog', { name: 'Close this prescription?' });
    await userEvent.type(within(dialog).getByRole('textbox', { name: /Reason/ }), 'Patient left');
    await userEvent.keyboard('{F9}');
    expect(dispense).not.toHaveBeenCalled();

    await userEvent.click(within(dialog).getByRole('button', { name: 'Go back' }));
    // The typed reason is not thrown away without asking.
    const question = await screen.findByRole('alertdialog', { name: 'Discard your changes?' });
    await userEvent.click(within(question).getByRole('button', { name: 'Discard changes' }));
    await waitFor(() => expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument());
    await userEvent.keyboard('{F9}');
    expect(dispense).toHaveBeenCalledTimes(1);
  });

  it('leaves a key pressed in a drawer over the page alone', async () => {
    const dispense = vi.fn();
    renderInFluent(
      <>
        <PrescriptionScreen dispense={dispense} />
        <OverlayDrawer open position="end">
          <DrawerHeader>
            <DrawerHeaderTitle>Notifications</DrawerHeaderTitle>
          </DrawerHeader>
          <DrawerBody>
            <Button>Mark all read</Button>
          </DrawerBody>
        </OverlayDrawer>
      </>,
    );

    (await screen.findByRole('button', { name: 'Mark all read' })).focus();
    await userEvent.keyboard('{F9}');

    expect(dispense).not.toHaveBeenCalled();
  });

  it('still keeps the key from the browser while a dialog is open', async () => {
    const previousDay = vi.fn();
    renderInFluent(
      <>
        <Shortcut combo="alt+arrowleft" run={previousDay} />
        <OpenDialog />
      </>,
    );

    // fireEvent returns false when the default action was prevented: Alt+Left must not take the browser back a page.
    expect(
      fireEvent.keyDown(await screen.findByRole('button', { name: 'Book' }), { key: 'ArrowLeft', altKey: true }),
    ).toBe(false);
    expect(previousDay).not.toHaveBeenCalled();
  });

  it("runs a dialog's own shortcut inside it, but not in a dialog opened over it", async () => {
    const save = vi.fn();
    renderInFluent(<SpecialtyEditor save={save} />);

    await userEvent.type(await screen.findByRole('textbox', { name: 'Name' }), 'Cardiology');
    await userEvent.keyboard('{Control>}s{/Control}');
    expect(save).toHaveBeenCalledTimes(1);

    await userEvent.click(screen.getByRole('button', { name: 'Close' }));
    const confirm = await screen.findByRole('alertdialog', { name: 'Discard your changes?' });
    within(confirm).getByRole('button', { name: 'Keep editing' }).focus();
    await userEvent.keyboard('{Control>}s{/Control}');
    expect(save).toHaveBeenCalledTimes(1);
  });

  it('runs a shortcut that works over dialogs while one is open', async () => {
    const lock = vi.fn();
    renderInFluent(
      <>
        <Shortcut combo="mod+shift+l" run={lock} overDialogs />
        <OpenDialog />
      </>,
    );

    (await screen.findByRole('button', { name: 'Book' })).focus();
    await userEvent.keyboard('{Control>}{Shift>}L{/Shift}{/Control}');

    expect(lock).toHaveBeenCalledTimes(1);
  });
});
