import { Avatar, Badge, Caption1, makeStyles, Text, tokens, Tooltip } from '@fluentui/react-components';
import {
  AlertUrgentRegular,
  CallRegular,
  DropRegular,
  InfoRegular,
  ShieldKeyholeRegular,
  WarningRegular,
} from '@fluentui/react-icons';
import { bloodGroupLabel, formatPhone, genderShort, type BloodGroup, type Gender } from '@hc360/core';
import type { ReactElement, ReactNode } from 'react';

/**
 * What is known about allergies: some recorded, a clinician confirmed "no known allergies", or nothing recorded yet.
 * The banner claims "no known allergies" only when that was recorded, never from an empty list. `checking` (still
 * loading) and `unavailable` (restricted record, no connection) say so, so a missing flag never reads as "no allergies".
 */
export type AllergyStatus = 'recorded' | 'none' | 'unknown' | 'checking' | 'unavailable';

/** How loudly an alert is shown: danger (DNR, isolation, infection control), warning, or information. */
export type PatientBannerAlertTone = 'danger' | 'warning' | 'informative';

/** An alert with its weight; a plain string is shown as before (a dark "important" badge). */
export interface PatientBannerAlert {
  label: string;
  tone: PatientBannerAlertTone;
}

export interface PatientBannerData {
  id: string;
  uhid: string;
  fullName: string;
  gender?: Gender | null;
  age?: string | null;
  phone?: string | null;
  bloodGroup?: BloodGroup | null;
  category?: string | null;
  isVip?: boolean;
  isDeceased?: boolean;
  allergies?: string[];
  allergyStatus?: AllergyStatus;
  /** What the `unavailable` flag says, e.g. "Allergies and alerts hidden: restricted record". */
  allergyNote?: string;
  alerts?: (string | PatientBannerAlert)[];
  payer?: string | null;
  /** The patient's photo as an image source; the initials are shown without it. */
  photoUrl?: string | null;
}

const useStyles = makeStyles({
  banner: {
    display: 'flex',
    alignItems: 'center',
    gap: '14px',
    padding: '12px 16px',
    backgroundColor: tokens.colorNeutralBackground1,
    borderRadius: tokens.borderRadiusLarge,
    boxShadow: tokens.shadow4,
    borderLeft: `4px solid ${tokens.colorBrandStroke1}`,
    flexWrap: 'wrap',
    position: 'sticky',
    top: '0',
    zIndex: 2,
  },
  identity: { display: 'flex', flexDirection: 'column', gap: '2px', minWidth: '220px', flex: 1 },
  nameRow: { display: 'flex', alignItems: 'center', gap: '8px', flexWrap: 'wrap' },
  name: { fontSize: tokens.fontSizeBase500, fontWeight: tokens.fontWeightSemibold },
  meta: { display: 'flex', gap: '14px', flexWrap: 'wrap', color: tokens.colorNeutralForeground2, alignItems: 'center' },
  metaItem: { display: 'inline-flex', alignItems: 'center', gap: '4px' },
  flags: { display: 'flex', gap: '6px', flexWrap: 'wrap', alignItems: 'center' },
  actions: { display: 'flex', gap: '8px', flexWrap: 'wrap' },
  uhid: { fontFamily: tokens.fontFamilyMonospace },
});

const ALERT_ICON: Record<PatientBannerAlertTone, ReactElement> = {
  danger: <AlertUrgentRegular />,
  warning: <WarningRegular />,
  informative: <InfoRegular />,
};

function AlertBadge({ alert }: { alert: string | PatientBannerAlert }) {
  if (typeof alert === 'string') {
    return (
      <Badge color="important" size="large" icon={<AlertUrgentRegular />}>
        {alert}
      </Badge>
    );
  }
  return (
    <Badge
      color={alert.tone}
      appearance={alert.tone === 'informative' ? 'tint' : 'filled'}
      size="large"
      icon={ALERT_ICON[alert.tone]}
    >
      {alert.label}
    </Badge>
  );
}

