import { Badge, makeStyles, Text, tokens, Tooltip } from '@fluentui/react-components';
import { StatusBadge } from '@hc360/ui';
import type { OrderPriority, PatientClass } from './common/types';
import type { ContainerType, LabOrderItemStatus, LabReportStatus, QcOutcome, ResultFlag, SampleStatus } from './api';
import { flagDisplay } from './flags';
import {
  containerInfo,
  itemStatusLabel,
  patientClassLabel,
  priorityLabel,
  qcOutcomeLabel,
  reportStatusLabel,
  sampleStatusLabel,
} from './labels';

const useStyles = makeStyles({
  tube: { display: 'inline-flex', alignItems: 'center', gap: '6px', whiteSpace: 'nowrap' },
  cap: {
    width: '14px',
    height: '14px',
    borderRadius: '50%',
    border: `1px solid ${tokens.colorNeutralStroke1}`,
    flexShrink: 0,
  },
  flag: { minWidth: '30px', justifyContent: 'center', fontWeight: tokens.fontWeightBold },
});

/** STAT and urgent work stand out; routine work shows nothing unless asked (less noise in busy lists). */
export function PriorityBadge({ priority, showRoutine }: { priority: OrderPriority; showRoutine?: boolean }) {
  if (priority === 'Routine' && !showRoutine) {
    return null;
  }
  return (
    <Badge
      appearance={priority === 'Stat' ? 'filled' : 'tint'}
      color={priority === 'Stat' ? 'danger' : priority === 'Urgent' ? 'warning' : 'informative'}
      shape="rounded"
    >
      {priorityLabel[priority]}
    </Badge>
  );
}

/** Tube with its cap colour and name, as it looks in the rack (colour is never the only signal). */
export function TubeChip({ container, detail }: { container: ContainerType; detail?: string }) {
  const styles = useStyles();
  const info = containerInfo[container];
  return (
    <Tooltip content={`${info.label} (${info.capName})`} relationship="description">
      <span className={styles.tube}>
        <span className={styles.cap} style={{ backgroundColor: info.cap }} aria-hidden />
        <Text size={200} weight="semibold">
          {info.short}
        </Text>
        {detail && <Text size={200}>{detail}</Text>}
      </span>
    </Tooltip>
  );
}

const TONE_COLOR = {
  normal: 'success',
  low: 'warning',
  high: 'warning',
  abnormal: 'warning',
  critical: 'danger',
} as const;

/**
 * Result flag (H, L, HH, LL, A, C). A preview (not yet saved) is shown as an outline so staff can tell it apart from
 * the flag the server stored.
 */
export function FlagChip({ flag, preview }: { flag: ResultFlag | null | undefined; preview?: boolean }) {
  const styles = useStyles();
  const display = flagDisplay(flag);
  if (!display || !display.code) {
    return null;
  }
  return (
    <Tooltip content={`${display.label}${preview ? ' (preview: saved flag may differ)' : ''}`} relationship="label">
      <Badge
        className={styles.flag}
        appearance={preview ? 'outline' : display.critical ? 'filled' : 'tint'}
        color={TONE_COLOR[display.tone]}
        shape="rounded"
        size="medium"
      >
        {display.code}
      </Badge>
    </Tooltip>
  );
}

export function SampleStatusBadge({ status }: { status: SampleStatus }) {
  const tone =
    status === 'Pending'
      ? 'warning'
      : status === 'Rejected' || status === 'Cancelled'
        ? 'danger'
        : status === 'Completed'
          ? 'success'
          : 'brand';
  return <StatusBadge status={status} tone={tone} label={sampleStatusLabel[status]} />;
}

export function ItemStatusBadge({ status }: { status: LabOrderItemStatus }) {
  const tone =
    status === 'Released'
      ? 'success'
      : status === 'Cancelled'
        ? 'danger'
        : status === 'Ordered'
          ? 'warning'
          : status === 'Verified'
            ? 'success'
            : 'brand';
  return <StatusBadge status={status} tone={tone} label={itemStatusLabel[status]} />;
}

export function ReportStatusBadge({ status, partial }: { status: LabReportStatus; partial?: boolean }) {
  const tone =
    status === 'Draft'
      ? 'informative'
      : status === 'Verified'
        ? 'warning'
        : status === 'Amended'
          ? 'important'
          : 'success';
  return (
    <StatusBadge
      status={status}
      tone={tone}
      label={`${reportStatusLabel[status]}${partial && status !== 'Draft' ? ' (partial)' : ''}`}
    />
  );
}

export function QcOutcomeBadge({ outcome }: { outcome: QcOutcome | null }) {
  if (!outcome) {
    return <StatusBadge status="Pending" tone="subtle" label="Not run today" />;
  }
  const tone = outcome === 'Rejected' ? 'danger' : outcome === 'AcceptedWithWarning' ? 'warning' : 'success';
  return <StatusBadge status={outcome} tone={tone} label={qcOutcomeLabel[outcome]} />;
}

export function PatientClassBadge({ patientClass }: { patientClass: PatientClass }) {
  return (
    <Badge
      appearance="outline"
      shape="rounded"
      color={patientClass === 'Emergency' ? 'danger' : patientClass === 'InPatient' ? 'brand' : 'informative'}
    >
      {patientClassLabel[patientClass]}
    </Badge>
  );
}
