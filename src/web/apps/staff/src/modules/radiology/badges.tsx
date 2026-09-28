import { Badge, Tooltip } from '@fluentui/react-components';
import { AlertUrgentRegular, BotRegular } from '@fluentui/react-icons';
import { StatusBadge } from '@hc360/ui';
export { DiagnosticReportBadge } from '@/modules/lab/common/DiagnosticReportBadge';
import type { ImagingStudyStatus, Modality } from './api';
import { modalityLabel, studyStatusLabel } from './labels';

export function StudyStatusBadge({ status }: { status: ImagingStudyStatus }) {
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
  return <StatusBadge status={status} tone={tone} label={studyStatusLabel[status]} />;
}

export function ModalityBadge({ modality }: { modality: Modality }) {
  return (
    <Tooltip content={modalityLabel[modality]} relationship="label">
      <Badge appearance="outline" shape="rounded">
        {modality}
      </Badge>
    </Tooltip>
  );
}

/** Flags that change the order of work: urgent AI triage and critical findings. */
export function StudyFlags({ urgentAi, critical }: { urgentAi?: boolean; critical?: boolean }) {
  return (
    <>
      {urgentAi && (
        <Badge color="danger" appearance="tint" icon={<BotRegular />} shape="rounded">
          AI: urgent
        </Badge>
      )}
      {critical && (
        <Badge color="danger" icon={<AlertUrgentRegular />} shape="rounded">
          Critical
        </Badge>
      )}
    </>
  );
}
