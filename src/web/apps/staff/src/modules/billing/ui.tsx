import {
  Button,
  Caption1,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Field,
  Input,
  makeStyles,
  mergeClasses,
  MessageBar,
  MessageBarActions,
  MessageBarBody,
  MessageBarTitle,
  Spinner,
  Text,
  Textarea,
  tokens,
  type FieldProps,
  type InputProps,
} from '@fluentui/react-components';
import { ArrowClockwiseRegular, ShieldKeyholeRegular } from '@fluentui/react-icons';
import { formatInr, isApiError } from '@hc360/core';
import { EmptyState, StatusBadge } from '@hc360/ui';
import { useState, type FormEvent, type ReactNode, type Ref } from 'react';
import {
  BILL_STATUS_LABEL,
  DISCOUNT_APPROVAL_STATUS_LABEL,
  GATEWAY_ORDER_STATUS_LABEL,
  IP_ACCOUNT_STATUS_LABEL,
  REFUND_STATUS_LABEL,
  type BillStatus,
  type DiscountApprovalStatus,
  type GatewayOrderStatus,
  type IpAccountStatus,
  type RefundStatus,
} from './labels';

const useStyles = makeStyles({
  amount: { fontVariantNumeric: 'tabular-nums', whiteSpace: 'nowrap' },
  strong: { fontWeight: tokens.fontWeightSemibold },
  large: { fontSize: tokens.fontSizeBase500, fontWeight: tokens.fontWeightSemibold },
  hero: { fontSize: '34px', lineHeight: '40px', fontWeight: tokens.fontWeightBold, letterSpacing: '-0.5px' },
  negative: { color: tokens.colorPaletteRedForeground1 },
  positive: { color: tokens.colorPaletteGreenForeground1 },
  muted: { color: tokens.colorNeutralForeground3 },
  details: { display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(200px, 1fr))', gap: '12px 24px' },
  detail: { display: 'flex', flexDirection: 'column', gap: '2px', minWidth: 0 },
  value: { overflowWrap: 'anywhere' },
  rows: { display: 'flex', flexDirection: 'column', gap: '4px' },
  row: { display: 'flex', justifyContent: 'space-between', alignItems: 'baseline', gap: '16px' },
  rowStrong: {
    borderTop: `1px solid ${tokens.colorNeutralStroke2}`,
    paddingTop: '6px',
    marginTop: '2px',
    fontWeight: tokens.fontWeightSemibold,
  },
  kbd: {
    fontFamily: tokens.fontFamilyMonospace,
    fontSize: tokens.fontSizeBase100,
    border: `1px solid ${tokens.colorNeutralStroke1}`,
    borderBottomWidth: '2px',
    borderRadius: tokens.borderRadiusSmall,
    padding: '0 4px',
    marginLeft: '6px',
    color: tokens.colorNeutralForeground2,
    backgroundColor: tokens.colorNeutralBackground3,
  },
  dialogBody: { display: 'flex', flexDirection: 'column', gap: '12px' },
});

/** An INR amount (Indian digit grouping, tabular digits). Negative amounts keep their minus sign. */
export function Amount({
  value,
  strong,
  size,
  tone,
  className,
}: {
  value: number | null | undefined;
  strong?: boolean;
  size?: 'normal' | 'large' | 'hero';
  tone?: 'neutral' | 'negative' | 'positive' | 'muted';
  className?: string;
}) {
  const styles = useStyles();
  return (
    <span
      className={mergeClasses(
        styles.amount,
        strong && styles.strong,
        size === 'large' && styles.large,
        size === 'hero' && styles.hero,
        tone === 'negative' && styles.negative,
        tone === 'positive' && styles.positive,
        tone === 'muted' && styles.muted,
        className,
      )}
    >
      {formatInr(value)}
    </span>
  );
}

/** A label/value row in a totals list; `strong` draws the separator used before a grand total. */
export function SummaryRow({ label, children, strong, hint }: { label: ReactNode; children: ReactNode; strong?: boolean; hint?: ReactNode }) {
  const styles = useStyles();
  return (
    <div className={mergeClasses(styles.row, strong && styles.rowStrong)}>
      <span>
        <Text weight={strong ? 'semibold' : 'regular'}>{label}</Text>
        {hint && (
          <Caption1 block className={styles.muted}>
            {hint}
          </Caption1>
        )}
      </span>
      <span>{children}</span>
    </div>
  );
}

export function SummaryList({ children }: { children: ReactNode }) {
  const styles = useStyles();
  return <div className={styles.rows}>{children}</div>;
}

