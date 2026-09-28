import { Badge, Caption1, makeStyles, Text, tokens, Tooltip } from '@fluentui/react-components';
import { ShieldKeyholeRegular, WeatherSnowflakeRegular } from '@fluentui/react-icons';
import { formatDateOnly } from '@hc360/core';
import { StatusBadge } from '@hc360/ui';
import type { ReactNode } from 'react';
import { expiryLabel, expiryTone, formatExpiry } from './format';
import { requiresPrescription, requiresRegister, scheduleHint, scheduleLabel, workflowTone, type DrugSchedule } from './labels';

const useStyles = makeStyles({
  flags: { display: 'inline-flex', gap: '4px', alignItems: 'center', flexWrap: 'wrap' },
  expiry: { display: 'inline-flex', flexDirection: 'column', lineHeight: 1.2 },
  details: { display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(200px, 1fr))', gap: '12px 24px' },
  item: { display: 'flex', flexDirection: 'column', gap: '2px', minWidth: 0 },
  label: { color: tokens.colorNeutralForeground3 },
  danger: { color: tokens.colorPaletteRedForeground1 },
  warning: { color: tokens.colorPaletteMarigoldForeground1 },
});

/** Schedule H / H1 / X and NDPS markers with their meaning on hover; nothing for unscheduled items. */
export function ScheduleBadge({ schedule, isControlled }: { schedule: DrugSchedule | null | undefined; isControlled?: boolean }) {
  if (!schedule || schedule === 'None' || schedule === 'Otc' || schedule === 'G') {
    return isControlled ? (
      <Tooltip content="Narcotic / psychotropic (NDPS): prescription and register entry needed" relationship="description">
        <Badge size="small" appearance="filled" color="danger" icon={<ShieldKeyholeRegular />}>
          NDPS
        </Badge>
      </Tooltip>
    ) : null;
  }
  const register = requiresRegister(schedule, Boolean(isControlled));
  return (
    <Tooltip content={isControlled ? `${scheduleHint[schedule]} (NDPS controlled)` : scheduleHint[schedule]} relationship="description">
      <Badge size="small" appearance={register ? 'filled' : 'tint'} color={register ? 'danger' : 'warning'} icon={register ? <ShieldKeyholeRegular /> : undefined}>
        {schedule === 'H' ? 'Rx · H' : scheduleLabel[schedule].replace('Schedule ', '')}
        {isControlled && schedule !== 'X' ? ' · NDPS' : ''}
      </Badge>
    </Tooltip>
  );
}

/** Prescription, register and cold-chain markers for an item. */
export function ItemFlags({ schedule, isControlled, isColdChain }: { schedule: DrugSchedule | null | undefined; isControlled: boolean; isColdChain?: boolean }) {
  const styles = useStyles();
  const rxOnly = schedule ? requiresPrescription(schedule, isControlled) : isControlled;
  if (!rxOnly && !isColdChain && !isControlled) {
    return null;
  }
  return (
    <span className={styles.flags}>
      <ScheduleBadge schedule={schedule} isControlled={isControlled} />
      {isColdChain && (
        <Tooltip content="Cold chain: keep at 2–8 °C" relationship="label">
          <Badge size="small" appearance="tint" color="informative" icon={<WeatherSnowflakeRegular />}>
            2–8 °C
          </Badge>
        </Tooltip>
      )}
    </span>
  );
}

/** Pack-style expiry ("Mar 2027") coloured by how soon it expires, with the exact date and days on hover. */
export function ExpiryText({ date, daysToExpiry, warnDays = 90, showDays }: { date: string | null | undefined; daysToExpiry?: number | null; warnDays?: number; showDays?: boolean }) {
  const styles = useStyles();
  if (!date) {
    return <Text>—</Text>;
  }
  const tone = daysToExpiry === undefined ? undefined : expiryTone(daysToExpiry, warnDays);
  const className = tone === 'danger' ? styles.danger : tone === 'warning' ? styles.warning : undefined;
  return (
    <Tooltip content={`${formatDateOnly(date)}${daysToExpiry !== undefined ? ` · ${expiryLabel(daysToExpiry)}` : ''}`} relationship="description">
      <span className={styles.expiry}>
        <Text className={className} weight={tone === 'danger' || tone === 'warning' ? 'semibold' : 'regular'}>
          {formatExpiry(date)}
        </Text>
        {showDays && daysToExpiry !== undefined && daysToExpiry !== null && <Caption1 className={className ?? styles.label}>{expiryLabel(daysToExpiry)}</Caption1>}
      </span>
    </Tooltip>
  );
}

/** Status pill for stock documents (knows states such as "In transit" and "Pending approval"). */
export function DocStatus({ status, label }: { status: string; label?: string }) {
  return <StatusBadge status={status} tone={workflowTone(status)} label={label} />;
}

/** Label/value grid used on detail panes. */
export function DetailGrid({ children }: { children: ReactNode }) {
  const styles = useStyles();
  return <div className={styles.details}>{children}</div>;
}

export function Detail({ label, children }: { label: string; children: ReactNode }) {
  const styles = useStyles();
  const empty = children === null || children === undefined || children === '' || children === false;
  return (
    <div className={styles.item}>
      <Caption1 className={styles.label}>{label}</Caption1>
      <Text>{empty ? '—' : children}</Text>
    </div>
  );
}
