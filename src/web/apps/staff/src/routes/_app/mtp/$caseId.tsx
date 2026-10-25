import {
  Button,
  Caption1,
  makeStyles,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  Text,
  tokens,
  useRestoreFocusTarget,
} from '@fluentui/react-components';
import { CheckmarkCircleRegular, DismissCircleRegular, EditRegular } from '@fluentui/react-icons';
import { formatDate, formatDateTime, isApiError } from '@hc360/core';
import { ErrorState, LoadingState, Page, PageHeader, Section, StatusBadge, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState, type ReactNode } from 'react';
import { DocumentButtons } from '@/lib/DocumentButtons';
import { usePermission } from '@/lib/permissions';
import { RequirePermission } from '@/modules/hr/shared/NoAccess';
import {
  MTP_CASE_READERS,
  MTP_PERMISSIONS,
  mtpApi,
  mtpCaseDocument,
  mtpCategoryLabel,
  mtpGroundLabel,
  mtpKeys,
  mtpMethodLabel,
  mtpQueries,
  mtpStatusLabel,
  mtpStatusTone,
  type MtpCaseDto,
} from '@/modules/mtp/api';
import { MtpConfidential } from '@/modules/mtp/MtpConfidential';
import { CaseDialog, TerminationDialog } from '@/modules/mtp/MtpDialogs';
import { ReasonDialog } from '@/modules/ot/shared/feedback';

export const Route = createFileRoute('/_app/mtp/$caseId')({
  component: () => (
    <RequirePermission permission={MTP_CASE_READERS} what="terminations of pregnancy">
      <MtpCasePage />
    </RequirePermission>
  ),
});

const useStyles = makeStyles({
  stack: { display: 'flex', flexDirection: 'column', gap: '12px' },
  facts: { display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(210px, 1fr))', gap: '12px 24px' },
  fact: { display: 'flex', flexDirection: 'column', gap: '2px', minWidth: 0 },
  label: { color: tokens.colorNeutralForeground3 },
  actions: { display: 'flex', gap: '8px', flexWrap: 'wrap', alignItems: 'center' },
});

function Fact({ label, children }: { label: string; children: ReactNode }) {
  const styles = useStyles();
  if (children === null || children === undefined || children === '') {
    return null;
  }
  return (
    <div className={styles.fact}>
      <Caption1 className={styles.label}>{label}</Caption1>
      <Text>{children}</Text>
    </div>
  );
}

/** One termination of pregnancy: the consent and the opinion, the termination once done, and the printed forms. */
function MtpCasePage() {
  const { caseId } = Route.useParams();
  const mtp = useQuery(mtpQueries.case(caseId));

  if (mtp.isLoading) {
    return <LoadingState label="Opening the case…" />;
  }
  if (mtp.error || !mtp.data) {
    return (
      <Page maxWidth={900}>
        <ErrorState
          error={mtp.error}
          onRetry={() => void mtp.refetch()}
          title={isApiError(mtp.error) && mtp.error.status === 404 ? 'Case not found' : undefined}
        />
      </Page>
    );
  }
  return <CaseView mtp={mtp.data} />;
}

type Dialog = 'correct' | 'terminate' | 'cancel' | null;

