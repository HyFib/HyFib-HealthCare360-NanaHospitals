import {
  Button,
  DrawerBody,
  DrawerFooter,
  DrawerHeader,
  DrawerHeaderTitle,
  OverlayDrawer,
  Spinner,
} from '@fluentui/react-components';
import { DismissRegular } from '@fluentui/react-icons';
import { useUnsavedChanges } from '@hc360/ui';
import type { FormEventHandler, ReactNode } from 'react';

/**
 * Side drawer for editing a master record (designation, shift, leave type…): title, a form body, and Save/Cancel in
 * the footer. Enter in a field submits; the drawer stays open while saving. Closing it with unsaved changes (`dirty`)
 * asks first, and so does leaving the screen (SRS UX-08).
 */
export function EditorDrawer({
  open,
  title,
  dirty,
  onClose,
  onSubmit,
  busy,
  submitLabel = 'Save',
  children,
  size = 'medium',
  footerExtra,
}: {
  open: boolean;
  title: ReactNode;
  /** The form has unsaved changes: react-hook-form's `formState.isDirty`. */
  dirty: boolean;
  onClose: () => void;
  onSubmit: FormEventHandler<HTMLFormElement>;
  busy?: boolean;
  submitLabel?: string;
  children: ReactNode;
  size?: 'small' | 'medium' | 'large';
  footerExtra?: ReactNode;
}) {
  const guard = useUnsavedChanges({ dirty, busy, open, onClose });
  return (
    <>
      <OverlayDrawer open={open} position="end" size={size} onOpenChange={guard.onOpenChange}>
        <form
          onSubmit={(event) => {
            // Save stays focusable while saving, so Enter can still submit the form: one save at a time.
            if (busy) {
              event.preventDefault();
            } else {
              onSubmit(event);
            }
          }}
          noValidate
          style={{ display: 'contents' }}
        >
          <DrawerHeader>
            <DrawerHeaderTitle
              action={
                <Button
                  appearance="subtle"
                  icon={<DismissRegular />}
                  aria-label="Close"
                  onClick={guard.requestClose}
                  disabled={busy}
                />
              }
            >
              {title}
            </DrawerHeaderTitle>
          </DrawerHeader>
          <DrawerBody>
            <div style={{ display: 'flex', flexDirection: 'column', gap: 12, paddingBottom: 12 }}>{children}</div>
          </DrawerBody>
          <DrawerFooter>
            <Button
              type="submit"
              appearance="primary"
              disabled={busy}
              // While saving the button stays focusable (aria-disabled): a disabled button drops focus out of the open
              // drawer, and Fluent then returns it to the button that opened the drawer, behind it.
              disabledFocusable={busy}
              icon={busy ? <Spinner size="tiny" /> : undefined}
            >
              {submitLabel}
            </Button>
            <Button onClick={guard.requestClose} disabled={busy}>
              Cancel
            </Button>
            {footerExtra}
          </DrawerFooter>
        </form>
      </OverlayDrawer>
      {guard.dialogs}
    </>
  );
}
