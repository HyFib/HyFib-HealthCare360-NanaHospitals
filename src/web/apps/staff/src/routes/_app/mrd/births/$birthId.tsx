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
  BIRTH_READERS,
  birthAttendantLabel,
  birthDocument,
  birthOutcomeLabel,
  birthSexLabel,
  deliveryMethodLabel,
  VITAL_PERMISSIONS,
  vitalApi,
  vitalKeys,
  vitalQueries,
  vitalStatusLabel,
  vitalStatusTone,
  type BirthRecordDto,
} from '@/modules/mrd/vital';
import { BirthDialog, PrintVitalButton, ReportDialog } from '@/modules/mrd/VitalDialogs';
import { REPORT_WITHIN_DAYS } from '@/modules/mrd/vitalRules';

export const Route = createFileRoute('/_app/mrd/births/$birthId')({
  component: () => (
    <RequirePermission permission={BIRTH_READERS} what="births">
      <BirthPage />
    </RequirePermission>
  ),
});

const useStyles = makeStyles({
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

/** One birth: the report (Form 1, Form 3 for a still birth), its report to the Registrar, and the printed form. */
function BirthPage() {
  const { birthId } = Route.useParams();
  const birth = useQuery(vitalQueries.birth(birthId));

  if (birth.isLoading) {
    return <LoadingState label="Opening the birth…" />;
  }
  if (birth.error || !birth.data) {
    return (
      <Page maxWidth={900}>
        <ErrorState
          error={birth.error}
          onRetry={() => void birth.refetch()}
          title={isApiError(birth.error) && birth.error.status === 404 ? 'Birth not found' : undefined}
        />
      </Page>
    );
  }
  return <BirthView birth={birth.data} />;
}

type Dialog = 'correct' | 'report' | 'error' | null;

function BirthView({ birth }: { birth: BirthRecordDto }) {
  const styles = useStyles();
  // Closing a dialog returns focus to the button that opened it.
  const restoreFocusTarget = useRestoreFocusTarget();
  const navigate = useNavigate();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const canRecord = usePermission(VITAL_PERMISSIONS.birthsRecord);
  const canRegister = usePermission(VITAL_PERMISSIONS.register);
  const canMarkError = usePermission(BIRTH_READERS);
  const [dialog, setDialog] = useState<Dialog>(null);
  const recorded = birth.status === 'Recorded';
  const overdue = recorded && todayIso() > birth.dueBy;
  const still = birth.outcome === 'StillBirth';
  const form = still ? 'Form 3' : 'Form 1';

  const markError = useMutation({
    mutationFn: (reason: string) => vitalApi.birthInError(birth.id, reason, birth.version),
    onSuccess: (saved) => {
      queryClient.setQueryData(vitalKeys.birth(birth.id), saved);
      void queryClient.invalidateQueries({ queryKey: vitalKeys.registers('birth') });
      setDialog(null);
      notify.success('Marked as entered in error', `${saved.serialNumber} is kept, but out of the register.`);
    },
  });

  return (
    <Page>
      <PageHeader
        title={`${still ? 'Still birth' : 'Birth'} ${birth.serialNumber}`}
        subtitle={`Mother ${birth.motherName} · ${birth.motherUhid} · ${formatDateTime(birth.bornAt)}`}
        breadcrumbs={[
          { label: 'Births', onClick: () => void navigate({ to: '/mrd/births' }) },
          { label: birth.serialNumber },
        ]}
        actions={
          <StatusBadge
            status={birth.status}
            tone={vitalStatusTone[birth.status]}
            label={vitalStatusLabel[birth.status]}
          />
        }
      />
      {birth.status === 'EnteredInError' && (
        <MessageBar intent="info">
          <MessageBarBody>
            <MessageBarTitle>Entered in error</MessageBarTitle>
            {birth.errorReason
              ? `The entry is kept, but out of the birth register: ${birth.errorReason}`
              : 'The entry is kept, but out of the birth register.'}
          </MessageBarBody>
        </MessageBar>
      )}
      {recorded && (
        <MessageBar intent={overdue ? 'warning' : 'info'}>
          <MessageBarBody>
            <MessageBarTitle>{overdue ? 'Overdue' : 'Not yet reported'}</MessageBarTitle>
            {overdue
              ? `The ${REPORT_WITHIN_DAYS} days to report the birth to the Registrar ended on ${formatDateOnly(birth.dueBy)}. Report it now; a late registration needs a late fee.`
              : `Report the birth to the Registrar by ${formatDateOnly(birth.dueBy)}. It can be corrected until it is reported.`}
          </MessageBarBody>
        </MessageBar>
      )}
      <Section
        title={still ? 'Still birth report (Form 3)' : 'Birth report (Form 1)'}
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
            {canRecord && recorded && (
              <Button icon={<EditRegular />} onClick={() => setDialog('correct')} {...restoreFocusTarget}>
                Correct
              </Button>
            )}
            {canMarkError && recorded && (
              <Button icon={<DismissCircleRegular />} onClick={() => setDialog('error')} {...restoreFocusTarget}>
                Entered in error
              </Button>
            )}
            <PrintVitalButton path={birthDocument(birth.id)} label={`Print ${form}`} />
          </div>
        }
      >
        <div className={styles.facts}>
          <Fact label="Date and time of birth">{formatDateTime(birth.bornAt)}</Fact>
          <Fact label="Outcome">{birthOutcomeLabel[birth.outcome]}</Fact>
          <Fact label="Sex">{birthSexLabel[birth.sex]}</Fact>
          <Fact label="Name of the child">{still ? null : (birth.childName ?? 'Not yet named')}</Fact>
          <Fact label="Baby's record">{birth.babyUhid}</Fact>
          <Fact label="Mother">{`${birth.motherName} (${birth.motherUhid}), ${birth.motherAgeYears} years`}</Fact>
          <Fact label="Father">{birth.fatherName}</Fact>
          <Fact label="Informant">{birth.informantName}</Fact>
          <Fact label="Birth weight">{birth.weightGrams ? `${birth.weightGrams} g` : null}</Fact>
          <Fact label="Length of pregnancy">{birth.weeksOfGestation ? `${birth.weeksOfGestation} weeks` : null}</Fact>
          <Fact label="Method of delivery">{deliveryMethodLabel[birth.method]}</Fact>
          <Fact label="Attended by">{birthAttendantLabel[birth.attendant]}</Fact>
          <Fact label="Children born alive to the mother, including this one">
            {birth.liveChildren !== null ? String(birth.liveChildren) : null}
          </Fact>
          <Fact label="Cause of the foetal death">{birth.stillBirthCause}</Fact>
        </div>
      </Section>
      <Section title="Registration">
        <div className={styles.facts}>
          <Fact label="To be reported by">{formatDateOnly(birth.dueBy)}</Fact>
          <Fact label="Reported on">{birth.reportedOn ? formatDateOnly(birth.reportedOn) : 'Not yet reported'}</Fact>
          <Fact label="Registration no.">{birth.registrationNumber}</Fact>
        </div>
      </Section>
      {dialog === 'correct' && <BirthDialog existing={birth} onClose={() => setDialog(null)} />}
      {dialog === 'report' && <ReportDialog kind="birth" id={birth.id} onClose={() => setDialog(null)} />}
      <ReasonDialog
        open={dialog === 'error'}
        title={`Mark birth ${birth.serialNumber} as entered in error?`}
        description="For an entry made against the wrong mother, or twice. It is kept, but leaves the birth register and is not reported."
        label="Reason"
        confirmLabel="Entered in error"
        destructive
        busy={markError.isPending}
        error={markError.error}
        presets={['Recorded against the wrong mother', 'Recorded twice']}
        onConfirm={(reason) => markError.mutate(reason)}
        onCancel={() => {
          markError.reset();
          setDialog(null);
        }}
      />
    </Page>
  );
}
