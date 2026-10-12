import {
  Button,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  makeStyles,
  Spinner,
  tokens,
} from '@fluentui/react-components';
import type { ReactNode } from 'react';
import { useT } from '@/i18n';

const useStyles = makeStyles({
  surface: { maxWidth: 'min(480px, calc(100vw - 32px))' },
  actions: {
    '@media (max-width: 480px)': { flexDirection: 'column-reverse', alignItems: 'stretch', gap: '8px' },
  },
  destructive: {
    backgroundColor: tokens.colorStatusDangerBackground3,
    ':hover': { backgroundColor: tokens.colorStatusDangerBackground3Hover },
    ':hover:active': { backgroundColor: tokens.colorStatusDangerBackground3Pressed },
  },
});

/** Every irreversible step (cancel an appointment, leave a call, sign out) is confirmed in plain words first. */
export function ConfirmDialog({
  open,
  title,
  children,
  confirmLabel,
  dismissLabel,
  destructive,
  busy,
  onConfirm,
  onDismiss,
}: {
  open: boolean;
  title: string;
  children?: ReactNode;
  confirmLabel: string;
  dismissLabel?: string;
  destructive?: boolean;
  busy?: boolean;
  onConfirm: () => void;
  onDismiss: () => void;
}) {
  const styles = useStyles();
  const t = useT();
  return (
    <Dialog open={open} modalType="alert" onOpenChange={(_, data) => !data.open && !busy && onDismiss()}>
      <DialogSurface className={styles.surface}>
        <DialogBody>
          <DialogTitle>{title}</DialogTitle>
          {children && <DialogContent>{children}</DialogContent>}
          <DialogActions className={styles.actions} fluid>
            <Button appearance="secondary" size="large" onClick={onDismiss} disabled={busy}>
              {dismissLabel ?? t('common.notNow')}
            </Button>
            <Button
              appearance="primary"
              size="large"
              onClick={onConfirm}
              disabled={busy}
              icon={busy ? <Spinner size="tiny" aria-hidden /> : undefined}
              className={destructive ? styles.destructive : undefined}
            >
              {confirmLabel}
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}
