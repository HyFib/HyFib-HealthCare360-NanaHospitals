import { Badge, Caption1, makeStyles, Text, tokens } from '@fluentui/react-components';
import { CheckmarkCircleRegular, ClockRegular, WarningRegular } from '@fluentui/react-icons';
import { StatusBadge } from '@hc360/ui';
import {
  breachStatusLabel,
  correctionDecisionLabel,
  requestStatusLabel,
  retentionActionLabel,
  type BreachStatus,
  type CorrectionDecision,
  type DataRequestStatus,
  type DataRequestType,
  type RetentionAction,
} from './labels';
import { breachStatusTone, identityRequired, isOpenStatus, requestStatusTone } from './rules';

const useStyles = makeStyles({
  state: { display: 'inline-flex', alignItems: 'center', gap: '4px' },
  ok: { color: tokens.colorPaletteGreenForeground1 },
  todo: { color: tokens.colorPaletteMarigoldForeground1 },
  muted: { color: tokens.colorNeutralForeground3 },
});

export function RequestStatusBadge({ status, isOverdue }: { status: DataRequestStatus; isOverdue?: boolean }) {
  const overdue = Boolean(isOverdue) && isOpenStatus(status);
  return (
    <StatusBadge
      status={status}
      tone={requestStatusTone(status, overdue)}
      label={overdue ? `${requestStatusLabel[status]} · overdue` : requestStatusLabel[status]}
    />
  );
}

export function BreachStatusBadge({ status, reportOverdue }: { status: BreachStatus; reportOverdue?: boolean }) {
  const overdue = Boolean(reportOverdue);
  return (
    <StatusBadge
      status={status}
      tone={breachStatusTone(status, overdue)}
      label={overdue ? `${breachStatusLabel[status]} · report overdue` : breachStatusLabel[status]}
    />
  );
}

export function DecisionBadge({ decision }: { decision: CorrectionDecision }) {
  const tone = decision === 'Accepted' ? 'success' : decision === 'Declined' ? 'informative' : 'warning';
  return <StatusBadge status={decision} tone={tone} label={correctionDecisionLabel[decision]} />;
}

export function RetentionActionBadge({ action }: { action: RetentionAction }) {
  const color = action === 'Delete' ? 'warning' : action === 'Review' ? 'brand' : 'informative';
  return (
    <Badge appearance="tint" shape="rounded" color={color}>
      {retentionActionLabel[action]}
    </Badge>
  );
}

/** Whether the requester's identity was checked, in words with an icon (never colour alone). */
export function IdentityState({ verified, type }: { verified: boolean; type: DataRequestType }) {
  const styles = useStyles();
  if (verified) {
    return (
      <span className={styles.state}>
        <CheckmarkCircleRegular className={styles.ok} aria-hidden />
        <Text>Checked</Text>
      </span>
    );
  }
  if (!identityRequired(type)) {
    return <Caption1 className={styles.muted}>Not needed</Caption1>;
  }
  return (
    <span className={styles.state}>
      <WarningRegular className={styles.todo} aria-hidden />
      <Text>Not yet</Text>
    </span>
  );
}

/** A deadline or clock in words, red and with an icon when overdue. */
export function DeadlineWords({ text, overdue, warn }: { text: string; overdue: boolean; warn?: boolean }) {
  const styles = useStyles();
  return (
    <span className={styles.state}>
      {overdue ? (
        <WarningRegular style={{ color: tokens.colorPaletteRedForeground1 }} aria-hidden />
      ) : (
        <ClockRegular className={warn ? styles.todo : styles.muted} aria-hidden />
      )}
      <Text
        weight={overdue || warn ? 'semibold' : 'regular'}
        style={{ color: overdue ? tokens.colorPaletteRedForeground1 : undefined }}
      >
        {text}
      </Text>
    </span>
  );
}
