import { Badge, Caption1, makeStyles, Text, Tooltip } from '@fluentui/react-components';
import { AlertUrgentRegular, LockClosedRegular, WarningRegular } from '@fluentui/react-icons';
import type { Gender } from '@hc360/core';
import type { ReactNode } from 'react';

const useStyles = makeStyles({
  badges: { display: 'inline-flex', gap: '4px', marginLeft: '6px', verticalAlign: 'middle', flexWrap: 'wrap' },
});

/**
 * A patient in one of the worklist's lists: name, age and sex, UHID, and the banner's safety flags, so a risky
 * patient stands out before the chart is opened. Extra badges (priority, follow-up, MLC…) go in `children`.
 * A restricted (VIP) record the user may not open arrives as "Restricted patient", and is badged as such.
 */
export function WorklistPatient({
  name,
  age,
  gender,
  uhid,
  hasActiveAllergies,
  hasCriticalAlerts,
  isRestricted = false,
  children,
}: {
  name: string;
  age: string | null;
  gender: Gender;
  uhid: string;
  hasActiveAllergies: boolean;
  hasCriticalAlerts: boolean;
  isRestricted?: boolean;
  children?: ReactNode;
}) {
  const styles = useStyles();
  return (
    <span>
      <Text weight="semibold">{name}</Text>{' '}
      <Caption1>
        ({age ?? '—'} / {gender.charAt(0)}) · {uhid}
      </Caption1>
      <span className={styles.badges}>
        {hasActiveAllergies && (
          <Tooltip content="Has active allergies" relationship="label">
            <Badge size="small" color="danger" icon={<WarningRegular />}>
              Allergy
            </Badge>
          </Tooltip>
        )}
        {hasCriticalAlerts && (
          <Tooltip content="Critical alert on the banner (DNR, isolation…)" relationship="label">
            <Badge size="small" color="important" icon={<AlertUrgentRegular />}>
              Alert
            </Badge>
          </Tooltip>
        )}
        {isRestricted && (
          <Tooltip
            content="Restricted record: the name and details are hidden. Opening the chart asks for emergency access."
            relationship="description"
          >
            <Badge size="small" appearance="tint" color="subtle" icon={<LockClosedRegular />}>
              Restricted
            </Badge>
          </Tooltip>
        )}
        {children}
      </span>
    </span>
  );
}
