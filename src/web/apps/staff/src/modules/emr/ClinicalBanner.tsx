import {
  Button,
  Field,
  makeStyles,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  Skeleton,
  SkeletonItem,
  Text,
  Textarea,
} from '@fluentui/react-components';
import { formatDateTime, humanize } from '@hc360/core';
import { ErrorState, Page, Section, useNotify, type PatientBannerData } from '@hc360/ui';
import { useMutation } from '@tanstack/react-query';
import { useState, type ReactNode } from 'react';
import { usePermission } from '@/lib/permissions';
import { patientApi } from '@/modules/patients/api';
import { PatientBannerWithPhoto } from '@/modules/patients/PatientBannerWithPhoto';
import type { PatientClinicalSummary } from './api';
import { flagFields } from './flags';
import { isRestricted } from './hooks';

const useStyles = makeStyles({
  skeleton: { display: 'flex', gap: '12px', padding: '12px 16px' },
  breakGlass: { display: 'flex', flexDirection: 'column', gap: '12px', maxWidth: '560px' },
});

function bannerData(summary: PatientClinicalSummary): PatientBannerData {
  const p = summary.patient;
  return {
    id: p.id,
    uhid: p.uhid,
    fullName: p.fullName,
    gender: p.gender,
    age: p.age,
    bloodGroup: p.bloodGroup,
    category: p.category === 'General' ? null : humanize(p.category),
    isVip: p.isVip,
    isDeceased: p.isDeceased,
    // The summary carries the same flags as the other screens' banners, so it is shown the same way.
    ...flagFields(summary),
  };
}

/**
 * The persistent clinical banner (EHR-03): identity, allergies and alerts from the Clinical module, always visible
 * (sticky) on the chart and the encounter workspace.
 */
export function ClinicalBanner({ summary, actions }: { summary: PatientClinicalSummary; actions?: ReactNode }) {
  return (
    <PatientBannerWithPhoto patient={bannerData(summary)} photoFileId={summary.patient.photoFileId} actions={actions} />
  );
}

export function BannerSkeleton() {
  const styles = useStyles();
  return (
    <Section>
      <Skeleton className={styles.skeleton} aria-label="Loading patient">
        <SkeletonItem shape="circle" size={48} />
        <div style={{ flex: 1, display: 'flex', flexDirection: 'column', gap: 8 }}>
          <SkeletonItem size={20} style={{ width: '40%' }} />
          <SkeletonItem size={12} style={{ width: '60%' }} />
        </div>
      </Skeleton>
    </Section>
  );
}

/** Error screen for chart loads: restricted records offer audited emergency access, anything else a retry. */
export function ChartLoadError({
  patientId,
  error,
  onRetry,
}: {
  patientId: string;
  error: unknown;
  onRetry: () => void;
}) {
  if (isRestricted(error)) {
    return <BreakGlass patientId={patientId} onGranted={onRetry} />;
  }
  return (
    <Page maxWidth={900}>
      <ErrorState error={error} onRetry={onRetry} />
    </Page>
  );
}

function BreakGlass({ patientId, onGranted }: { patientId: string; onGranted: () => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const canBreakGlass = usePermission('patients.break_glass');
  const [reason, setReason] = useState('');
  const request = useMutation({
    mutationFn: () => patientApi.breakGlass(patientId, reason.trim()),
    onSuccess: (grant) => {
      notify.warning('Emergency access granted', `Until ${formatDateTime(grant.expiresAt)}. This access is recorded.`);
      onGranted();
    },
    onError: (error) => notify.error(error, 'Emergency access was not granted'),
  });

  return (
    <Page maxWidth={720}>
      <Section title="Restricted record">
        <div className={styles.breakGlass}>
          <MessageBar intent="warning" layout="multiline">
            <MessageBarBody>
              <MessageBarTitle>Access to this patient's clinical record is restricted</MessageBarTitle>
              Use emergency access only when it is clinically necessary. Your name, the reason and every screen you open
              are recorded and reviewed.
            </MessageBarBody>
          </MessageBar>
          {canBreakGlass ? (
            <>
              <Field
                label="Reason for emergency access"
                required
                hint="At least 15 characters, for example: unconscious patient in casualty, needs allergy history"
              >
                <Textarea value={reason} onChange={(_, d) => setReason(d.value)} rows={3} maxLength={500} />
              </Field>
              <div>
                <Button
                  appearance="primary"
                  disabled={reason.trim().length < 15 || request.isPending}
                  onClick={() => request.mutate()}
                >
                  Request emergency access
                </Button>
              </div>
            </>
          ) : (
            <Text>Ask the treating doctor or the duty medical officer to open this record.</Text>
          )}
        </div>
      </Section>
    </Page>
  );
}
