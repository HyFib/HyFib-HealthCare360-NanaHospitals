import {
  Button,
  Caption1,
  makeStyles,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  Table,
  TableBody,
  TableCell,
  TableHeader,
  TableHeaderCell,
  TableRow,
  Text,
  tokens,
  useRestoreFocusTarget,
} from '@fluentui/react-components';
import { DismissCircleRegular, EditRegular, SendRegular } from '@fluentui/react-icons';
import { formatDateOnly, formatDateTime, isApiError, todayIso } from '@hc360/core';
import { ErrorState, LoadingState, Page, PageHeader, Section, StatusBadge, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState, type ReactNode } from 'react';
import { usePermission } from '@/lib/permissions';
import { RequirePermission } from '@/modules/hr/shared/NoAccess';
import { ReasonDialog } from '@/modules/ot/shared/feedback';
import {
  causeLineLabel,
  DEATH_READERS,
  deathDocument,
  deathMannerLabel,
  pregnancyLabel,
  VITAL_PERMISSIONS,
  vitalApi,
  vitalKeys,
  vitalQueries,
  vitalStatusLabel,
  vitalStatusTone,
  type DeathRecordDto,
} from '@/modules/mrd/vital';
import { DeathDialog, PrintVitalButton, ReportDialog } from '@/modules/mrd/VitalDialogs';
import { REPORT_WITHIN_DAYS } from '@/modules/mrd/vitalRules';

export const Route = createFileRoute('/_app/mrd/deaths/$deathId')({
  component: () => (
    <RequirePermission permission={DEATH_READERS} what="deaths">
      <DeathPage />
    </RequirePermission>
  ),
});