function CaseView({ mtp }: { mtp: MtpCaseDto }) {
  const styles = useStyles();
  // Closing a dialog returns focus to the button that opened it.
  const restoreFocusTarget = useRestoreFocusTarget();
  const navigate = useNavigate();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const canCertify = usePermission(MTP_PERMISSIONS.certify);
  const [dialog, setDialog] = useState<Dialog>(null);
  const open = mtp.status === 'Certified';

  const cancel = useMutation({
    mutationFn: (reason: string) => mtpApi.cancel(mtp.id, reason, mtp.version),
    onSuccess: (saved) => {
      queryClient.setQueryData(mtpKeys.case(mtp.id), saved);
      void queryClient.invalidateQueries({ queryKey: mtpKeys.cases });
      void queryClient.invalidateQueries({ queryKey: ['mtp', 'register'] });
      setDialog(null);
      notify.success('Case cancelled', `${saved.serialNumber} stays in no register.`);
    },
  });

  return (
    <Page>
      <PageHeader
        title={`MTP case ${mtp.serialNumber}`}
        subtitle={`${mtp.patientName} · ${mtp.uhid}${mtp.approvalNumber ? ` · certificate of approval ${mtp.approvalNumber}` : ''}`}
        breadcrumbs={[
          { label: 'MTP cases', onClick: () => void navigate({ to: '/mtp' }) },
          { label: mtp.serialNumber },
        ]}
        actions={
          <StatusBadge status={mtp.status} tone={mtpStatusTone[mtp.status]} label={mtpStatusLabel[mtp.status]} />
        }
      />
      <MtpConfidential />
      {mtp.status === 'Cancelled' && (
        <MessageBar intent="info">
          <MessageBarBody>
            <MessageBarTitle>Cancelled</MessageBarTitle>
            {mtp.cancelReason
              ? `The termination will not be done here: ${mtp.cancelReason}`
              : 'The termination will not be done here.'}
          </MessageBarBody>
        </MessageBar>
      )}
      {open && (
        <MessageBar intent="info">
          <MessageBarBody>
            <MessageBarTitle>Certified, not yet done</MessageBarTitle>
            The consent and the opinion are recorded. Record the termination once it is done; it then enters the
            admission register and the case cannot change.
          </MessageBarBody>
        </MessageBar>
      )}
      <Section
        title="Consent and opinion (Forms C and I)"
        actions={
          <div className={styles.actions}>
            {canCertify && open && (
              <>
                <Button
                  appearance="primary"
                  icon={<CheckmarkCircleRegular />}
                  onClick={() => setDialog('terminate')}
                  {...restoreFocusTarget}
                >
                  Record termination
                </Button>
                <Button icon={<EditRegular />} onClick={() => setDialog('correct')} {...restoreFocusTarget}>
                  Correct details
                </Button>
                <Button icon={<DismissCircleRegular />} onClick={() => setDialog('cancel')} {...restoreFocusTarget}>
                  Cancel the case
                </Button>
              </>
            )}
            <DocumentButtons path={mtpCaseDocument(mtp.id)} printLabel="Print forms" />
          </div>
        }
      >
        <div className={styles.facts}>
          <Fact label="Age">{`${mtp.ageYears} years${mtp.mentallyIll ? ', mentally ill' : ''}`}</Fact>
          <Fact label="Consent">
            {mtp.consentBy === 'Guardian'
              ? `Her guardian, ${mtp.guardianName} (${mtp.guardianRelation}), on ${formatDate(mtp.consentOn)}`
              : `Her own, on ${formatDate(mtp.consentOn)}`}
          </Fact>
          <Fact label="Length of pregnancy">{`${mtp.weeksOfGestation} weeks`}</Fact>
          <Fact label="Grounds">{mtp.grounds.map((g) => mtpGroundLabel[g]).join('; ')}</Fact>
          <Fact label="Category (rule 3B)">{mtp.category ? mtpCategoryLabel[mtp.category] : null}</Fact>
          <Fact label="Medical Board's opinion">{mtp.medicalBoardReference}</Fact>
          <Fact label="Opinion formed by">
            {mtp.opinions
              .map((o) => `${o.doctorName} (${o.registrationNumber}), ${formatDate(o.opinionOn)}`)
              .join('; ')}
          </Fact>
          <Fact label="Certified">
            {`${formatDateTime(mtp.certifiedAt)}${mtp.certifiedBy ? `, by ${mtp.certifiedBy}` : ''}`}
          </Fact>
        </div>
      </Section>
      {mtp.status === 'Terminated' && (
        <Section title="Termination (Form III)">
          <div className={styles.facts}>
            <Fact label="Terminated on">{mtp.terminatedOn ? formatDate(mtp.terminatedOn) : null}</Fact>
            <Fact label="Method">{mtp.method ? mtpMethodLabel[mtp.method] : null}</Fact>
            <Fact label="Performed by">{mtp.performedBy}</Fact>
            <Fact label="Discharged on">{mtp.dischargedOn ? formatDate(mtp.dischargedOn) : null}</Fact>
            <Fact label="Result">{mtp.outcome}</Fact>
            <Fact label="Remarks">{mtp.remarks}</Fact>
          </div>
        </Section>
      )}
      {dialog === 'correct' && <CaseDialog existing={mtp} onClose={() => setDialog(null)} />}
      {dialog === 'terminate' && <TerminationDialog mtp={mtp} onClose={() => setDialog(null)} />}
      <ReasonDialog
        open={dialog === 'cancel'}
        title={`Cancel MTP case ${mtp.serialNumber}?`}
        description="The termination will not be done here: she withdrew her consent, or went elsewhere. The case is closed and stays in no register."
        label="Reason"
        confirmLabel="Cancel the case"
        destructive
        busy={cancel.isPending}
        error={cancel.error}
        presets={['She withdrew her consent', 'She went to another hospital']}
        onConfirm={(reason) => cancel.mutate(reason)}
        onCancel={() => {
          cancel.reset();
          setDialog(null);
        }}
      />
    </Page>
  );
}
