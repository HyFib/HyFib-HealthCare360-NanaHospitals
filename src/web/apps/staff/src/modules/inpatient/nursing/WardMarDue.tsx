import {
  Badge,
  Button,
  Caption1,
  makeStyles,
  mergeClasses,
  Select,
  Skeleton,
  SkeletonItem,
  Text,
  tokens,
} from '@fluentui/react-components';
import { CheckmarkRegular, PillRegular } from '@fluentui/react-icons';
import { formatTime } from '@hc360/core';
import { EmptyState, ErrorState, Section } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { nursingQueries, type MarOutcome, type WardMarItem } from '../nursingApi';
import { formatDuration } from '../time';
import { RecordDoseDialog, type DoseToRecord } from './RecordDoseDialog';

const useStyles = makeStyles({
  groups: { display: 'flex', flexDirection: 'column', gap: '12px' },
  group: { display: 'flex', flexDirection: 'column', gap: '6px' },
  groupHeader: { display: 'flex', alignItems: 'baseline', gap: '8px', flexWrap: 'wrap' },
  row: {
    display: 'grid',
    gridTemplateColumns: '84px minmax(0, 1fr) auto',
    alignItems: 'center',
    gap: '8px 12px',
    padding: '8px 10px',
    borderRadius: tokens.borderRadiusMedium,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    backgroundColor: tokens.colorNeutralBackground1,
    '@media (max-width: 560px)': { gridTemplateColumns: 'minmax(0, 1fr)' },
  },
  overdue: { borderLeft: `5px solid ${tokens.colorPaletteRedBorderActive}` },
  due: { borderLeft: `5px solid ${tokens.colorPaletteMarigoldBorderActive}` },
  time: { fontWeight: tokens.fontWeightSemibold, fontFamily: tokens.fontFamilyNumeric },
  actions: { display: 'flex', gap: '6px', flexWrap: 'wrap' },
  big: { minHeight: '40px', minWidth: '96px' },
  muted: { color: tokens.colorNeutralForeground3 },
});

interface PatientGroup {
  admissionId: string;
  patientName: string;
  uhid: string;
  bedLabel: string | null;
  items: WardMarItem[];
}

function groupByPatient(items: readonly WardMarItem[]): PatientGroup[] {
  const map = new Map<string, PatientGroup>();
  for (const item of items) {
    const group = map.get(item.admissionId) ?? {
      admissionId: item.admissionId,
      patientName: item.patientName,
      uhid: item.uhid,
      bedLabel: item.bedLabel,
      items: [],
    };
    group.items.push(item);
    map.set(item.admissionId, group);
  }
  // Beds in label order so the round follows the ward.
  return [...map.values()].sort((a, b) => (a.bedLabel ?? '').localeCompare(b.bedLabel ?? '', 'en', { numeric: true }));
}

/** The ward's medication round: overdue and due doses, grouped bed by bed. Two taps record a dose. */
export function WardMarDue({
  wardId,
  onOpenPatient,
}: {
  wardId: string;
  onOpenPatient: (admissionId: string) => void;
}) {
  const styles = useStyles();
  const canRecord = usePermission('nursing.mar');
  const [withinMinutes, setWithinMinutes] = useState(60);
  const due = useQuery({ ...nursingQueries.wardMarDue(wardId, withinMinutes), enabled: canRecord });
  const [recording, setRecording] = useState<{ dose: DoseToRecord; outcome: MarOutcome } | null>(null);

  if (!canRecord) {
    return null;
  }

  const items = due.data ?? [];
  const overdue = items.filter((i) => i.isOverdue).length;
  const groups = groupByPatient(items);

  const open = (item: WardMarItem, outcome: MarOutcome) =>
    setRecording({
      outcome,
      dose: {
        id: item.entryId,
        drugName: item.drugName,
        dose: item.dose,
        route: item.route,
        scheduledAt: item.scheduledAt,
        version: item.version,
        admissionId: item.admissionId,
        patientName: item.patientName,
        bedLabel: item.bedLabel,
        isOverdue: item.isOverdue,
      },
    });

  return (
    <Section
      title="Medication round"
      description={
        due.data ? `${items.length} doses to give${overdue ? `, ${overdue} overdue` : ''}` : 'Doses due on this ward'
      }
      actions={
        <Select
          value={String(withinMinutes)}
          onChange={(_, d) => setWithinMinutes(Number(d.value))}
          aria-label="Show doses due within"
        >
          <option value="30">Due in 30 min</option>
          <option value="60">Due in 1 hour</option>
          <option value="120">Due in 2 hours</option>
          <option value="240">Due in 4 hours</option>
        </Select>
      }
    >
      {due.error ? (
        <ErrorState error={due.error} onRetry={() => void due.refetch()} />
      ) : !due.data ? (
        <Skeleton aria-label="Loading the medication round">
          {Array.from({ length: 4 }, (_, i) => (
            <SkeletonItem key={i} style={{ height: 52, marginBottom: 8 }} />
          ))}
        </Skeleton>
      ) : groups.length === 0 ? (
        <EmptyState
          icon={<PillRegular />}
          title="No doses due"
          description="Doses appear here as their time approaches, and stay until they are recorded."
        />
      ) : (
        <div className={styles.groups}>
          {groups.map((group) => (
            <div
              key={group.admissionId}
              className={styles.group}
              role="group"
              aria-label={`${group.patientName}, bed ${group.bedLabel ?? ''}`}
            >
              <div className={styles.groupHeader}>
                <Text weight="semibold">{group.bedLabel ?? '—'}</Text>
                <Button appearance="transparent" size="small" onClick={() => onOpenPatient(group.admissionId)}>
                  {group.patientName}
                </Button>
                <Caption1 className={styles.muted}>{group.uhid}</Caption1>
              </div>
              {group.items.map((item) => (
                <div
                  key={item.entryId}
                  className={mergeClasses(styles.row, item.isOverdue ? styles.overdue : styles.due)}
                >
                  <span>
                    <Text className={styles.time}>{formatTime(item.scheduledAt)}</Text>
                    {item.isOverdue && (
                      <Badge
                        size="small"
                        appearance="tint"
                        color="danger"
                        style={{ display: 'block', marginTop: 2, width: 'fit-content' }}
                      >
                        {formatDuration(item.minutesLate)} late
                      </Badge>
                    )}
                  </span>
                  <span>
                    <Text weight="semibold">{item.drugName}</Text>
                    <Caption1 block>
                      {item.dose}
                      {item.route ? ` · ${item.route}` : ''}
                    </Caption1>
                  </span>
                  <span className={styles.actions}>
                    <Button
                      className={styles.big}
                      appearance="primary"
                      icon={<CheckmarkRegular />}
                      onClick={() => open(item, 'Given')}
                    >
                      Given
                    </Button>
                    <Button className={styles.big} onClick={() => open(item, 'Held')}>
                      Not given
                    </Button>
                  </span>
                </div>
              ))}
            </div>
          ))}
        </div>
      )}
      {recording && (
        <RecordDoseDialog dose={recording.dose} initialOutcome={recording.outcome} onClose={() => setRecording(null)} />
      )}
    </Section>
  );
}
