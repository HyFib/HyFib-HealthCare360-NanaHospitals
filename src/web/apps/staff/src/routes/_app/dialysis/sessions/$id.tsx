import {
  Button,
  Caption1,
  makeStyles,
  mergeClasses,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  Text,
  tokens,
} from '@fluentui/react-components';
import {
  CalendarCancelRegular,
  CheckmarkCircleRegular,
  CircleRegular,
  DesktopPulseRegular,
  RecordRegular,
} from '@fluentui/react-icons';
import { formatDateOnly, useRealtimeEvent } from '@hc360/core';
import { LoadingState, Page, PageHeader, Section, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { dialysisApi, dialysisKeys, dialysisQueries, type DialysisSessionDetail } from '@/modules/dialysis/api';
import { SeroBadge, SessionStatusBadge } from '@/modules/dialysis/DialysisBadges';
import { AssignMachineDialog } from '@/modules/dialysis/DialysisDialogs';
import { seroLabel, shiftLabel } from '@/modules/dialysis/labels';
import { sessionStage, type SessionStage } from '@/modules/dialysis/logic';
import {
  CompletePanel,
  PreAssessmentPanel,
  ReadingsPanel,
  SessionSummary,
  StartPanel,
} from '@/modules/dialysis/SessionPanels';
import { ChargeStatusPanel, NoAccess, ReasonDialog, RecordLoadError } from '@/modules/ot/shared/feedback';
import { useFacilityId } from '@/modules/ot/shared/hooks';
import { SpecialtyPatientBanner } from '@/modules/ot/shared/SpecialtyPatientBanner';

export const Route = createFileRoute('/_app/dialysis/sessions/$id')({
  component: DialysisSessionPage,
});

const useStyles = makeStyles({
  stages: { display: 'flex', gap: '4px 20px', flexWrap: 'wrap', alignItems: 'center' },
  stage: { display: 'inline-flex', gap: '6px', alignItems: 'center', color: tokens.colorNeutralForeground3 },
  stageDone: { color: tokens.colorPaletteGreenForeground1 },
  stageCurrent: { color: tokens.colorBrandForeground1, fontWeight: tokens.fontWeightSemibold },
  badges: { display: 'inline-flex', gap: '6px', alignItems: 'center' },
  row: { display: 'flex', gap: '12px', alignItems: 'center', flexWrap: 'wrap' },
});

const STAGES: { id: SessionStage; label: string }[] = [
  { id: 'machine', label: 'Machine assigned' },
  { id: 'pre-assessment', label: 'Before dialysis' },
  { id: 'start', label: 'Connected' },
  { id: 'running', label: 'On dialysis' },
  { id: 'completed', label: 'Completed' },
];

function DialysisSessionPage() {
  const { id } = Route.useParams();
  const queryClient = useQueryClient();
  const facilityId = useFacilityId();
  const canRead = usePermission('dialysis.read');
  const session = useQuery({ ...dialysisQueries.session(id), enabled: canRead });

  useRealtimeEvent(facilityId ? `facility:${facilityId}` : null, 'dialysis.board.changed', () => {
    void queryClient.invalidateQueries({ queryKey: dialysisKeys.session(id) });
  });

  if (!canRead) {
    return (
      <Page>
        <NoAccess what="dialysis sessions" />
      </Page>
    );
  }
  if (session.isLoading) {
    return <LoadingState label="Loading the session…" />;
  }
  if (session.error || !session.data) {
    return (
      <Page maxWidth={900}>
        <RecordLoadError error={session.error} onRetry={() => void session.refetch()} what="dialysis session" />
      </Page>
    );
  }
  return <SessionView session={session.data} />;
}

function SessionView({ session: s }: { session: DialysisSessionDetail }) {
  const styles = useStyles();
  const navigate = useNavigate();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const canRecord = usePermission('dialysis.sessions.manage');
  const [assigning, setAssigning] = useState(false);
  const [cancelling, setCancelling] = useState(false);
  const stage = sessionStage(s);
  const stageIndex = STAGES.findIndex((x) => x.id === stage);

  const cancel = useMutation({
    mutationFn: ({ reason, missed }: { reason: string; missed: boolean }) => dialysisApi.cancel(s.id, reason, missed),
    onSuccess: (_, { missed }) => {
      setCancelling(false);
      notify.success(missed ? 'Recorded as missed' : 'Session cancelled');
      void queryClient.invalidateQueries({ queryKey: dialysisKeys.all });
    },
  });
  const retry = useMutation({
    mutationFn: () => dialysisApi.retryCharges(s.id),
    onSuccess: (outcome) => {
      if (outcome.status === 'Failed') {
        notify.warning('Billing is still not available', outcome.error ?? undefined);
      } else {
        notify.success('Session charge posted');
      }
      void queryClient.invalidateQueries({ queryKey: dialysisKeys.session(s.id) });
    },
    onError: (error) => notify.error(error, 'Could not retry billing'),
  });

  return (
    <Page>
      <SpecialtyPatientBanner
        patientId={s.patientId}
        fallback={{ uhid: s.uhid, fullName: s.patientName }}
        alerts={s.seroStatus && s.seroStatus !== 'Negative' ? [seroLabel[s.seroStatus]] : undefined}
      />
      <PageHeader
        title={`Haemodialysis · ${formatDateOnly(s.scheduledDate)}`}
        icon={<DesktopPulseRegular />}
        subtitle={`${shiftLabel[s.shift]} shift · session ${s.sessionNumber} · machine ${s.machineCode ?? 'not assigned'}${s.dryWeightKg !== null ? ` · dry weight ${s.dryWeightKg} kg` : ''}`}
        breadcrumbs={[
          {
            label: 'Dialysis board',
            onClick: () => void navigate({ to: '/dialysis', search: { date: s.scheduledDate } }),
          },
          { label: 'Sessions', onClick: () => void navigate({ to: '/dialysis/sessions' }) },
          { label: s.sessionNumber },
        ]}
        actions={
          <>
            <span className={styles.badges}>
              {s.seroStatus && <SeroBadge sero={s.seroStatus} long />}
              <SessionStatusBadge status={s.status} />
            </span>
            {canRecord && s.status === 'Scheduled' && (
              <>
                <Button appearance={s.machineId ? 'secondary' : 'primary'} onClick={() => setAssigning(true)}>
                  {s.machineId ? 'Change machine' : 'Assign machine'}
                </Button>
                <Button icon={<CalendarCancelRegular />} onClick={() => setCancelling(true)}>
                  Cancel or mark missed
                </Button>
              </>
            )}
          </>
        }
      />

      {stage !== 'closed' && (
        <div className={styles.stages} aria-label="Session progress">
          {STAGES.map((x, index) => {
            const done = index < stageIndex || stage === 'completed';
            const current = index === stageIndex && stage !== 'completed';
            return (
              <span
                key={x.id}
                className={mergeClasses(styles.stage, done && styles.stageDone, current && styles.stageCurrent)}
                aria-current={current ? 'step' : undefined}
              >
                {done ? (
                  <CheckmarkCircleRegular aria-label="Done" />
                ) : current ? (
                  <RecordRegular aria-label="Now" />
                ) : (
                  <CircleRegular aria-label="Not yet" />
                )}
                {x.label}
              </span>
            );
          })}
        </div>
      )}

      {(s.status === 'Cancelled' || s.status === 'Missed') && (
        <MessageBar intent={s.status === 'Missed' ? 'error' : 'warning'}>
          <MessageBarBody>
            <MessageBarTitle>{s.status === 'Missed' ? 'The patient did not come' : 'Cancelled'}</MessageBarTitle>
            {s.cancelReason}
          </MessageBarBody>
        </MessageBar>
      )}

      {s.status === 'Scheduled' && !s.machineId && (
        <MessageBar intent="info">
          <MessageBarBody>
            Assign a machine of the patient’s sero category (
            {s.seroStatus ? seroLabel[s.seroStatus].toLowerCase() : 'not known'}).
          </MessageBarBody>
        </MessageBar>
      )}

      <Section title="Before dialysis" description="Weight, blood pressure and pulse before connecting.">
        <PreAssessmentPanel session={s} canRecord={canRecord} />
      </Section>

      {s.status === 'Scheduled' && (
        <Section
          title="Connect and start"
          description="Checks the machine’s sero category, that it is free and disinfected, and the dialyzer’s owner and uses."
        >
          <StartPanel session={s} canRecord={canRecord} />
        </Section>
      )}

      {(s.status === 'InProgress' || s.status === 'Completed') && (
        <Section title="During dialysis" description="Hourly readings.">
          <ReadingsPanel session={s} canRecord={canRecord} />
        </Section>
      )}

      {s.status === 'InProgress' && canRecord && (
        <Section
          title="After dialysis"
          description="Post-dialysis weight and blood pressure, fluid removed and complications."
        >
          <CompletePanel session={s} canRecord={canRecord} />
        </Section>
      )}

      {s.status === 'Completed' && (
        <Section title="Summary">
          <SessionSummary session={s} />
          <div style={{ marginTop: 12 }}>
            <ChargeStatusPanel
              status={s.chargeStatus}
              error={s.chargeError}
              canRetry={canRecord}
              retrying={retry.isPending}
              onRetry={() => retry.mutate()}
            />
          </div>
        </Section>
      )}

      {s.status === 'Completed' && (
        <div className={styles.row}>
          <Caption1>Disinfect machine {s.machineCode ?? ''} before the next patient — record it on the board.</Caption1>
          <Button size="small" onClick={() => void navigate({ to: '/dialysis', search: { date: s.scheduledDate } })}>
            Open the board
          </Button>
        </div>
      )}

      {assigning && (
        <AssignMachineDialog
          session={{
            id: s.id,
            patientId: s.patientId,
            patientName: s.patientName,
            scheduledDate: s.scheduledDate,
            shift: s.shift,
            machineId: s.machineId,
          }}
          onClose={() => setAssigning(false)}
        />
      )}
      <ReasonDialog
        open={cancelling}
        title="Cancel this session?"
        description={
          <Text>
            Tick the box if the patient did not come: missed dialysis is a clinical risk and is tracked separately.
          </Text>
        }
        confirmLabel="Save"
        busy={cancel.isPending}
        error={cancel.error}
        option={{
          label: 'The patient did not come (record as missed)',
          hint: 'Leave unticked if the unit cancelled the session.',
        }}
        presets={['Patient did not come', 'Admitted to ICU', 'Machine breakdown', 'Rescheduled at patient request']}
        onConfirm={(reason, missed) => cancel.mutate({ reason, missed })}
        onCancel={() => {
          setCancelling(false);
          cancel.reset();
        }}
      />
    </Page>
  );
}
