import {
  Badge,
  Button,
  Caption1,
  makeStyles,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  Text,
  tokens,
} from '@fluentui/react-components';
import { ArrowRepeatAllRegular, ArrowSyncRegular, DismissRegular } from '@fluentui/react-icons';
import { formatDateTime } from '@hc360/core';
import { ErrorState, LoadingState, Page, PageHeader, Section, StatusBadge, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { DocumentButtons } from '@/lib/DocumentButtons';
import { usePermission } from '@/lib/permissions';
import { emrKeys } from '@/modules/emr/api';
import { ChartLoadError, ClinicalBanner } from '@/modules/emr/ClinicalBanner';
import { directionsText } from '@/modules/emr/dosing';
import { DigitalSignatureBadge, DigitalSignatureSection } from '@/modules/emr/DigitalSignature';
import { useClinicalSummary } from '@/modules/emr/hooks';
import { PrescriptionEditor } from '@/modules/emr/PrescriptionEditor';
import { medicineNames } from '@/modules/emr/prescriptionForm';
import {
  canIssueFinalCopy,
  prescriptionDocument,
  prescriptionQueries,
  prescriptionsApi,
  type PrescriptionDto,
} from '@/modules/emr/prescriptionsApi';
import { RefillDialog } from '@/modules/emr/PrescriptionsList';
import { ReasonDialog } from '@/modules/emr/ui';

export const Route = createFileRoute('/_app/emr/prescriptions/$prescriptionId')({
  component: PrescriptionPage,
});

const useStyles = makeStyles({
  lines: { display: 'flex', flexDirection: 'column', gap: '10px', margin: 0, padding: 0, listStyle: 'none' },
  line: {
    display: 'flex',
    flexDirection: 'column',
    gap: '2px',
    paddingBottom: '8px',
    borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  row: { display: 'flex', gap: '8px', alignItems: 'baseline', flexWrap: 'wrap' },
  muted: { color: tokens.colorNeutralForeground3 },
  actions: { display: 'flex', gap: '8px', flexWrap: 'wrap' },
});

function PrescriptionPage() {
  const { prescriptionId } = Route.useParams();
  const prescription = useQuery(prescriptionQueries.detail(prescriptionId));
  if (prescription.isLoading) {
    return <LoadingState />;
  }
  if (prescription.error || !prescription.data) {
    return (
      <Page maxWidth={900}>
        <ErrorState error={prescription.error} onRetry={() => void prescription.refetch()} />
      </Page>
    );
  }
  return <PrescriptionView key={prescriptionId} prescription={prescription.data} />;
}

/**
 * One prescription: drafts open in the editor (check and sign); signed ones can be printed, downloaded as a PDF,
 * repeated, refilled or cancelled, and signed with the doctor's DSC in HC360 Desktop where the hospital uses it. Where
 * the hospital requires the DSC, print and PDF wait for a valid DSC signature.
 */
function PrescriptionView({ prescription: rx }: { prescription: PrescriptionDto }) {
  const styles = useStyles();
  const notify = useNotify();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const canWrite = usePermission('clinical.prescriptions.write');
  const canSign = usePermission('clinical.prescriptions.sign');
  const canRefill = usePermission('clinical.prescriptions.refill');
  const summary = useClinicalSummary(rx.patientId);
  const [cancelling, setCancelling] = useState(false);
  const [refilling, setRefilling] = useState(false);

  const refresh = () => {
    void queryClient.invalidateQueries({ queryKey: prescriptionQueries.detail(rx.id).queryKey });
    void queryClient.invalidateQueries({ queryKey: emrKeys.patient(rx.patientId) });
  };

  const repeat = useMutation({
    mutationFn: () => prescriptionsApi.repeat(rx.id),
    onSuccess: (draft) => {
      notify.success(`Draft ${draft.prescriptionNumber} created`, 'Review the medicines, then check and sign it.');
      refresh();
      void navigate({ to: '/emr/prescriptions/$prescriptionId', params: { prescriptionId: draft.id } });
    },
    onError: (error) => notify.error(error, 'Could not repeat the prescription'),
  });
  const cancel = useMutation({
    mutationFn: (reason: string) => prescriptionsApi.cancel(rx.id, reason),
    onSuccess: () => {
      setCancelling(false);
      notify.success('Prescription cancelled');
      refresh();
    },
    onError: (error) => notify.error(error, 'Could not cancel the prescription'),
  });

  if (summary.error) {
    return <ChartLoadError patientId={rx.patientId} error={summary.error} onRetry={() => void summary.refetch()} />;
  }

  const toChart = () =>
    void navigate({
      to: '/emr/patients/$patientId',
      params: { patientId: rx.patientId },
      search: { tab: 'prescriptions' },
    });

  return (
    <Page maxWidth={1400}>
      {summary.data && <ClinicalBanner summary={summary.data} />}
      <PageHeader
        title={`Prescription ${rx.prescriptionNumber}`}
        subtitle={`${rx.practitionerName ?? 'Doctor'} · ${rx.isInpatient ? 'In-patient' : 'Out-patient'} · ${formatDateTime(rx.signedAt ?? rx.prescribedAt)}`}
        breadcrumbs={[
          { label: 'Chart', onClick: toChart },
          { label: 'Prescriptions', onClick: toChart },
          { label: rx.prescriptionNumber },
        ]}
        actions={
          <div className={styles.actions}>
            <StatusBadge status={rx.status} />
            <DigitalSignatureBadge prescription={rx} />
            {canIssueFinalCopy(rx) && <DocumentButtons path={prescriptionDocument(rx.id)} printLabel="Print (A5)" />}
            {canWrite && rx.status !== 'Draft' && (
              <Button icon={<ArrowRepeatAllRegular />} disabled={repeat.isPending} onClick={() => repeat.mutate()}>
                Repeat
              </Button>
            )}
            {canRefill && rx.status === 'Signed' && rx.refillsRemaining > 0 && (
              <Button icon={<ArrowSyncRegular />} onClick={() => setRefilling(true)}>
                Refill ({rx.refillsRemaining} left)
              </Button>
            )}
            {canSign && rx.status !== 'Cancelled' && (
              <Button icon={<DismissRegular />} onClick={() => setCancelling(true)}>
                Cancel
              </Button>
            )}
          </div>
        }
      />

      {rx.status === 'Draft' && canWrite ? (
        <Section title="Draft" description="Not yet sent to the pharmacy. Check safety, then sign.">
          <PrescriptionEditor
            key={rx.id}
            patientId={rx.patientId}
            encounterId={rx.encounterId}
            visitId={rx.visitId}
            admissionId={rx.admissionId}
            isInpatient={rx.isInpatient}
            initialDraft={rx}
            currentMedications={summary.data?.currentMedications ?? []}
            onSigned={refresh}
          />
        </Section>
      ) : (
        <Section title={`Medicines (${rx.items.length})`}>
          {rx.status === 'Cancelled' && (
            <MessageBar intent="warning">
              <MessageBarBody>
                <MessageBarTitle>Cancelled {formatDateTime(rx.cancelledAt)}</MessageBarTitle>
                {rx.cancelReason}
              </MessageBarBody>
            </MessageBar>
          )}
          {rx.safetyOverrideReason && (
            <MessageBar intent="warning" layout="multiline">
              <MessageBarBody>
                <MessageBarTitle>Signed with a safety override</MessageBarTitle>
                {rx.safetyOverrideReason}
              </MessageBarBody>
            </MessageBar>
          )}
          <ul className={styles.lines}>
            {rx.items.map((item) => {
              const { name, detail } = medicineNames(item);
              return (
                <li key={item.id} className={styles.line}>
                  <span className={styles.row}>
                    <Text weight="semibold">
                      {item.sequence}. {name}
                    </Text>
                    {detail && <Caption1 className={styles.muted}>{detail}</Caption1>}
                    {item.isControlledDrug && (
                      <Badge size="small" color="danger" appearance="outline">
                        Schedule {item.controlledSchedule ?? 'H1/X'}
                      </Badge>
                    )}
                  </span>
                  <Text>
                    {directionsText({
                      dose: item.dose,
                      frequency: item.frequency,
                      customFrequency: item.customFrequency,
                      durationDays: item.durationDays,
                      route: item.route,
                      instructions: item.instructions,
                    })}
                  </Text>
                  <Caption1 className={styles.muted}>Quantity {item.quantity}</Caption1>
                </li>
              );
            })}
          </ul>
          {rx.advice && (
            <>
              <div style={{ height: 12 }} />
              <Caption1 className={styles.muted}>Advice</Caption1>
              <Text block style={{ whiteSpace: 'pre-wrap' }}>
                {rx.advice}
              </Text>
            </>
          )}
          {rx.notes && (
            <>
              <div style={{ height: 8 }} />
              <Caption1 className={styles.muted}>Note for the pharmacy</Caption1>
              <Text block>{rx.notes}</Text>
            </>
          )}
        </Section>
      )}

      {rx.status !== 'Draft' && <DigitalSignatureSection prescription={rx} canSign={canSign} />}

      <ReasonDialog
        open={cancelling}
        title={`Cancel ${rx.prescriptionNumber}?`}
        description={
          rx.status === 'Signed'
            ? 'It is withdrawn from the pharmacy queue. Medicines already dispensed are not affected.'
            : 'The draft is discarded.'
        }
        confirmLabel="Cancel prescription"
        destructive
        busy={cancel.isPending}
        onConfirm={(reason) => cancel.mutate(reason)}
        onCancel={() => setCancelling(false)}
      />
      <RefillDialog
        prescriptionId={refilling ? rx.id : null}
        patientId={rx.patientId}
        onClose={() => {
          setRefilling(false);
          refresh();
        }}
      />
    </Page>
  );
}
