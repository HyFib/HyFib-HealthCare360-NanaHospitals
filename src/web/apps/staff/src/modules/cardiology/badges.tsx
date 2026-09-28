import { Badge } from '@fluentui/react-components';
import { StatusBadge } from '@hc360/ui';
import type { CardiologyStudyStatus, CardiologyStudyType } from './api';
import { cardiologyStatusLabel, studyTypeLabel } from './labels';

export function CardiologyStatusBadge({ status }: { status: CardiologyStudyStatus }) {
  const tone =
    status === 'Released'
      ? 'success'
      : status === 'Cancelled'
        ? 'danger'
        : status === 'Ordered' || status === 'Scheduled'
          ? 'warning'
          : status === 'Acquired' || status === 'Reported' || status === 'Verified'
            ? 'important'
            : 'brand';
  return <StatusBadge status={status} tone={tone} label={cardiologyStatusLabel[status]} />;
}

export function StudyTypeBadge({ type }: { type: CardiologyStudyType }) {
  return (
    <Badge appearance="outline" shape="rounded">
      {studyTypeLabel[type]}
    </Badge>
  );
}