/** The banner's allergy and alert fields, which `PatientFlagBadges` also shows on their own. */
export type PatientFlagsData = Pick<PatientBannerData, 'allergies' | 'allergyStatus' | 'allergyNote' | 'alerts'>;

/**
 * The banner's allergy and alert badges on their own, for dialogs and counters that act on a patient without the full
 * banner (sample collection, the pharmacy counter, diet orders).
 */
export function PatientFlagBadges({ flags }: { flags: PatientFlagsData }) {
  const styles = useStyles();
  const allergies = flags.allergies ?? [];
  const alerts = flags.alerts ?? [];
  if (allergies.length === 0 && !flags.allergyStatus && alerts.length === 0) {
    return null;
  }

  return (
    <div className={styles.flags} role="group" aria-label="Allergies and alerts">
      {allergies.length > 0 ? (
        <Badge color="danger" size="large" icon={<WarningRegular />}>
          Allergies: {allergies.join(', ')}
        </Badge>
      ) : flags.allergyStatus === 'none' ? (
        <Badge appearance="tint" color="success">
          No known allergies
        </Badge>
      ) : flags.allergyStatus === 'unknown' ? (
        <Badge appearance="tint" color="warning" icon={<WarningRegular />}>
          Allergies not recorded
        </Badge>
      ) : flags.allergyStatus === 'checking' ? (
        <Badge appearance="outline" color="informative">
          Checking allergies…
        </Badge>
      ) : flags.allergyStatus === 'unavailable' ? (
        <Badge appearance="tint" color="warning" size="large" icon={<WarningRegular />}>
          {flags.allergyNote ?? 'Allergies and alerts unavailable'}
        </Badge>
      ) : null}
      {alerts.map((alert, index) => (
        <AlertBadge key={`${typeof alert === 'string' ? alert : alert.label}-${index}`} alert={alert} />
      ))}
    </div>
  );
}

/**
 * Persistent patient context (SRS UX-04, EHR-03): identity, age/sex, blood group and — most prominently —
 * allergies and clinical alerts, so they are visible on every clinical screen.
 */
export function PatientBanner({ patient, actions }: { patient: PatientBannerData; actions?: ReactNode }) {
  const styles = useStyles();

  return (
    <section className={styles.banner} aria-label={`Patient ${patient.fullName}, UHID ${patient.uhid}`}>
      <Avatar
        name={patient.fullName}
        size={48}
        color="colorful"
        image={patient.photoUrl ? { src: patient.photoUrl, alt: '' } : undefined}
      />
      <div className={styles.identity}>
        <div className={styles.nameRow}>
          <Text className={styles.name}>{patient.fullName}</Text>
          <Badge appearance="outline" className={styles.uhid}>
            {patient.uhid}
          </Badge>
          {patient.isVip && (
            <Tooltip content="Restricted record — access is audited" relationship="label">
              <Badge color="warning" icon={<ShieldKeyholeRegular />}>
                Restricted
              </Badge>
            </Tooltip>
          )}
          {patient.isDeceased && <Badge color="danger">Deceased</Badge>}
        </div>
        <div className={styles.meta}>
          <span className={styles.metaItem}>
            <Text weight="semibold">
              {patient.age ?? '—'} / {patient.gender ? genderShort[patient.gender] : '—'}
            </Text>
          </span>
          {patient.bloodGroup && patient.bloodGroup !== 'Unknown' && (
            <span className={styles.metaItem}>
              <DropRegular aria-hidden />
              {bloodGroupLabel[patient.bloodGroup]}
            </span>
          )}
          {patient.phone && (
            <span className={styles.metaItem}>
              <CallRegular aria-hidden />
              {formatPhone(patient.phone)}
            </span>
          )}
          {patient.payer && <Caption1>{patient.payer}</Caption1>}
          {patient.category && patient.category !== 'General' && <Caption1>{patient.category}</Caption1>}
        </div>
      </div>
      <PatientFlagBadges flags={patient} />
      {actions && <div className={styles.actions}>{actions}</div>}
    </section>
  );
}