export function DetailGrid({ children, minColumnWidth }: { children: ReactNode; minColumnWidth?: number }) {
  const styles = useStyles();
  return (
    <div className={styles.details} style={minColumnWidth ? { gridTemplateColumns: `repeat(auto-fill, minmax(${minColumnWidth}px, 1fr))` } : undefined}>
      {children}
    </div>
  );
}

/** Label over value; empty values show an em dash. */
export function Detail({ label, children }: { label: string; children: ReactNode }) {
  const styles = useStyles();
  const empty = children === null || children === undefined || children === '';
  return (
    <div className={styles.detail}>
      <Caption1 className={styles.muted}>{label}</Caption1>
      <Text className={styles.value}>{empty ? '—' : children}</Text>
    </div>
  );
}

/** Keyboard shortcut hint shown next to a button label. */
export function KeyHint({ children }: { children: ReactNode }) {
  const styles = useStyles();
  return (
    <kbd className={styles.kbd} aria-hidden>
      {children}
    </kbd>
  );
}

const BILL_TONE: Record<BillStatus, 'informative' | 'warning' | 'success' | 'danger'> = {
  Draft: 'informative',
  Finalized: 'warning',
  PartiallyPaid: 'warning',
  Paid: 'success',
  Cancelled: 'danger',
  Refunded: 'danger',
};

export function BillStatusBadge({ status }: { status: BillStatus }) {
  return <StatusBadge status={status} tone={BILL_TONE[status]} label={BILL_STATUS_LABEL[status]} />;
}

export function RefundStatusBadge({ status }: { status: RefundStatus }) {
  const tone = status === 'Paid' ? 'success' : status === 'Rejected' ? 'danger' : status === 'Approved' ? 'brand' : 'warning';
  return <StatusBadge status={status} tone={tone} label={REFUND_STATUS_LABEL[status]} />;
}

export function DiscountStatusBadge({ status }: { status: DiscountApprovalStatus }) {
  return <StatusBadge status={status} label={DISCOUNT_APPROVAL_STATUS_LABEL[status]} />;
}

export function LinkStatusBadge({ status }: { status: GatewayOrderStatus }) {
  return <StatusBadge status={status} tone={status === 'Created' ? 'warning' : undefined} label={GATEWAY_ORDER_STATUS_LABEL[status]} />;
}

export function IpStatusBadge({ status }: { status: IpAccountStatus }) {
  const tone = status === 'Open' ? 'brand' : status === 'Closed' ? 'success' : status === 'Cancelled' ? 'danger' : 'warning';
  return <StatusBadge status={status} tone={tone} label={IP_ACCOUNT_STATUS_LABEL[status]} />;
}

/** Shown when a user reaches a screen they have no permission for (the server refuses it too). */
export function NoAccess({ what }: { what: string }) {
  return (
    <EmptyState
      icon={<ShieldKeyholeRegular />}
      title={`You don't have access to ${what}`}
      description="Ask your administrator to add the permission to your role if you need it for your work."
    />
  );
}

/**
 * Why an action was refused, shown next to the action (business rules are never a passing toast): validation,
 * rule violations, concurrency clashes and network drops.
 */
export function ActionError({ error, title, onReload }: { error: unknown; title?: string; onReload?: () => void }) {
  if (!error) {
    return null;
  }
  if (!isApiError(error)) {
    return (
      <MessageBar intent="error" layout="multiline" politeness="assertive">
        <MessageBarBody>
          <MessageBarTitle>{title ?? 'Something went wrong'}</MessageBarTitle>
          Please try again.
        </MessageBarBody>
      </MessageBar>
    );
  }
  const conflict = error.isConflict && error.code !== 'billing.draft_exists';
  const heading = error.isNetworkError
    ? 'Cannot reach the server'
    : conflict
      ? 'Someone else changed this record'
      : error.isValidation
        ? 'Please check the details'
        : (title ?? error.title);
  return (
    <MessageBar intent={error.isNetworkError || conflict ? 'warning' : 'error'} layout="multiline" politeness="assertive">
      <MessageBarBody>
        <MessageBarTitle>{heading}</MessageBarTitle>
        {conflict ? 'Reload to see the latest version, then try again.' : error.userMessage}
        {error.referenceId && <Caption1 block>Reference: {error.referenceId}</Caption1>}
      </MessageBarBody>
      {conflict && onReload && (
        <MessageBarActions>
          <Button icon={<ArrowClockwiseRegular />} onClick={onReload}>
            Reload
          </Button>
        </MessageBarActions>
      )}
    </MessageBar>
  );
}

