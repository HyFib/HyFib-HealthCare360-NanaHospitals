import {
  Button,
  DrawerBody,
  DrawerFooter,
  DrawerHeader,
  DrawerHeaderTitle,
  MessageBar,
  MessageBarBody,
  OverlayDrawer,
  Spinner,
  type DrawerProps,
} from '@fluentui/react-components';
import { DismissRegular } from '@fluentui/react-icons';
import { isApiError, useHotkey } from '@hc360/core';
import { useRef, type FormEventHandler, type ReactNode } from 'react';
import type { EditorGuard } from './useEditorGuard';

/**
 * The masters' editor: a drawer from the right holding a form, with Save and Cancel at the foot. Enter in a field and
 * Ctrl+S both save.
 */
export function MasterDrawer({
  title,
  size = 'medium',
  guard,
  busy,
  submitLabel,
  onSubmit,
  footer,
  children,
}: {
  title: string;
  size?: DrawerProps['size'];
  guard: EditorGuard;
  busy: boolean;
  submitLabel: string;
  onSubmit: FormEventHandler<HTMLFormElement>;
  /** Extra actions beside Save and Cancel (a preview, say). */
  footer?: ReactNode;
  children: ReactNode;
}) {
  const surface = useRef<HTMLDivElement>(null);
  const form = useRef<HTMLFormElement>(null);
  useHotkey(
    'mod+s',
    (event) => {
      event.preventDefault();
      form.current?.requestSubmit();
    },
    { allowInInputs: true, enabled: !busy, within: surface },
  );

  return (
    <>
      <OverlayDrawer ref={surface} open position="end" size={size} onOpenChange={guard.onOpenChange}>
        <form
          ref={form}
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
                <Button appearance="subtle" icon={<DismissRegular />} aria-label="Close" onClick={guard.requestClose} />
              }
            >
              {title}
            </DrawerHeaderTitle>
          </DrawerHeader>
          <DrawerBody>{children}</DrawerBody>
          <DrawerFooter>
            <Button
              type="submit"
              appearance="primary"
              disabled={busy}
              // While saving the button stays focusable (aria-disabled): a disabled button drops focus out of the open
              // drawer, and Fluent then returns it to the button that opened the drawer, behind it.
              disabledFocusable={busy}
              icon={busy ? <Spinner size="tiny" aria-hidden /> : undefined}
            >
              {submitLabel}
            </Button>
            <Button onClick={guard.requestClose} disabled={busy}>
              Cancel
            </Button>
            {footer}
          </DrawerFooter>
        </form>
      </OverlayDrawer>
      {guard.dialogs}
    </>
  );
}

/** The drawer while a record loads for editing, or why it could not be loaded. */
export function LoadingDrawer({
  error,
  what,
  size = 'medium',
  onClose,
}: {
  error: unknown;
  what: string;
  size?: DrawerProps['size'];
  onClose: () => void;
}) {
  return (
    <OverlayDrawer open position="end" size={size} onOpenChange={(_, data) => !data.open && onClose()}>
      <DrawerHeader>
        <DrawerHeaderTitle
          action={<Button appearance="subtle" icon={<DismissRegular />} aria-label="Close" onClick={onClose} />}
        >
          {error ? `Could not open the ${what}` : `Opening the ${what}…`}
        </DrawerHeaderTitle>
      </DrawerHeader>
      <DrawerBody>
        {error ? (
          <MessageBar intent="error">
            <MessageBarBody>
              {isApiError(error) ? error.userMessage : `The ${what} could not be loaded. Close this and try again.`}
            </MessageBarBody>
          </MessageBar>
        ) : (
          <Spinner label="Loading…" />
        )}
      </DrawerBody>
    </OverlayDrawer>
  );
}
