import {
  Button,
  Field,
  FluentProvider,
  Input,
  Menu,
  MenuItem,
  MenuList,
  MenuPopover,
  MenuTrigger,
  useRestoreFocusTarget,
  webLightTheme,
} from '@fluentui/react-components';
import { ConfirmDialog } from '@hc360/ui';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { useState, type ReactNode } from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { ReasonDialog as BillingReasonDialog } from '@/modules/billing/ui';
import { ReasonDialog as EmrReasonDialog } from '@/modules/emr/ui';
import { EditorDrawer } from '@/modules/hr/shared/EditorDrawer';
import { ReasonDialog as HrReasonDialog } from '@/modules/hr/shared/ReasonDialog';
import { ReasonDialog as InpatientReasonDialog } from '@/modules/inpatient/ReasonDialog';
import { ReasonDialog as InventoryReasonDialog } from '@/modules/inventory/ReasonDialog';
import { ReasonDialog as LabReasonDialog } from '@/modules/lab/common/ReasonDialog';
import { ReasonDialog as OtReasonDialog } from '@/modules/ot/shared/feedback';

// The dialogs the screens share, opened from a row menu as the screens open them: closing one returns focus to the
// menu button, and while one is busy, focus stays in it.

interface DialogState {
  open: boolean;
  busy: boolean;
  confirm: () => void;
  cancel: () => void;
}

interface DialogCase {
  name: string;
  role: 'dialog' | 'alertdialog';
  confirmLabel: string;
  /** The confirm button waits for a reason. */
  needsReason?: boolean;
  render: (dialog: DialogState) => ReactNode;
}

const DIALOGS: DialogCase[] = [
  {
    name: 'ConfirmDialog',
    role: 'alertdialog',
    confirmLabel: 'Delete',
    render: ({ open, busy, confirm, cancel }) => (
      <ConfirmDialog
        open={open}
        title="Delete the template?"
        confirmLabel="Delete"
        busy={busy}
        onConfirm={confirm}
        onCancel={cancel}
      >
        It cannot be brought back.
      </ConfirmDialog>
    ),
  },
  {
    name: 'the billing ReasonDialog',
    role: 'alertdialog',
    confirmLabel: 'Cancel the bill',
    needsReason: true,
    render: ({ open, busy, confirm, cancel }) => (
      <BillingReasonDialog
        open={open}
        title="Cancel this bill?"
        confirmLabel="Cancel the bill"
        busy={busy}
        onConfirm={confirm}
        onCancel={cancel}
      />
    ),
  },
  {
    name: 'the EMR ReasonDialog',
    role: 'alertdialog',
    confirmLabel: 'Cancel the order',
    needsReason: true,
    render: ({ open, busy, confirm, cancel }) => (
      <EmrReasonDialog
        open={open}
        title="Cancel this order?"
        confirmLabel="Cancel the order"
        busy={busy}
        onConfirm={confirm}
        onCancel={cancel}
      />
    ),
  },
  {
    name: 'the OT ReasonDialog',
    role: 'alertdialog',
    confirmLabel: 'Cancel the booking',
    needsReason: true,
    render: ({ open, busy, confirm, cancel }) => (
      <OtReasonDialog
        open={open}
        title="Cancel this booking?"
        confirmLabel="Cancel the booking"
        busy={busy}
        onConfirm={confirm}
        onCancel={cancel}
      />
    ),
  },
  {
    name: 'the in-patient ReasonDialog',
    role: 'alertdialog',
    confirmLabel: 'Block the bed',
    needsReason: true,
    render: ({ open, busy, confirm, cancel }) => (
      <InpatientReasonDialog
        open={open}
        title="Block this bed?"
        confirmLabel="Block the bed"
        busy={busy}
        onConfirm={confirm}
        onCancel={cancel}
      />
    ),
  },
  {
    name: 'the inventory ReasonDialog',
    role: 'alertdialog',
    confirmLabel: 'Reject the indent',
    needsReason: true,
    // Mounted only while needed.
    render: ({ open, busy, confirm, cancel }) =>
      open && (
        <InventoryReasonDialog
          title="Reject this indent?"
          confirmLabel="Reject the indent"
          busy={busy}
          onSubmit={confirm}
          onClose={cancel}
        />
      ),
  },
  {
    name: 'the lab ReasonDialog',
    role: 'alertdialog',
    confirmLabel: 'Cancel the test',
    needsReason: true,
    render: ({ open, busy, confirm, cancel }) => (
      <LabReasonDialog
        open={open}
        title="Cancel this test?"
        confirmLabel="Cancel the test"
        busy={busy}
        onConfirm={confirm}
        onCancel={cancel}
      />
    ),
  },
  {
    name: 'the HR ReasonDialog',
    role: 'dialog',
    confirmLabel: 'Reject the leave',
    needsReason: true,
    render: ({ open, busy, confirm, cancel }) => (
      <HrReasonDialog
        open={open}
        title="Reject this leave?"
        confirmLabel="Reject the leave"
        busy={busy}
        onConfirm={confirm}
        onCancel={cancel}
      />
    ),
  },
  {
    name: 'EditorDrawer',
    role: 'dialog',
    confirmLabel: 'Save',
    render: ({ open, busy, confirm, cancel }) => (
      <EditorDrawer
        open={open}
        title="Edit the shift"
        dirty={false}
        busy={busy}
        onSubmit={(event) => {
          event.preventDefault();
          confirm();
        }}
        onClose={cancel}
      >
        <Field label="Name">
          <Input />
        </Field>
      </EditorDrawer>
    ),
  },
];

