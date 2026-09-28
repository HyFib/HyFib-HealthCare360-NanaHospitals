import { Badge, makeStyles, mergeClasses, Text, tokens } from '@fluentui/react-components';
import { DropRegular, WarningRegular } from '@fluentui/react-icons';
import { formatDateTime, type BloodGroup } from '@hc360/core';
import { StatusBadge } from '@hc360/ui';
import type {
  BloodRequestStatus,
  BloodRequestUrgency,
  BloodUnitStatus,
  DonorEligibilityOutcome,
  ReactionSeverity,
  ReactionStatus,
  TtiPanel,
} from './api';
import {
  groupLabel,
  outcomeLabel,
  outcomeTone,
  reactionStatusLabel,
  reactionStatusTone,
  requestStatusLabel,
  requestStatusTone,
  severityLabel,
  severityTone,
  unitStatusLabel,
  unitStatusTone,
  urgencyLabel,
  urgencyTone,
} from './labels';
import { expiryUrgency, ttiState } from './rules';

const useStyles = makeStyles({
  group: { fontWeight: tokens.fontWeightBold, letterSpacing: '0.5px' },
  expired: { color: tokens.colorPaletteRedForeground1, fontWeight: tokens.fontWeightSemibold },
  today: { color: tokens.colorPaletteRedForeground1 },
  soon: { color: tokens.colorPaletteDarkOrangeForeground1 },
});

/** ABO/Rh(D) group in large type; "Not grouped" (never "Unknown") until the blood bank has grouped the sample. */
export function BloodGroupBadge({ group, size = 'large', note }: { group: BloodGroup | null | undefined; size?: 'medium' | 'large' | 'extra-large'; note?: string }) {
  const styles = useStyles();
  const known = Boolean(group && group !== 'Unknown');
  return (
    <Badge
      appearance={known ? 'filled' : 'outline'}
      color={known ? 'danger' : 'warning'}
      size={size}
      icon={known ? <DropRegular /> : <WarningRegular />}
      className={styles.group}
      aria-label={`Blood group ${groupLabel(group)}${note ? `, ${note}` : ''}`}
    >
      {groupLabel(group)}
    </Badge>
  );
}

export function UnitStatusBadge({ status }: { status: BloodUnitStatus }) {
  return <StatusBadge status={status} tone={unitStatusTone[status]} label={unitStatusLabel[status]} />;
}

/** Screening status of a unit or donation: pending, cleared (all five non-reactive) or reactive with the markers. */
export function TtiBadge({ tti }: { tti: TtiPanel }) {
  const state = ttiState(tti);
  if (state.reactive.length > 0) {
    return <StatusBadge status="Reactive" tone="danger" label={`Reactive: ${state.reactive.join(', ')}`} />;
  }
  if (state.cleared) {
    return <StatusBadge status="Cleared" tone="success" label="TTI cleared" />;
  }
  return <StatusBadge status="Pending" tone="warning" label={`TTI pending (${state.pending.length})`} />;
}

export function UrgencyBadge({ urgency }: { urgency: BloodRequestUrgency }) {
  return (
    <Badge appearance={urgency === 'Emergency' ? 'filled' : 'tint'} color={urgencyTone[urgency]} icon={urgency === 'Emergency' ? <WarningRegular /> : undefined}>
      {urgencyLabel[urgency]}
    </Badge>
  );
}

export function RequestStatusBadge({ status }: { status: BloodRequestStatus }) {
  return <StatusBadge status={status} tone={requestStatusTone[status]} label={requestStatusLabel[status]} />;
}

export function OutcomeBadge({ outcome }: { outcome: DonorEligibilityOutcome }) {
  return <StatusBadge status={outcome} tone={outcomeTone[outcome]} label={outcomeLabel[outcome]} />;
}

export function SeverityBadge({ severity }: { severity: ReactionSeverity }) {
  return <StatusBadge status={severity} tone={severityTone[severity]} label={severityLabel[severity]} />;
}

export function ReactionStatusBadge({ status }: { status: ReactionStatus }) {
  return <StatusBadge status={status} tone={reactionStatusTone[status]} label={reactionStatusLabel[status]} />;
}

/** Expiry with its urgency in words and colour ("Expired", "Expires in 5 h", "Expires in 2 days"). */
export function ExpiryText({ expiresAt, now }: { expiresAt: string; now: number }) {
  const styles = useStyles();
  const urgency = expiryUrgency(expiresAt, now);
  const hours = Math.floor((Date.parse(expiresAt) - now) / 3_600_000);
  const text =
    urgency === 'expired'
      ? 'Expired'
      : urgency === 'today'
        ? `Expires in ${Math.max(hours, 0)} h`
        : urgency === 'soon'
          ? `Expires in ${Math.ceil(hours / 24)} days`
          : formatDateTime(expiresAt);
  return (
    <Text className={mergeClasses(urgency === 'expired' && styles.expired, urgency === 'today' && styles.today, urgency === 'soon' && styles.soon)} title={formatDateTime(expiresAt)}>
      {text}
    </Text>
  );
}
