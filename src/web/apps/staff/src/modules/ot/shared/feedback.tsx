import {
  Badge,
  Button,
  Caption1,
  Checkbox,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Field,
  makeStyles,
  MessageBar,
  MessageBarActions,
  MessageBarBody,
  MessageBarTitle,
  Spinner,
  Text,
  Textarea,
  tokens,
} from '@fluentui/react-components';
import { ArrowClockwiseRegular, LockClosedRegular, PersonRegular, ShieldKeyholeRegular } from '@fluentui/react-icons';
import { formatDateTime, isApiError } from '@hc360/core';
import {
  CloseRequestScope,
  EmptyState,
  ErrorState,
  useCloseRequests,
  useUnsavedChanges,
  useValuesChanged,
} from '@hc360/ui';
import { useNavigate } from '@tanstack/react-router';
import { useState, type FormEvent, type KeyboardEvent, type ReactNode } from 'react';
import { isConcurrencyConflict } from './errors';
import { chargeStatusLabel, type ChargeStatus } from './types';

const useStyles = makeStyles({
  details: { display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(200px, 1fr))', gap: '12px 24px' },
  detail: { display: 'flex', flexDirection: 'column', gap: '2px', minWidth: 0 },
  label: { color: tokens.colorNeutralForeground3 },
  value: { overflowWrap: 'anywhere' },
  who: { color: tokens.colorNeutralForeground3, display: 'inline-flex', alignItems: 'center', gap: '4px' },
  charge: { display: 'flex', alignItems: 'center', gap: '8px', flexWrap: 'wrap' },
  reason: { display: 'flex', flexDirection: 'column', gap: '12px' },
  presets: { display: 'flex', gap: '6px', flexWrap: 'wrap' },
});

/**
 * Shows why an action was refused, prominently and next to the action (UX rule 3: safety alerts are never a passing
 * toast). Covers validation (400), rule violations (422), clashes and concurrency (409) and network drops.
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

  const concurrency = isConcurrencyConflict(error);
  const heading = error.isNetworkError
    ? 'Cannot reach the server'
    : error.isValidation
      ? 'Please correct the highlighted fields'
      : concurrency
        ? 'Someone else changed this record'
        : (title ?? error.title);

  return (
    <MessageBar
      intent={error.isNetworkError || concurrency ? 'warning' : 'error'}
      layout="multiline"
      politeness="assertive"
    >
      <MessageBarBody>
        <MessageBarTitle>{heading}</MessageBarTitle>
        {concurrency ? 'Reload to see their changes, then make yours again.' : error.userMessage}
        {error.referenceId && <Caption1 block>Reference: {error.referenceId}</Caption1>}
      </MessageBarBody>
      {concurrency && onReload && (
        <MessageBarActions>
          <Button icon={<ArrowClockwiseRegular />} onClick={onReload}>
            Reload
          </Button>
        </MessageBarActions>
      )}
    </MessageBar>
  );
}

/**
 * Loading failure of a patient record: restricted (VIP) records explain break-the-glass, missing records say so,
 * everything else offers a retry.
 */