const confirmed = vi.fn();
const cancelled = vi.fn();

function Opener({ dialog }: { dialog: DialogCase }) {
  const restoreFocusTarget = useRestoreFocusTarget();
  const [open, setOpen] = useState(false);
  const [busy, setBusy] = useState(false);
  return (
    <>
      <Menu>
        <MenuTrigger disableButtonEnhancement>
          <Button {...restoreFocusTarget}>Actions</Button>
        </MenuTrigger>
        <MenuPopover>
          <MenuList>
            <MenuItem onClick={() => setOpen(true)}>Open the dialog…</MenuItem>
          </MenuList>
        </MenuPopover>
      </Menu>
      {dialog.render({
        open,
        busy,
        // The request never ends, so the dialog stays busy.
        confirm: () => {
          confirmed();
          setBusy(true);
        },
        cancel: () => {
          cancelled();
          setOpen(false);
        },
      })}
    </>
  );
}

function renderOpener(dialog: DialogCase) {
  render(
    <FluentProvider theme={webLightTheme}>
      <Opener dialog={dialog} />
    </FluentProvider>,
  );
}

describe.each(DIALOGS)('$name opened from a menu', (dialog) => {
  beforeEach(() => {
    confirmed.mockClear();
    cancelled.mockClear();
  });

  it('returns focus to the menu button when closed from the keyboard', async () => {
    renderOpener(dialog);
    const actions = screen.getByRole('button', { name: 'Actions' });

    actions.focus();
    await userEvent.keyboard('{Enter}');
    await waitFor(() => expect(screen.getByRole('menuitem', { name: 'Open the dialog…' })).toHaveFocus());
    await userEvent.keyboard('{Enter}');
    const surface = await screen.findByRole(dialog.role);
    await waitFor(() => expect(surface).toContainElement(document.activeElement as HTMLElement));
    await userEvent.keyboard('{Escape}');

    await waitFor(() => expect(screen.queryByRole(dialog.role)).not.toBeInTheDocument());
    await waitFor(() => expect(actions).toHaveFocus());
  });

  it('keeps focus on the confirm button while busy, and neither confirms again nor closes', async () => {
    renderOpener(dialog);

    await userEvent.click(screen.getByRole('button', { name: 'Actions' }));
    await userEvent.click(await screen.findByRole('menuitem', { name: 'Open the dialog…' }));
    const surface = await screen.findByRole(dialog.role);
    if (dialog.needsReason) {
      await userEvent.type(within(surface).getByRole('textbox'), 'The patient asked to stop');
    }
    const confirm = within(surface).getByRole('button', { name: dialog.confirmLabel });
    await userEvent.click(confirm);

    // A disabled button loses focus in the browser, and focus that leaves the dialog goes back to the menu behind it.
    await waitFor(() => expect(confirm).toHaveAttribute('aria-disabled', 'true'));
    expect(confirm).not.toBeDisabled();
    expect(confirm).toHaveFocus();
    await userEvent.keyboard('{Enter}');
    await userEvent.keyboard('{Escape}');
    expect(confirmed).toHaveBeenCalledTimes(1);
    expect(cancelled).not.toHaveBeenCalled();
    expect(screen.getByRole(dialog.role)).toBe(surface);
  });
});
