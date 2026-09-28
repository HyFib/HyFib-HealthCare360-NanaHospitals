import { Badge, Button, Caption1, Link, makeStyles, Text, tokens, Tooltip } from '@fluentui/react-components';
import { AlertUrgentRegular, HeartPulseRegular, PillRegular, TaskListLtrRegular, WarningRegular } from '@fluentui/react-icons';
import { formatDateOnly, formatDateTime, formatRelative } from '@hc360/core';
import { Section, StatusBadge } from '@hc360/ui';
import { encounterTypeLabel, type PatientClinicalSummary, type VitalsSnapshot } from './api';
import type { ChartTab } from './timeline';
import { vitalChips, type VitalChip } from './vitals';

const useStyles = makeStyles({
  grid: {
    display: 'grid',
    gap: '16px',
    gridTemplateColumns: 'repeat(auto-fill, minmax(min(100%, 300px), 1fr))',
    alignItems: 'start',
  },
  list: { display: 'flex', flexDirection: 'column', gap: '8px', margin: 0, padding: 0, listStyle: 'none' },
  item: { display: 'flex', flexDirection: 'column', gap: '2px', minWidth: 0 },
  itemRow: { display: 'flex', gap: '6px', alignItems: 'baseline', flexWrap: 'wrap' },
  code: { fontFamily: tokens.fontFamilyMonospace, color: tokens.colorBrandForeground1 },
  muted: { color: tokens.colorNeutralForeground3 },
  chips: { display: 'flex', flexWrap: 'wrap', gap: '6px' },
  chip: {
    display: 'inline-flex',
    alignItems: 'baseline',
    gap: '4px',
    padding: '4px 8px',
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground3,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  chipWarning: { backgroundColor: tokens.colorPaletteMarigoldBackground1, border: `1px solid ${tokens.colorPaletteMarigoldBorder2}` },
  chipDanger: { backgroundColor: tokens.colorPaletteRedBackground1, border: `1px solid ${tokens.colorPaletteRedBorder2}` },
  chipLabel: { color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200 },
  chipValue: { fontWeight: tokens.fontWeightSemibold },
  chipFlag: { fontSize: tokens.fontSizeBase200 },
  empty: { color: tokens.colorNeutralForeground3 },
  alert: { display: 'flex', gap: '8px', alignItems: 'flex-start' },
  critical: { color: tokens.colorPaletteRedForeground1, flexShrink: 0, fontSize: '18px', display: 'flex' },
  warning: { color: tokens.colorPaletteMarigoldForeground1, flexShrink: 0, fontSize: '18px', display: 'flex' },
  encounter: {
    display: 'flex',
    flexDirection: 'column',
    gap: '2px',
    padding: '6px 8px',
    borderRadius: tokens.borderRadiusMedium,
    cursor: 'pointer',
    ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover },
  },
});

/** Vital values as chips; out-of-range values are coloured and carry the flag text (colour is never the only signal). */
export function VitalChipList({ chips }: { chips: VitalChip[] }) {
  const styles = useStyles();
  return (
    <div className={styles.chips}>
      {chips.map((chip) => {
        const tone = chip.flag ? (chip.flag.severity === 'Critical' ? styles.chipDanger : styles.chipWarning) : undefined;
        const content = (
          <span className={[styles.chip, tone].filter(Boolean).join(' ')} key={chip.label}>
            <span className={styles.chipLabel}>{chip.label}</span>
            <span className={styles.chipValue}>{chip.value}</span>
            {chip.flag && <span className={styles.chipFlag}>{chip.flag.severity === 'Critical' ? '‼' : chip.flag.direction === 'High' ? '↑' : '↓'}</span>}
          </span>
        );
        return chip.flag ? (
          <Tooltip key={chip.label} content={chip.flag.message} relationship="description">
            {content}
          </Tooltip>
        ) : (
          content
        );
      })}
    </div>
  );
}

export function LatestVitals({ vitals, onRecord }: { vitals: VitalsSnapshot | null; onRecord?: () => void }) {
  const styles = useStyles();
  if (!vitals) {
    return (
      <Text className={styles.empty}>
        No vitals recorded yet.{' '}
        {onRecord && (
          <Link as="button" onClick={onRecord}>
            Record vitals
          </Link>
        )}
      </Text>
    );
  }
  const flags = vitals.flags.filter((f) => f.severity === 'Critical');
  return (
    <div className={styles.item}>
      <VitalChipList chips={vitalChips(vitals)} />
      <Caption1 className={styles.muted}>
        Recorded {formatRelative(vitals.recordedAt)} ({formatDateTime(vitals.recordedAt)})
      </Caption1>
      {flags.length > 0 && (
        <Text weight="semibold" style={{ color: tokens.colorPaletteRedForeground1 }}>
          {flags.map((f) => f.message).join('; ')}
        </Text>
      )}
    </div>
  );
}

