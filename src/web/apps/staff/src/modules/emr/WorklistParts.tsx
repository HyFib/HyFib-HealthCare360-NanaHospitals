import { Badge, Caption1, makeStyles, Text, Tooltip } from '@fluentui/react-components';
import { AlertUrgentRegular, LockClosedRegular, WarningRegular } from '@fluentui/react-icons';
import type { Gender } from '@hc360/core';
import { Children, type ReactNode } from 'react';

const useStyles = makeStyles({
  // The name, then age, sex and UHID, then the badges: on one line where the cell is wide enough, each going onto a
  // line of its own as it narrows. The name and the UHID never wrap within themselves; narrower than the cell, they end
  // in an ellipsis (the whole of them in the title) rather than spill out of it.
  root: { display: 'flex', flexWrap: 'wrap', alignItems: 'center', columnGap: '6px', rowGap: '2px' },
  line: { minWidth: 0 },
  badges: { display: 'flex', flexWrap: 'wrap', gap: '4px', minWidth: 0 },
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
  const details = `(${age ?? '—'} / ${gender.charAt(0)}) · ${uhid}`;
  const hasBadges = hasActiveAllergies || hasCriticalAlerts || isRestricted || Children.toArray(children).length > 0;
  return (
    <span className={styles.root}>
      <Text weight="semibold" wrap={false} truncate title={name} className={styles.line}>
        {name}
      </Text>
      <Caption1 wrap={false} truncate title={details} className={styles.line}>
        {details}
      </Caption1>
      {/* The tooltips describe the badges: a badge says "Allergy" itself, and a plain element cannot be given a name. */}
      {hasBadges && (
        <span className={styles.badges}>
          {hasActiveAllergies && (
            <Tooltip content="Has active allergies" relationship="description">
              <Badge size="small" color="danger" icon={<WarningRegular />}>
                Allergy
              </Badge>
            </Tooltip>
          )}
          {hasCriticalAlerts && (
            <Tooltip content="Critical alert on the banner (DNR, isolation…)" relationship="description">
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
      )}
    </span>
  );
}
