import { Badge, Tooltip } from '@fluentui/react-components';
import { AlertUrgentRegular } from '@fluentui/react-icons';
import { StatusBadge } from '@hc360/ui';
import { capaStatusLabel, incidentStatusLabel, severityHelp, severityLabel, type CapaStatus, type IncidentSeverity, type IncidentStatus, type RiskRating } from './labels';
import { capaTone, ratingTone, severityTone } from './rules';

export function SeverityBadge({ severity }: { severity: IncidentSeverity }) {
  return (
    <Tooltip content={severityHelp[severity]} relationship="description">
      <span>
        <StatusBadge status={severity} tone={severityTone(severity)} label={severityLabel[severity]} />
      </span>
    </Tooltip>
  );
}

export function SentinelBadge({ reason }: { reason?: string | null }) {
  return (
    <Tooltip content={reason ? `Sentinel event: ${reason}` : 'Sentinel event: root-cause analysis is mandatory'} relationship="description">
      <Badge color="danger" appearance="filled" icon={<AlertUrgentRegular />}>
        Sentinel
      </Badge>
    </Tooltip>
  );
}

export function IncidentStatusBadge({ status }: { status: IncidentStatus }) {
  const tone = status === 'Closed' ? 'success' : status === 'Reported' ? 'warning' : 'brand';
  return <StatusBadge status={status} tone={tone} label={incidentStatusLabel[status]} />;
}

export function CapaStatusBadge({ status, isOverdue }: { status: CapaStatus; isOverdue?: boolean }) {
  return <StatusBadge status={status} tone={capaTone(status, Boolean(isOverdue))} label={isOverdue ? `${capaStatusLabel[status]} · overdue` : capaStatusLabel[status]} />;
}

export function RatingBadge({ rating, score }: { rating: RiskRating; score?: number | null }) {
  return <StatusBadge status={rating} tone={ratingTone(rating)} label={score !== undefined && score !== null ? `${rating} · ${score}` : rating} />;
}