/** Problems, current medicines, latest vitals and alerts at a glance (EHR-03). */
export function ChartSummary({
  summary,
  onOpenTab,
  onOpenEncounter,
  onRecordVitals,
}: {
  summary: PatientClinicalSummary;
  onOpenTab: (tab: ChartTab) => void;
  onOpenEncounter: (encounterId: string) => void;
  onRecordVitals?: () => void;
}) {
  const styles = useStyles();
  const medicines = summary.currentMedications;

  return (
    <div className={styles.grid}>
      <Section
        title="Alerts and allergies"
        actions={
          <Button size="small" appearance="subtle" onClick={() => onOpenTab('allergies')}>
            Manage
          </Button>
        }
      >
        <ul className={styles.list}>
          {summary.alerts.map((alert) => (
            <li key={alert.id} className={styles.alert}>
              <span className={alert.severity === 'Critical' ? styles.critical : styles.warning} aria-hidden>
                <AlertUrgentRegular />
              </span>
              <span className={styles.item}>
                <Text weight="semibold">{alert.title}</Text>
                {alert.detail && <Caption1>{alert.detail}</Caption1>}
                {alert.expiresOn && <Caption1 className={styles.muted}>Until {formatDateOnly(alert.expiresOn)}</Caption1>}
              </span>
            </li>
          ))}
          {summary.allergies.map((allergy) => (
            <li key={allergy.id} className={styles.alert}>
              <span className={styles.critical} aria-hidden>
                <WarningRegular />
              </span>
              <span className={styles.item}>
                <Text weight="semibold">
                  {allergy.allergen} <Caption1 className={styles.muted}>({allergy.type.toLowerCase()} allergy)</Caption1>
                </Text>
                <Caption1>
                  {allergy.severity}
                  {allergy.reaction ? ` · ${allergy.reaction}` : ''}
                </Caption1>
              </span>
            </li>
          ))}
          {summary.alerts.length === 0 && summary.allergies.length === 0 && (
            <li>
              <Text className={styles.empty}>{summary.allergiesEverRecorded ? 'No active alerts. No known allergies.' : 'No active alerts. Allergies have not been recorded yet.'}</Text>
            </li>
          )}
        </ul>
      </Section>

      <Section
        title="Active problems"
        actions={
          <Button size="small" appearance="subtle" icon={<TaskListLtrRegular />} onClick={() => onOpenTab('diagnoses')}>
            All diagnoses
          </Button>
        }
      >
        {summary.activeProblems.length === 0 ? (
          <Text className={styles.empty}>No active problems recorded.</Text>
        ) : (
          <ul className={styles.list}>
            {summary.activeProblems.map((problem) => (
              <li key={problem.id} className={styles.item}>
                <span className={styles.itemRow}>
                  {problem.icdCode && <span className={styles.code}>{problem.icdCode}</span>}
                  <Text weight="semibold">{problem.title}</Text>
                </span>
                <Caption1 className={styles.muted}>
                  {problem.status === 'Chronic' ? 'Chronic' : problem.type}
                  {problem.onsetDate ? ` · since ${formatDateOnly(problem.onsetDate)}` : ''}
                </Caption1>
              </li>
            ))}
          </ul>
        )}
      </Section>

      <Section
        title={`Current medicines (${medicines.length})`}
        actions={
          <Button size="small" appearance="subtle" icon={<PillRegular />} onClick={() => onOpenTab('prescriptions')}>
            Prescriptions
          </Button>
        }
      >
        {medicines.length === 0 ? (
          <Text className={styles.empty}>No current medicines prescribed here.</Text>
        ) : (
          <ul className={styles.list}>
            {medicines.slice(0, 8).map((m) => (
              <li key={m.itemId} className={styles.item}>
                <span className={styles.itemRow}>
                  <Text weight="semibold">
                    {m.drugName}
                    {m.strength ? ` ${m.strength}` : ''}
                  </Text>
                  {m.isControlledDrug && (
                    <Badge size="small" color="danger" appearance="outline">
                      H1/X
                    </Badge>
                  )}
                </span>
                <Caption1>
                  {m.dose} · {m.frequency} · until {formatDateOnly(m.endsOn)}
                </Caption1>
                <Caption1 className={styles.muted}>
                  {m.prescriberName ?? 'Doctor'} · {m.prescriptionNumber}
                </Caption1>
              </li>
            ))}
            {medicines.length > 8 && <Caption1 className={styles.muted}>and {medicines.length - 8} more</Caption1>}
          </ul>
        )}
      </Section>

      <Section
        title="Latest vitals"
        actions={
          <Button size="small" appearance="subtle" icon={<HeartPulseRegular />} onClick={() => onOpenTab('vitals')}>
            Trends
          </Button>
        }
      >
        <LatestVitals vitals={summary.lastVitals} onRecord={onRecordVitals} />
      </Section>

      <Section title="Recent encounters">
        {summary.recentEncounters.length === 0 ? (
          <Text className={styles.empty}>No encounters yet.</Text>
        ) : (
          <ul className={styles.list}>
            {summary.recentEncounters.map((e) => (
              <li key={e.id}>
                <div
                  className={styles.encounter}
                  role="button"
                  tabIndex={0}
                  onClick={() => onOpenEncounter(e.id)}
                  onKeyDown={(event) => {
                    if (event.key === 'Enter' || event.key === ' ') {
                      event.preventDefault();
                      onOpenEncounter(e.id);
                    }
                  }}
                >
                  <span className={styles.itemRow}>
                    <Text weight="semibold">{formatDateTime(e.startedAt)}</Text>
                    <StatusBadge status={e.status === 'InProgress' ? 'Draft' : e.status} label={e.status === 'InProgress' ? 'Unsigned' : undefined} />
                  </span>
                  <Caption1>
                    {encounterTypeLabel[e.type]}
                    {e.departmentName ? ` · ${e.departmentName}` : ''}
                    {e.practitionerName ? ` · ${e.practitionerName}` : ''}
                  </Caption1>
                  {(e.diagnoses.length > 0 || e.chiefComplaint) && <Caption1 className={styles.muted}>{e.diagnoses.length > 0 ? e.diagnoses.join('; ') : e.chiefComplaint}</Caption1>}
                </div>
              </li>
            ))}
          </ul>
        )}
      </Section>
    </div>
  );
}
