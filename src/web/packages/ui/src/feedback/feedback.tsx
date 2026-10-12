import {
  Badge,
  Body1,
  Button,
  Caption1,
  Card,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  makeStyles,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  Spinner,
  Subtitle1,
  Text,
  Toast,
  ToastBody,
  ToastTitle,
  tokens,
  useToastController,
} from '@fluentui/react-components';
import { ArrowClockwiseRegular, DocumentSearchRegular } from '@fluentui/react-icons';
import { humanize, isApiError, isFeatureDisabled } from '@hc360/core';
import type { ReactNode } from 'react';
import { statusTone, type StatusTone } from './status';

const useStyles = makeStyles({
  empty: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    gap: '8px',
    padding: '40px 16px',
    textAlign: 'center',
    color: tokens.colorNeutralForeground3,
  },
  emptyIcon: { fontSize: '40px', color: tokens.colorNeutralForeground4, display: 'flex' },
  loading: { display: 'flex', justifyContent: 'center', padding: '40px 16px' },
  kpi: { display: 'flex', flexDirection: 'column', gap: '4px', padding: '14px 16px', minWidth: '160px' },
  kpiValue: {
    fontSize: tokens.fontSizeHero700,
    fontWeight: tokens.fontWeightSemibold,
    lineHeight: tokens.lineHeightHero700,
  },
  kpiLabel: { color: tokens.colorNeutralForeground3 },
  kpiHeader: { display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: '8px' },
  kpiIcon: { color: tokens.colorBrandForeground1, fontSize: '20px', display: 'flex' },
});

export function EmptyState({
  title,
  description,
  action,
  icon,
}: {
  title: string;
  description?: string;
  action?: ReactNode;
  icon?: ReactNode;
}) {
  const styles = useStyles();
  return (
    <div className={styles.empty} role="status">
      <span className={styles.emptyIcon}>{icon ?? <DocumentSearchRegular />}</span>
      <Subtitle1 as="p">{title}</Subtitle1>
      {description && <Body1>{description}</Body1>}
      {action}
    </div>
  );
}

export function LoadingState({ label = 'Loading…' }: { label?: string }) {
  const styles = useStyles();
  return (
    <div className={styles.loading}>
      <Spinner label={label} />
    </div>
  );
}

/**
 * Friendly error with retry and the support reference ID (never raw stack traces). A feature an administrator switched
 * off is not a failure: it is explained calmly, without a retry that could only be refused again.
 */
export function ErrorState({ error, onRetry, title }: { error: unknown; onRetry?: () => void; title?: string }) {
  const message = isApiError(error) ? error.userMessage : 'Something went wrong while loading this page.';
  const reference = isApiError(error) ? error.referenceId : undefined;
  const switchedOff = isFeatureDisabled(error);
  return (
    <MessageBar
      intent={switchedOff ? 'info' : isApiError(error) && error.isNetworkError ? 'warning' : 'error'}
      layout="multiline"
    >
      <MessageBarBody>
        <MessageBarTitle>{title ?? (isApiError(error) ? error.title : 'Could not load')}</MessageBarTitle>
        {message}
        {reference && !switchedOff && <Caption1 block>Reference: {reference}</Caption1>}
      </MessageBarBody>
      {onRetry && !switchedOff && (
        <Button appearance="transparent" icon={<ArrowClockwiseRegular />} onClick={onRetry}>
          Retry
        </Button>
      )}
    </MessageBar>
  );
}

/** Colour-coded status pill derived from common workflow words, with an explicit override. */
export function StatusBadge({
  status,
  tone,
  label,
}: {
  status: string | null | undefined;
  tone?: StatusTone;
  label?: string;
}) {
  if (!status) {
    return null;
  }
  return (
    <Badge appearance="tint" color={tone ?? statusTone(status)} shape="rounded">
      {label ?? humanize(status)}
    </Badge>
  );
}

export function KpiCard({
  label,
  value,
  hint,
  icon,
  onClick,
}: {
  label: string;
  value: ReactNode;
  hint?: ReactNode;
  icon?: ReactNode;
  onClick?: () => void;
}) {
  const styles = useStyles();
  return (
    <Card
      className={styles.kpi}
      onClick={onClick}
      appearance="filled"
      focusMode={onClick ? 'no-tab' : 'off'}
      role={onClick ? 'button' : undefined}
    >
      <div className={styles.kpiHeader}>
        <Text className={styles.kpiLabel}>{label}</Text>
        {icon && <span className={styles.kpiIcon}>{icon}</span>}
      </div>
      <span className={styles.kpiValue}>{value}</span>
      {hint && <Caption1 className={styles.kpiLabel}>{hint}</Caption1>}
    </Card>
  );
}

export function ConfirmDialog({
  open,
  title,
  children,
  confirmLabel = 'Confirm',
  cancelLabel = 'Cancel',
  destructive,
  busy,
  onConfirm,
  onCancel,
}: {
  open: boolean;
  title: string;
  children: ReactNode;
  confirmLabel?: string;
  cancelLabel?: string;
  destructive?: boolean;
  busy?: boolean;
  onConfirm: () => void;
  onCancel: () => void;
}) {
  return (
    <Dialog open={open} onOpenChange={(_, data) => !data.open && !busy && onCancel()} modalType="alert">
      <DialogSurface>
        <DialogBody>
          <DialogTitle>{title}</DialogTitle>
          <DialogContent>{children}</DialogContent>
          <DialogActions>
            <Button appearance="secondary" onClick={onCancel} disabled={busy}>
              {cancelLabel}
            </Button>
            <Button
              appearance="primary"
              onClick={onConfirm}
              disabled={busy}
              // While the request runs the button stays focusable (aria-disabled): a disabled button drops focus out of
              // the open dialog, and Fluent then returns it to the button that opened the dialog, behind it.
              disabledFocusable={busy}
              // Decorative: the button says it is busy by being unavailable (aria-disabled).
              icon={busy ? <Spinner size="tiny" aria-hidden /> : undefined}
              style={destructive ? { backgroundColor: tokens.colorPaletteRedBackground3 } : undefined}
            >
              {confirmLabel}
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}

export const TOASTER_ID = 'hc360-toaster';

/** Toast helper bound to the app-wide Toaster (rendered once in the shell). */
export function useNotify() {
  const { dispatchToast } = useToastController(TOASTER_ID);
  const show = (intent: 'success' | 'error' | 'info' | 'warning', title: string, body?: ReactNode) =>
    dispatchToast(
      <Toast>
        <ToastTitle>{title}</ToastTitle>
        {body && <ToastBody>{body}</ToastBody>}
      </Toast>,
      { intent, timeout: intent === 'error' ? 8000 : 4000 },
    );
  return {
    success: (title: string, body?: ReactNode) => show('success', title, body),
    info: (title: string, body?: ReactNode) => show('info', title, body),
    warning: (title: string, body?: ReactNode) => show('warning', title, body),
    error: (error: unknown, fallback = 'Something went wrong') =>
      show('error', isApiError(error) ? error.title : fallback, isApiError(error) ? error.userMessage : undefined),
  };
}
