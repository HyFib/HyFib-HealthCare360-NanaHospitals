import { Avatar, Badge, Caption1, makeStyles, Text, tokens, Tooltip } from '@fluentui/react-components';
import {
  AlertUrgentRegular,
  CallRegular,
  DropRegular,
  ShieldKeyholeRegular,
  WarningRegular,
} from '@fluentui/react-icons';
import { bloodGroupLabel, formatPhone, genderShort, type BloodGroup, type Gender } from '@hc360/core';
import type { ReactNode } from 'react';

/**
 * What is known about allergies: some recorded, a clinician confirmed "no known allergies", or nothing recorded yet.
 * The banner claims "no known allergies" only when that was recorded, never from an empty list.
 */
export type AllergyStatus = 'recorded' | 'none' | 'unknown';

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
  alerts?: string[];
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

/**
 * Persistent patient context (SRS UX-04, EHR-03): identity, age/sex, blood group and — most prominently —
 * allergies and clinical alerts, so they are visible on every clinical screen.
 */
export function PatientBanner({ patient, actions }: { patient: PatientBannerData; actions?: ReactNode }) {
  const styles = useStyles();
  const allergies = patient.allergies ?? [];
  const alerts = patient.alerts ?? [];

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
      <div className={styles.flags}>
        {allergies.length > 0 ? (
          <Badge color="danger" size="large" icon={<WarningRegular />}>
            Allergies: {allergies.join(', ')}
          </Badge>
        ) : patient.allergyStatus === 'none' ? (
          <Badge appearance="tint" color="success">
            No known allergies
          </Badge>
        ) : patient.allergyStatus === 'unknown' ? (
          <Badge appearance="tint" color="warning" icon={<WarningRegular />}>
            Allergies not recorded
          </Badge>
        ) : null}
        {alerts.map((alert) => (
          <Badge key={alert} color="important" size="large" icon={<AlertUrgentRegular />}>
            {alert}
          </Badge>
        ))}
      </div>
      {actions && <div className={styles.actions}>{actions}</div>}
    </section>
  );
}
