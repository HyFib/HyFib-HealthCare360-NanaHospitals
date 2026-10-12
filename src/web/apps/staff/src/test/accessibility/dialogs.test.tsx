import { Field, FluentProvider, Input } from '@fluentui/react-components';
import { hc360LightTheme } from '@hc360/ui';
import { expectNoAxeViolations } from '@hc360/ui/testing';
import { render, screen } from '@testing-library/react';
import type { ReactNode } from 'react';
import { describe, expect, it, vi } from 'vitest';
import { PasswordConfirmDialog } from '@/modules/account/PasswordConfirmDialog';
import { ReasonDialog as BillingReasonDialog } from '@/modules/billing/ui';
import { ReasonDialog as EmrReasonDialog } from '@/modules/emr/ui';
import { EditorDrawer } from '@/modules/hr/shared/EditorDrawer';
import { ReasonDialog as HrReasonDialog } from '@/modules/hr/shared/ReasonDialog';
import { ReasonDialog as InpatientReasonDialog } from '@/modules/inpatient/ReasonDialog';
import { ReasonDialog as InventoryReasonDialog } from '@/modules/inventory/ReasonDialog';
import { ReasonDialog as LabReasonDialog } from '@/modules/lab/common/ReasonDialog';
import { MasterDrawer } from '@/modules/masters/MasterDrawer';
import { ReasonDialog as OtReasonDialog } from '@/modules/ot/shared/feedback';

// The dialogs and drawers the staff screens share, open and while they work, checked with axe-core for WCAG 2.2 A and
// AA problems (SRS UX-10). The dialog is checked with the page behind it, as a screen reader meets it.

const confirm = vi.fn();
const cancel = vi.fn();

const DIALOGS: { name: string; role: 'dialog' | 'alertdialog'; render: (busy: boolean) => ReactNode }[] = [
  {
    name: 'billing ReasonDialog',
    role: 'alertdialog',
    render: (busy) => (
      <BillingReasonDialog
        open
        title="Cancel this bill?"
        confirmLabel="Cancel the bill"
        busy={busy}
        onConfirm={confirm}
        onCancel={cancel}
      />
    ),
  },
  {
    name: 'EMR ReasonDialog',
    role: 'alertdialog',
    render: (busy) => (
      <EmrReasonDialog
        open
        title="Cancel this order?"
        confirmLabel="Cancel the order"
        busy={busy}
        onConfirm={confirm}
        onCancel={cancel}
      />
    ),
  },
  {
    name: 'OT ReasonDialog',
    role: 'alertdialog',
    render: (busy) => (
      <OtReasonDialog
        open
        title="Cancel this booking?"
        confirmLabel="Cancel the booking"
        busy={busy}
        onConfirm={confirm}
        onCancel={cancel}
      />
    ),
  },
  {
    name: 'in-patient ReasonDialog',
    role: 'alertdialog',
    render: (busy) => (
      <InpatientReasonDialog
        open
        title="Block this bed?"
        confirmLabel="Block the bed"
        busy={busy}
        onConfirm={confirm}
        onCancel={cancel}
      />
    ),
  },
  {
    name: 'inventory ReasonDialog',
    role: 'alertdialog',
    render: (busy) => (
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
    name: 'lab ReasonDialog',
    role: 'alertdialog',
    render: (busy) => (
      <LabReasonDialog
        open
        title="Cancel this test?"
        confirmLabel="Cancel the test"
        busy={busy}
        onConfirm={confirm}
        onCancel={cancel}
      />
    ),
  },
  {
    name: 'HR ReasonDialog',
    role: 'dialog',
    render: (busy) => (
      <HrReasonDialog
        open
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
    render: (busy) => (
      <EditorDrawer
        open
        title="Edit the shift"
        busy={busy}
        onSubmit={(event) => event.preventDefault()}
        onClose={cancel}
      >
        <Field label="Name" required>
          <Input />
        </Field>
      </EditorDrawer>
    ),
  },
  {
    name: 'MasterDrawer',
    role: 'dialog',
    render: (busy) => (
      <MasterDrawer
        title="Edit specialty"
        guard={{ close: cancel, requestClose: cancel, dialogs: null }}
        busy={busy}
        submitLabel="Save"
        onSubmit={(event) => event.preventDefault()}
      >
        <Field label="Name" required>
          <Input />
        </Field>
      </MasterDrawer>
    ),
  },
  {
    name: 'PasswordConfirmDialog',
    role: 'alertdialog',
    render: (busy) => (
      <PasswordConfirmDialog
        open
        title="Turn off two-step verification?"
        confirmLabel="Turn it off"
        busy={busy}
        onConfirm={confirm}
        onCancel={cancel}
      >
        Enter your password to confirm it is you.
      </PasswordConfirmDialog>
    ),
  },
];

function show(dialog: ReactNode) {
  render(
    <FluentProvider theme={hc360LightTheme}>
      <main>
        <h1>Screen behind the dialog</h1>
      </main>
      {dialog}
    </FluentProvider>,
  );
}

describe.each(DIALOGS)('$name', ({ role, render: dialog }) => {
  it('has no problems when open', async () => {
    show(dialog(false));
    expect(await screen.findByRole(role)).toBeInTheDocument();
    await expectNoAxeViolations();
  });

  it('has no problems while it works', async () => {
    show(dialog(true));
    expect(await screen.findByRole(role)).toBeInTheDocument();
    await expectNoAxeViolations();
  });
});