export function RecordLoadError({
  error,
  onRetry,
  patientId,
  what = 'record',
}: {
  error: unknown;
  onRetry?: () => void;
  patientId?: string | null;
  what?: string;
}) {
  const navigate = useNavigate();
  if (isApiError(error) && error.code === 'patient.restricted') {
    return (
      <MessageBar intent="warning" layout="multiline" icon={<ShieldKeyholeRegular />}>
        <MessageBarBody>
          <MessageBarTitle>Restricted record</MessageBarTitle>
          {error.userMessage} Emergency access is requested from the patient&apos;s profile; it is time-limited and
          every screen opened is recorded.
        </MessageBarBody>
        {patientId && (
          <MessageBarActions>
            <Button
              icon={<PersonRegular />}
              onClick={() => void navigate({ to: '/patients/$patientId', params: { patientId } })}
            >
              Open patient profile
            </Button>
          </MessageBarActions>
        )}
      </MessageBar>
    );
  }
  if (isApiError(error) && error.status === 404) {
    return (
      <EmptyState
        title={`This ${what} was not found`}
        description="It may have been removed, or the link is wrong. Go back to the list and search again."
      />
    );
  }
  return <ErrorState error={error} onRetry={onRetry} />;
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

export function DetailGrid({ children, minColumnWidth }: { children: ReactNode; minColumnWidth?: number }) {
  const styles = useStyles();
  return (
    <div
      className={styles.details}
      style={
        minColumnWidth ? { gridTemplateColumns: `repeat(auto-fill, minmax(${minColumnWidth}px, 1fr))` } : undefined
      }
    >
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
      <Caption1 className={styles.label}>{label}</Caption1>
      <Text className={styles.value}>{empty ? '—' : children}</Text>
    </div>
  );
}

/** "Confirmed by Sister Mary · 26 Sept 2026, 9:02 am" — every clinical entry shows who and when. */
export function WhoWhen({
  by,
  at,
  prefix = 'Recorded by',
  locked,
}: {
  by: string | null | undefined;
  at: string | null | undefined;
  prefix?: string;
  locked?: boolean;
}) {
  const styles = useStyles();
  if (!by && !at) {
    return null;
  }
  return (
    <Caption1 className={styles.who}>
      {locked && <LockClosedRegular aria-label="Locked" />}
      {prefix} {by ?? 'unknown user'}
      {at ? ` · ${formatDateTime(at)}` : ''}
    </Caption1>
  );
}

const chargeTone: Record<ChargeStatus, 'success' | 'danger' | 'informative' | 'subtle'> = {
  NotDue: 'subtle',
  Posted: 'success',
  Failed: 'danger',
  Cancelled: 'informative',
};

export function ChargeBadge({ status }: { status: ChargeStatus }) {
  return (
    <Badge appearance="tint" color={chargeTone[status]} shape="rounded">
      {chargeStatusLabel[status]}
    </Badge>
  );
}

/**
 * Billing hand-off state with a retry. A failed charge never blocks clinical work: the event is saved and billing
 * can be retried when it is available again.
 */
export function ChargeStatusPanel({
  status,
  error,
  canRetry,
  retrying,
  onRetry,
}: {
  status: ChargeStatus;
  error?: string | null;
  canRetry: boolean;
  retrying?: boolean;
  onRetry?: () => void;
}) {
  const styles = useStyles();
  if (status !== 'Failed') {
    return (
      <div className={styles.charge}>
        <ChargeBadge status={status} />
      </div>
    );
  }
  return (
    <MessageBar intent="warning" layout="multiline">
      <MessageBarBody>
        <MessageBarTitle>The charge could not be posted to the bill</MessageBarTitle>
        {error ? `${error}. ` : ''}The clinical record is saved. Retry when billing is available again; nothing is
        billed twice.
      </MessageBarBody>
      {canRetry && onRetry && (
        <MessageBarActions>
          <Button
            icon={retrying ? <Spinner size="tiny" /> : <ArrowClockwiseRegular />}
            disabled={retrying}
            onClick={onRetry}
          >
            Retry billing
          </Button>
        </MessageBarActions>
      )}
    </MessageBar>
  );
}

export interface ReasonDialogProps {
  open: boolean;
  title: string;
  description?: ReactNode;
  label?: string;
  placeholder?: string;
  confirmLabel: string;
  destructive?: boolean;
  busy?: boolean;
  error?: unknown;
  minLength?: number;
  maxLength?: number;
  /** Quick picks that fill the reason in one click. */
  presets?: string[];
  /** An extra yes/no, e.g. "The patient did not come". */
  option?: { label: string; hint?: string; defaultChecked?: boolean };
  children?: ReactNode;
  /** The fields in `children` have unsaved changes (the typed reason counts by itself). */
  dirty?: boolean;
  onConfirm: (reason: string, option: boolean) => void;
  onCancel: () => void;
}

/** Asks for the reason behind a cancel, postpone, discard or close (every one is audited). Ctrl+Enter confirms. */
export function ReasonDialog(props: ReasonDialogProps) {
  const { open } = props;
  // A fresh form every time the dialog opens: bump the key on each closed → open transition.
  const [session, setSession] = useState(0);
  const [wasOpen, setWasOpen] = useState(open);
  if (wasOpen !== open) {
    setWasOpen(open);
    if (open) {
      setSession((value) => value + 1);
    }
  }

  const closing = useCloseRequests({ onClose: props.onCancel, busy: props.busy });

  return (
    <Dialog open={open} onOpenChange={closing.onOpenChange} modalType="alert">
      <DialogSurface>
        <CloseRequestScope value={closing}>
          <ReasonForm key={session} {...props} />
        </CloseRequestScope>
      </DialogSurface>
    </Dialog>
  );
}

function ReasonForm(props: ReasonDialogProps) {
  const styles = useStyles();
  const { option, minLength = 3, maxLength = 500 } = props;
  const [reason, setReason] = useState('');
  const [checked, setChecked] = useState(option?.defaultChecked ?? false);
  // A typed reason is not thrown away without asking (SRS UX-08).
  const guard = useUnsavedChanges({
    dirty: useValuesChanged({ reason, checked }) || Boolean(props.dirty),
    busy: props.busy,
    onClose: props.onCancel,
  });

  const valid = reason.trim().length >= minLength;
  const submit = (event?: FormEvent) => {
    event?.preventDefault();
    if (valid && !props.busy) {
      props.onConfirm(reason.trim(), checked);
    }
  };
  const onKeyDown = (event: KeyboardEvent) => {
    if (event.key === 'Enter' && (event.ctrlKey || event.metaKey)) {
      submit();
    }
  };

  return (
    <>
      <form onSubmit={submit} noValidate>
        <DialogBody>
          <DialogTitle>{props.title}</DialogTitle>
          <DialogContent className={styles.reason}>
            {props.description && <Text>{props.description}</Text>}
            {props.children}
            {props.presets && props.presets.length > 0 && (
              <div className={styles.presets} role="group" aria-label="Common reasons">
                {props.presets.map((preset) => (
                  <Button
                    key={preset}
                    size="small"
                    appearance={reason === preset ? 'primary' : 'secondary'}
                    onClick={() => setReason(preset)}
                  >
                    {preset}
                  </Button>
                ))}
              </div>
            )}
            <Field
              label={props.label ?? 'Reason'}
              required
              hint={`At least ${minLength} characters. Press Ctrl+Enter to confirm.`}
            >
              <Textarea
                value={reason}
                onChange={(_, data) => setReason(data.value)}
                onKeyDown={onKeyDown}
                placeholder={props.placeholder}
                maxLength={maxLength}
                rows={3}
                resize="vertical"
                autoFocus
              />
            </Field>
            {option && (
              <Field hint={option.hint}>
                <Checkbox
                  checked={checked}
                  onChange={(_, data) => setChecked(data.checked === true)}
                  label={option.label}
                />
              </Field>
            )}
            <ActionError error={props.error} />
          </DialogContent>
          <DialogActions>
            <Button onClick={guard.requestClose} disabled={props.busy}>
              Go back
            </Button>
            <Button
              type="submit"
              appearance="primary"
              disabled={!valid || props.busy}
              // While the request runs the button stays focusable (aria-disabled): a disabled button drops focus out of
              // the open dialog, and Fluent then returns it to the button that opened the dialog, behind it. It still
              // submits the form, which `submit` ignores while busy.
              disabledFocusable={props.busy}
              icon={props.busy ? <Spinner size="tiny" /> : undefined}
              style={props.destructive ? { backgroundColor: tokens.colorPaletteRedBackground3 } : undefined}
            >
              {props.confirmLabel}
            </Button>
          </DialogActions>
        </DialogBody>
      </form>
      {guard.dialogs}
    </>
  );
}