const useStyles = makeStyles({
  stack: { display: 'flex', flexDirection: 'column', gap: '12px' },
  facts: { display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(210px, 1fr))', gap: '12px 24px' },
  fact: { display: 'flex', flexDirection: 'column', gap: '2px', minWidth: 0 },
  label: { color: tokens.colorNeutralForeground3 },
  actions: { display: 'flex', gap: '8px', flexWrap: 'wrap', alignItems: 'center' },
  line: { width: '170px' },
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

/** One death: its medical certificate of cause of death (Form 4), its report to the Registrar, and the printed form. */
function DeathPage() {
  const { deathId } = Route.useParams();
  const death = useQuery(vitalQueries.death(deathId));

  if (death.isLoading) {
    return <LoadingState label="Opening the certificate…" />;
  }
  if (death.error || !death.data) {
    return (
      <Page maxWidth={900}>
        <ErrorState
          error={death.error}
          onRetry={() => void death.refetch()}
          title={isApiError(death.error) && death.error.status === 404 ? 'Death not found' : undefined}
        />
      </Page>
    );
  }
  return <DeathView death={death.data} />;
}

type Dialog = 'correct' | 'report' | 'error' | null;

function DeathView({ death }: { death: DeathRecordDto }) {
  const styles = useStyles();
  // Closing a dialog returns focus to the button that opened it.
  const restoreFocusTarget = useRestoreFocusTarget();
  const navigate = useNavigate();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const canCertify = usePermission(VITAL_PERMISSIONS.deathsCertify);
  const canRegister = usePermission(VITAL_PERMISSIONS.register);
  const canMarkError = usePermission(DEATH_READERS);
  const [dialog, setDialog] = useState<Dialog>(null);
  const recorded = death.status === 'Recorded';
  const overdue = recorded && todayIso() > death.dueBy;

  const markError = useMutation({
    mutationFn: (reason: string) => vitalApi.deathInError(death.id, reason, death.version),
    onSuccess: (saved) => {
      queryClient.setQueryData(vitalKeys.death(death.id), saved);
      void queryClient.invalidateQueries({ queryKey: vitalKeys.registers('death') });
      setDialog(null);
      notify.success('Marked as entered in error', `${saved.serialNumber} is kept, but out of the register.`);
    },
  });

  return (
    <Page>
      <PageHeader
        title={`Death certificate ${death.serialNumber}`}
        subtitle={`${death.patientName} · ${death.uhid} · died ${formatDateTime(death.diedAt)}`}
        breadcrumbs={[
          { label: 'Deaths', onClick: () => void navigate({ to: '/mrd/deaths' }) },
          { label: death.serialNumber },
        ]}
        actions={
          <StatusBadge
            status={death.status}
            tone={vitalStatusTone[death.status]}
            label={vitalStatusLabel[death.status]}
          />
        }
      />
      {death.status === 'EnteredInError' && (
        <MessageBar intent="info">
          <MessageBarBody>
            <MessageBarTitle>Entered in error</MessageBarTitle>
            {death.errorReason
              ? `The certificate is kept, but out of the death register: ${death.errorReason}`
              : 'The certificate is kept, but out of the death register.'}
          </MessageBarBody>
        </MessageBar>
      )}
      {recorded && (
        <MessageBar intent={overdue ? 'warning' : 'info'}>
          <MessageBarBody>
            <MessageBarTitle>{overdue ? 'Overdue' : 'Not yet reported'}</MessageBarTitle>
            {overdue
              ? `The ${REPORT_WITHIN_DAYS} days to report the death to the Registrar ended on ${formatDateOnly(death.dueBy)}. Report it now; a late registration needs a late fee.`
              : `Report the death to the Registrar by ${formatDateOnly(death.dueBy)}. The certificate can be corrected until it is reported.`}
          </MessageBarBody>
        </MessageBar>
      )}
      <Section
        title="Medical certificate of cause of death (Form 4)"
        actions={
          <div className={styles.actions}>
            {canRegister && recorded && (
              <Button
                appearance="primary"
                icon={<SendRegular />}
                onClick={() => setDialog('report')}
                {...restoreFocusTarget}
              >
                Report to Registrar
              </Button>
            )}
            {canCertify && recorded && (
              <Button icon={<EditRegular />} onClick={() => setDialog('correct')} {...restoreFocusTarget}>
                Correct
              </Button>
            )}
            {canMarkError && recorded && (
              <Button icon={<DismissCircleRegular />} onClick={() => setDialog('error')} {...restoreFocusTarget}>
                Entered in error
              </Button>
            )}
            <PrintVitalButton path={deathDocument(death.id)} label="Print certificate" />
          </div>
        }
      >
        <div className={styles.stack}>
          <div className={styles.facts}>
            <Fact label="Name of the deceased">{`${death.patientName} (${death.uhid})`}</Fact>
            <Fact label="Sex">{death.gender}</Fact>
            <Fact label="Age">{death.ageYears !== null ? `${death.ageYears} years` : null}</Fact>
            <Fact label="Date and time of death">{formatDateTime(death.diedAt)}</Fact>
          </div>
          <Table size="small" aria-label="Cause of death, Part I">
            <TableHeader>
              <TableRow>
                <TableHeaderCell className={styles.line}>Part I</TableHeaderCell>
                <TableHeaderCell>Condition</TableHeaderCell>
                <TableHeaderCell>Interval between onset and death</TableHeaderCell>
              </TableRow>
            </TableHeader>
            <TableBody>
              {death.causes.map((c, i) => (
                <TableRow key={i}>
                  <TableCell className={styles.line}>{causeLineLabel[Math.min(i, 3)]}</TableCell>
                  <TableCell>{c.condition}</TableCell>
                  <TableCell>{c.interval ?? ''}</TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
          <div className={styles.facts}>
            <Fact label="Part II: other significant conditions">{death.otherConditions}</Fact>
            <Fact label="Manner of death">{deathMannerLabel[death.manner]}</Fact>
            <Fact label="Medico-legal case">{death.medicoLegal ? 'Yes' : 'No'}</Fact>
            <Fact label="If a woman, pregnancy">
              {death.pregnancy === 'NotApplicable' ? null : pregnancyLabel[death.pregnancy]}
            </Fact>
            <Fact label="Certified by">
              {`${death.certifiedBy} (registration ${death.certifierRegistrationNumber}), ${formatDateTime(death.certifiedAt)}`}
            </Fact>
          </div>
        </div>
      </Section>
      <Section title="Registration">
        <div className={styles.facts}>
          <Fact label="To be reported by">{formatDateOnly(death.dueBy)}</Fact>
          <Fact label="Reported on">{death.reportedOn ? formatDateOnly(death.reportedOn) : 'Not yet reported'}</Fact>
          <Fact label="Registrar's registration no.">{death.registrationNumber}</Fact>
        </div>
      </Section>
      {dialog === 'correct' && <DeathDialog existing={death} onClose={() => setDialog(null)} />}
      {dialog === 'report' && <ReportDialog kind="death" id={death.id} onClose={() => setDialog(null)} />}
      <ReasonDialog
        open={dialog === 'error'}
        title={`Mark certificate ${death.serialNumber} as entered in error?`}
        description="For a certificate made for the wrong patient, or twice. It is kept, but leaves the death register and is not reported."
        label="Reason"
        confirmLabel="Entered in error"
        destructive
        busy={markError.isPending}
        error={markError.error}
        presets={['Certified for the wrong patient', 'Certified twice']}
        onConfirm={(reason) => markError.mutate(reason)}
        onCancel={() => {
          markError.reset();
          setDialog(null);
        }}
      />
    </Page>
  );
}
