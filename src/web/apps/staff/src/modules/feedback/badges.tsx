import { Badge, RatingDisplay, Tooltip } from '@fluentui/react-components';
import { StatusBadge } from '@hc360/ui';
import { complaintStatusLabel, type ComplaintPriority, type ComplaintStatus, type Sentiment } from './labels';
import { npsGroup, sentimentTone } from './rules';

export function SentimentBadge({ sentiment }: { sentiment: Sentiment }) {
  return <StatusBadge status={sentiment} tone={sentimentTone(sentiment)} />;
}

export function ComplaintStatusBadge({
  status,
  isOverdue,
  escalated,
}: {
  status: ComplaintStatus;
  isOverdue?: boolean;
  escalated?: boolean;
}) {
  const tone = isOverdue ? 'danger' : status === 'Open' ? 'warning' : status === 'InProgress' ? 'brand' : 'success';
  const late = escalated ? 'overdue, escalated' : 'overdue';
  return (
    <StatusBadge
      status={status}
      tone={tone}
      label={isOverdue ? `${complaintStatusLabel[status]} · ${late}` : complaintStatusLabel[status]}
    />
  );
}

export function PriorityBadge({ priority }: { priority: ComplaintPriority }) {
  return priority === 'High' ? (
    <Tooltip content="Resolve within 24 hours" relationship="description">
      <Badge color="danger" appearance="tint" shape="rounded">
        High
      </Badge>
    </Tooltip>
  ) : (
    <Badge color="informative" appearance="tint" shape="rounded">
      Normal
    </Badge>
  );
}

/** A 0–10 recommendation answer, coloured by promoter / passive / detractor. */
export function NpsAnswer({ value }: { value: number | null | undefined }) {
  if (value === null || value === undefined) {
    return <>—</>;
  }
  const group = npsGroup(value);
  const color = group === 'Promoter' ? 'success' : group === 'Passive' ? 'warning' : 'danger';
  return (
    <Tooltip content={`${group} (${value} of 10)`} relationship="description">
      <Badge color={color} appearance="filled" shape="circular">
        {value}
      </Badge>
    </Tooltip>
  );
}

/** Average stars, compact (one star and the number). */
export function StarsValue({ value }: { value: number | null | undefined }) {
  if (value === null || value === undefined) {
    return <>—</>;
  }
  return (
    <RatingDisplay
      value={Math.round(value * 10) / 10}
      compact
      color="marigold"
      size="small"
      aria-label={`${value.toFixed(1)} out of 5 stars`}
    />
  );
}
