import {
  Button,
  DrawerBody,
  DrawerFooter,
  DrawerHeader,
  DrawerHeaderTitle,
  makeStyles,
  OverlayDrawer,
  tokens,
} from '@fluentui/react-components';
import { DismissRegular } from '@fluentui/react-icons';
import type { ReactNode } from 'react';
import { useT } from '@/i18n';
import { useMediaQuery, WIDE_LAYOUT_QUERY } from '@/lib/hooks';
import type { UnsavedChangesGuard } from '@/lib/unsavedChanges';

const useStyles = makeStyles({
  bottom: {
    maxHeight: '92dvh',
    borderTopLeftRadius: tokens.borderRadiusXLarge,
    borderTopRightRadius: tokens.borderRadiusXLarge,
  },
  body: { display: 'flex', flexDirection: 'column', gap: '16px', paddingBottom: '16px' },
  footer: {
    display: 'flex',
    flexDirection: 'column',
    gap: '8px',
    paddingBottom: 'calc(16px + env(safe-area-inset-bottom))',
    '@media (min-width: 520px)': { flexDirection: 'row-reverse', justifyContent: 'flex-start' },
  },
});

/**
 * A task panel: slides up from the bottom on phones (thumb reach), from the side on wide screens. Focus is trapped
 * inside while open and returns to the button that opened it. A sheet holding a form passes its `guard`
 * (useUnsavedChanges): closing it with unsaved changes then asks first (SRS UX-08).
 */
export function Sheet({
  open,
  title,
  onClose,
  guard,
  children,
  footer,
  dismissible = true,
}: {
  open: boolean;
  title: string;
  onClose: () => void;
  /** The form's unsaved-change guard: the close button, Esc and a tap outside close through it. */
  guard?: UnsavedChangesGuard;
  children: ReactNode;
  footer?: ReactNode;
  /** False while a request is running, so the panel cannot be closed half-way. */
  dismissible?: boolean;
}) {
  const styles = useStyles();
  const t = useT();
  const wide = useMediaQuery(WIDE_LAYOUT_QUERY);
  const requestClose = () => {
    if (!dismissible) {
      return;
    }
    if (guard) {
      guard.requestClose();
    } else {
      onClose();
    }
  };
  return (
    <>
      <OverlayDrawer
        open={open}
        onOpenChange={(_, data) => !data.open && requestClose()}
        position={wide ? 'end' : 'bottom'}
        size={wide ? 'medium' : 'full'}
        className={wide ? undefined : styles.bottom}
        modalType={dismissible ? 'modal' : 'alert'}
      >
        <DrawerHeader>
          <DrawerHeaderTitle
            action={
              <Button
                appearance="subtle"
                size="large"
                icon={<DismissRegular />}
                aria-label={t('common.close')}
                onClick={requestClose}
                disabled={!dismissible}
              />
            }
          >
            {title}
          </DrawerHeaderTitle>
        </DrawerHeader>
        <DrawerBody className={styles.body}>{children}</DrawerBody>
        {footer && <DrawerFooter className={styles.footer}>{footer}</DrawerFooter>}
      </OverlayDrawer>
      {guard?.dialogs}
    </>
  );
}
