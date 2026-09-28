import {
  Badge,
  Button,
  makeStyles,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  Skeleton,
  SkeletonItem,
} from '@fluentui/react-components';
import { PersonRegular, WalletRegular, WarningRegular } from '@fluentui/react-icons';
import { formatInr, isApiError } from '@hc360/core';
import { PatientBanner } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { useNavigate } from '@tanstack/react-router';
import type { ReactNode } from 'react';
import { usePermission } from '@/lib/permissions';
import { patientQueries } from '@/modules/patients/api';
import { billingQueries } from './api';

const useStyles = makeStyles({
  root: { display: 'flex', flexDirection: 'column', gap: '8px' },
  chips: { display: 'flex', gap: '8px', flexWrap: 'wrap', alignItems: 'center' },
});

/**
 * Patient context on billing screens: the standard banner plus what matters at the counter — unpaid bills and any
 * advance that can be used. Restricted (VIP) records explain themselves instead of failing silently.
 */
export function BillingPatientBanner({
  patientId,
  actions,
  showDues = true,
}: {
  patientId: string;
  actions?: ReactNode;
  showDues?: boolean;
}) {
  const styles = useStyles();
  const navigate = useNavigate();
  const canSeeDues = usePermission(['billing.payments.collect', 'billing.charges.read']);
  const patient = useQuery(patientQueries.detail(patientId));
  const dues = useQuery({ ...billingQueries.dues(patientId), enabled: showDues && canSeeDues });

  if (patient.isLoading) {
    return (
      <Skeleton aria-label="Loading patient">
        <SkeletonItem size={48} />
      </Skeleton>
    );
  }
  if (patient.error || !patient.data) {
    const restricted = isApiError(patient.error) && patient.error.code === 'patient.restricted';
    return (
      <MessageBar intent="warning" layout="multiline">
        <MessageBarBody>
          <MessageBarTitle>{restricted ? 'Restricted record' : 'Could not load the patient'}</MessageBarTitle>
          {restricted
            ? 'This patient’s record is restricted. Emergency access is requested from the patient profile.'
            : isApiError(patient.error)
              ? patient.error.userMessage
              : 'Try again in a moment.'}
        </MessageBarBody>
        <Button
          icon={<PersonRegular />}
          onClick={() => void navigate({ to: '/patients/$patientId', params: { patientId } })}
        >
          Patient profile
        </Button>
      </MessageBar>
    );
  }

  const p = patient.data;
  const d = dues.data;
  return (
    <div className={styles.root}>
      <PatientBanner
        patient={{
          id: p.id,
          uhid: p.uhid,
          fullName: p.fullName,
          gender: p.gender,
          age: p.age,
          phone: p.phone,
          bloodGroup: p.bloodGroup,
          category: p.category,
          isVip: p.isVip,
          isDeceased: p.isDeceased,
        }}
        actions={actions}
      />
      {p.mergedIntoPatientId && (
        <MessageBar intent="warning">
          <MessageBarBody>
            <MessageBarTitle>Merged record</MessageBarTitle>
            This UHID was merged into another record. Bill the surviving record.
          </MessageBarBody>
        </MessageBar>
      )}
      {d && (d.totalDue > 0 || d.advanceBalance > 0) && (
        <div className={styles.chips}>
          {d.totalDue > 0 && (
            <Badge size="large" appearance="tint" color="warning" icon={<WarningRegular />}>
              Unpaid bills: {formatInr(d.totalDue)} on {d.bills.length} bill{d.bills.length === 1 ? '' : 's'}
            </Badge>
          )}
          {d.advanceBalance > 0 && (
            <Badge size="large" appearance="tint" color="informative" icon={<WalletRegular />}>
              Advance held: {formatInr(d.advanceBalance)}
            </Badge>
          )}
        </div>
      )}
    </div>
  );
}