/** Rupee input: digits with optional paise, the ₹ sign in front and a numeric keypad on tablets. */
export function AmountField({
  label,
  value,
  onChange,
  hint,
  error,
  required,
  autoFocus,
  disabled,
  placeholder,
  size,
  inputRef,
  onKeyDown,
}: {
  label: string;
  value: string;
  onChange: (value: string) => void;
  hint?: FieldProps['hint'];
  error?: string | null;
  required?: boolean;
  autoFocus?: boolean;
  disabled?: boolean;
  placeholder?: string;
  size?: InputProps['size'];
  inputRef?: Ref<HTMLInputElement>;
  onKeyDown?: InputProps['onKeyDown'];
}) {
  return (
    <Field label={label} hint={hint} required={required} validationMessage={error ?? undefined} validationState={error ? 'error' : 'none'}>
      <Input
        ref={inputRef}
        value={value}
        onChange={(_, data) => onChange(data.value)}
        contentBefore={<Text>₹</Text>}
        inputMode="decimal"
        autoComplete="off"
        autoFocus={autoFocus}
        disabled={disabled}
        placeholder={placeholder ?? '0.00'}
        size={size}
        onKeyDown={onKeyDown}
      />
    </Field>
  );
}

export interface ReasonDialogProps {
  open: boolean;
  title: string;
  /** What will happen, in plain words. */
  description?: ReactNode;
  label?: string;
  placeholder?: string;
  hint?: string;
  confirmLabel: string;
  destructive?: boolean;
  /** Reason optional (e.g. approval remarks); required by default. */
  optional?: boolean;
  /** Server rule: reasons need at least 5 characters. */
  minLength?: number;
  maxLength?: number;
  busy?: boolean;
  /** Server error shown inside the dialog. */
  error?: unknown;
  children?: ReactNode;
  onConfirm: (reason: string) => void;
  onCancel: () => void;
}

/**
 * Confirmation that captures a reason (cancel a bill, reject a refund, approve a discharge with dues…). Enter
 * submits; the confirm button stays disabled until the reason is long enough, so nothing irreversible happens by
 * accident.
 */
export function ReasonDialog(props: ReasonDialogProps) {
  const styles = useStyles();
  const [reason, setReason] = useState('');
  const [wasOpen, setWasOpen] = useState(props.open);
  if (props.open !== wasOpen) {
    setWasOpen(props.open);
    if (props.open) {
      setReason('');
    }
  }
  const minLength = props.optional ? 0 : (props.minLength ?? 5);
  const trimmed = reason.trim();
  const valid = trimmed.length >= minLength;

  const submit = (event: Pick<FormEvent, 'preventDefault'>) => {
    event.preventDefault();
    if (valid && !props.busy) {
      props.onConfirm(trimmed);
    }
  };

  return (
    <Dialog open={props.open} onOpenChange={(_, data) => !data.open && !props.busy && props.onCancel()} modalType="alert">
      <DialogSurface style={{ maxWidth: 540 }}>
        <form onSubmit={submit}>
          <DialogBody>
            <DialogTitle>{props.title}</DialogTitle>
            <DialogContent className={styles.dialogBody}>
              {props.description && <Text>{props.description}</Text>}
              {props.children}
              <Field
                label={props.label ?? 'Reason'}
                required={!props.optional}
                hint={props.hint ?? (minLength > 0 ? `At least ${minLength} characters` : undefined)}
              >
                <Textarea
                  value={reason}
                  onChange={(_, data) => setReason(data.value)}
                  placeholder={props.placeholder}
                  maxLength={props.maxLength ?? 300}
                  rows={3}
                  resize="vertical"
                  autoFocus
                  onKeyDown={(event) => {
                    if (event.key === 'Enter' && !event.shiftKey) {
                      submit(event);
                    }
                  }}
                />
              </Field>
              <ActionError error={props.error} />
            </DialogContent>
            <DialogActions>
              <Button onClick={props.onCancel} disabled={props.busy}>
                Cancel
              </Button>
              <Button
                type="submit"
                appearance="primary"
                disabled={!valid || props.busy}
                icon={props.busy ? <Spinner size="tiny" /> : undefined}
                style={props.destructive ? { backgroundColor: tokens.colorPaletteRedBackground3 } : undefined}
              >
                {props.confirmLabel}
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
      </DialogSurface>
    </Dialog>
  );
}
