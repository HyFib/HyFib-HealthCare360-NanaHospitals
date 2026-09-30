import { Badge, Caption1, makeStyles, mergeClasses, Radio, RadioGroup, Text, tokens } from '@fluentui/react-components';
import {
  BroomRegular,
  CheckmarkCircleRegular,
  ProhibitedRegular,
  ShieldRegular,
  WarningRegular,
} from '@fluentui/react-icons';
import { formatRelative } from '@hc360/core';
import { StatusBadge } from '@hc360/ui';
import type { DialysisMachineStatus, DialysisSessionStatus, SeroStatus } from './api';
import {
  machineStatusLabel,
  machineStatusTone,
  seroLabel,
  seroShort,
  seroTone,
  sessionStatusLabel,
  sessionStatusTone,
} from './labels';
import type { MachineChoice } from './logic';

const useStyles = makeStyles({
  disinfection: { display: 'inline-flex', alignItems: 'center', gap: '4px' },
  dirty: { color: tokens.colorPaletteDarkOrangeForeground1, fontWeight: tokens.fontWeightSemibold },
  clean: { color: tokens.colorPaletteGreenForeground1 },
  picker: { display: 'flex', flexDirection: 'column', gap: '6px' },
  option: {
    display: 'grid',
    gridTemplateColumns: 'minmax(0, 1fr)',
    padding: '8px 10px',
    borderRadius: tokens.borderRadiusMedium,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  optionBlocked: { backgroundColor: tokens.colorNeutralBackground3 },
  reasons: { display: 'flex', flexDirection: 'column', gap: '2px', paddingLeft: '28px' },
  blocker: { color: tokens.colorPaletteRedForeground1, display: 'inline-flex', gap: '4px', alignItems: 'center' },
  warning: {
    color: tokens.colorPaletteDarkOrangeForeground1,
    display: 'inline-flex',
    gap: '4px',
    alignItems: 'center',
  },
  label: { display: 'inline-flex', gap: '8px', alignItems: 'center', flexWrap: 'wrap' },
});

/** Sero category with its own colour, icon and words (colour is never the only signal). */
export function SeroBadge({
  sero,
  size = 'medium',
  long,
}: {
  sero: SeroStatus;
  size?: 'small' | 'medium' | 'large';
  long?: boolean;
}) {
  return (
    <Badge
      appearance={sero === 'Negative' ? 'tint' : 'filled'}
      color={seroTone[sero]}
      size={size}
      icon={<ShieldRegular />}
      aria-label={`Sero status: ${seroLabel[sero]}`}
    >
      {long ? seroLabel[sero] : seroShort[sero]}
    </Badge>
  );
}

export function SessionStatusBadge({ status }: { status: DialysisSessionStatus }) {
  return <StatusBadge status={status} tone={sessionStatusTone[status]} label={sessionStatusLabel[status]} />;
}

export function MachineStatusBadge({ status }: { status: DialysisMachineStatus }) {
  return <StatusBadge status={status} tone={machineStatusTone[status]} label={machineStatusLabel[status]} />;
}

/** Disinfection state after the last session: must be recorded before the next patient is connected. */
export function DisinfectionState({
  needsDisinfection,
  lastDisinfectedAt,
}: {
  needsDisinfection: boolean;
  lastDisinfectedAt?: string | null;
}) {
  const styles = useStyles();
  return needsDisinfection ? (
    <span className={mergeClasses(styles.disinfection, styles.dirty)}>
      <BroomRegular aria-hidden /> Needs disinfection
    </span>
  ) : (
    <span className={mergeClasses(styles.disinfection, styles.clean)}>
      <CheckmarkCircleRegular aria-hidden /> Disinfected
      {lastDisinfectedAt ? ` ${formatRelative(lastDisinfectedAt)}` : ''}
    </span>
  );
}

/**
 * Machine choice with sero segregation made visible: every machine is listed, compatible ones can be chosen, and
 * the others are disabled with the exact reason.
 */
export function MachinePicker({
  choices,
  value,
  onChange,
  label = 'Machine',
}: {
  choices: MachineChoice[];
  value: string | null;
  onChange: (machineId: string) => void;
  label?: string;
}) {
  const styles = useStyles();
  const sorted = [...choices].sort(
    (a, b) => Number(b.check.eligible) - Number(a.check.eligible) || a.code.localeCompare(b.code),
  );
  return (
    <RadioGroup aria-label={label} value={value ?? ''} onChange={(_, d) => onChange(d.value)} className={styles.picker}>
      {sorted.map((choice) => (
        <div key={choice.id} className={mergeClasses(styles.option, !choice.check.eligible && styles.optionBlocked)}>
          <Radio
            value={choice.id}
            disabled={!choice.check.eligible}
            label={
              <span className={styles.label}>
                <Text weight="semibold">{choice.code}</Text>
                {choice.station && <Caption1>{choice.station}</Caption1>}
                <SeroBadge sero={choice.seroDedication} size="small" />
                <MachineStatusBadge status={choice.status} />
              </span>
            }
          />
          {(choice.check.blockers.length > 0 || choice.check.warnings.length > 0) && (
            <div className={styles.reasons}>
              {choice.check.blockers.map((reason) => (
                <Caption1 key={reason} className={styles.blocker}>
                  <ProhibitedRegular aria-hidden /> {reason}
                </Caption1>
              ))}
              {choice.check.warnings.map((reason) => (
                <Caption1 key={reason} className={styles.warning}>
                  <WarningRegular aria-hidden /> {reason}
                </Caption1>
              ))}
            </div>
          )}
        </div>
      ))}
    </RadioGroup>
  );
}
