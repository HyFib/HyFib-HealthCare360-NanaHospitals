import {
  Accordion,
  AccordionHeader,
  AccordionItem,
  AccordionPanel,
  Badge,
  Button,
  Caption1,
  makeStyles,
  Menu,
  MenuItem,
  MenuList,
  MenuPopover,
  MenuTrigger,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  ProgressBar,
  Text,
  tokens,
} from '@fluentui/react-components';
import {
  ArrowClockwiseRegular,
  CalendarAddRegular,
  CalendarArrowRightRegular,
  CheckmarkRegular,
  ClipboardTaskListLtrRegular,
  DismissCircleRegular,
  MoreHorizontalRegular,
  NotepadRegular,
  PersonProhibitedRegular,
} from '@fluentui/react-icons';
import { formatDateOnly, formatDateTime, useRealtimeEvent } from '@hc360/core';
import { DataTable, LoadingState, Page, PageHeader, Section, StatusBadge, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { ChargeBadge, Detail, DetailGrid, NoAccess, ReasonDialog, RecordLoadError } from '@/modules/ot/shared/feedback';
import { useFacilityId } from '@/modules/ot/shared/hooks';
import { SpecialtyPatientBanner } from '@/modules/ot/shared/SpecialtyPatientBanner';
import { TrendChart } from '@/modules/ot/shared/TrendChart';
import {
  physioApi,
  physioKeys,
  physioQueries,
  type PhysioReferralDetail,
  type PhysioSessionDto,
} from '@/modules/physio/api';
import {
  assessmentTypeLabel,
  modalityLabel,
  MRC_GRADES,
  orderPriorityLabel,
  orderPriorityTone,
  physioSessionStatusLabel,
  physioSessionStatusTone,
  referralStatusLabel,
  referralStatusTone,
} from '@/modules/physio/labels';
import { canCancelReferral, nextStep, painChange, planProgress } from '@/modules/physio/logic';
import {
  AssessmentDialog,
  AttendDialog,
  RescheduleDialog,
  ScheduleSessionsDialog,
  TreatmentPlanDialog,
} from '@/modules/physio/PhysioDialogs';

export const Route = createFileRoute('/_app/physio/referrals/$id')({
  component: PhysioReferralPage,
});

const useStyles = makeStyles({
  stack: { display: 'flex', flexDirection: 'column', gap: '12px' },
  badges: { display: 'inline-flex', gap: '6px', alignItems: 'center' },
  muted: { color: tokens.colorNeutralForeground3 },
  pre: { whiteSpace: 'pre-wrap' },
  tableSmall: { width: '100%', borderCollapse: 'collapse' },
  actions: { display: 'inline-flex', gap: '4px' },
});

type Dialog = 'assess' | 'plan' | 'schedule' | 'complete' | 'cancel' | null;

function PhysioReferralPage() {
  const { id } = Route.useParams();
  const queryClient = useQueryClient();
  const facilityId = useFacilityId();
  const canRead = usePermission('physio.read');
  const referral = useQuery({ ...physioQueries.referral(id), enabled: canRead });

  useRealtimeEvent(facilityId ? `facility:${facilityId}` : null, 'physio.worklist.changed', () => {
    void queryClient.invalidateQueries({ queryKey: physioKeys.referral(id) });
  });

  if (!canRead) {
    return (
      <Page>
        <NoAccess what="physiotherapy records" />
      </Page>
    );
  }
  if (referral.isLoading) {
    return <LoadingState label="Loading the referral…" />;
  }
  if (referral.error || !referral.data) {
    return (
      <Page maxWidth={900}>
        <RecordLoadError error={referral.error} onRetry={() => void referral.refetch()} what="physiotherapy referral" />
      </Page>
    );
  }
  return <ReferralView referral={referral.data} />;
}

function ReferralView({ referral: r }: { referral: PhysioReferralDetail }) {
  const styles = useStyles();
  const navigate = useNavigate();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const canTreat = usePermission('physio.treatment.manage');
  const canRefer = usePermission('physio.referrals.manage');
  const [dialog, setDialog] = useState<Dialog>(null);
  const [attending, setAttending] = useState<PhysioSessionDto | null>(null);
  const [moving, setMoving] = useState<PhysioSessionDto | null>(null);
  const [closing, setClosing] = useState<PhysioSessionDto | null>(null);
  const step = nextStep(r);
  const progress = planProgress(r.plan, r.sessions);
  const open = r.status !== 'Completed' && r.status !== 'Cancelled';

  const invalidate = () => void queryClient.invalidateQueries({ queryKey: physioKeys.all });
  const complete = useMutation({
    mutationFn: (summary: string) => physioApi.completeReferral(r.id, summary),
    onSuccess: () => {
      setDialog(null);
      notify.success('Referral completed');
      invalidate();
    },
  });
  const cancel = useMutation({
    mutationFn: (reason: string) => physioApi.cancelReferral(r.id, reason),
    onSuccess: () => {
      setDialog(null);
      notify.success('Referral cancelled');
      invalidate();
    },
  });
  const close = useMutation({
    mutationFn: ({ id, reason, missed }: { id: string; reason: string; missed: boolean }) =>
      physioApi.close(id, reason, missed),
    onSuccess: (_, { missed }) => {
      setClosing(null);
      notify.success(missed ? 'Recorded as missed' : 'Session cancelled');
      invalidate();
    },
  });
  const retry = useMutation({
    mutationFn: (sessionId: string) => physioApi.retryCharges(sessionId),
    onSuccess: (outcome) => {
      if (outcome.status === 'Failed') {
        notify.warning('Billing is still not available', outcome.error ?? undefined);
      } else {
        notify.success('Session charges posted');
      }
      invalidate();
    },
    onError: (error) => notify.error(error, 'Could not retry billing'),
  });

  const primary =
    canTreat && open
      ? step.action === 'assess'
        ? { label: 'Record assessment', icon: <ClipboardTaskListLtrRegular />, dialog: 'assess' as const }
        : step.action === 'plan'
          ? { label: 'Write treatment plan', icon: <NotepadRegular />, dialog: 'plan' as const }
          : step.action === 'schedule'
            ? { label: 'Book sessions', icon: <CalendarAddRegular />, dialog: 'schedule' as const }
            : step.action === 'complete'
              ? { label: 'Complete referral', icon: <CheckmarkRegular />, dialog: 'complete' as const }
              : null
      : null;

  const attended = r.sessions.filter((s) => s.status === 'Attended');
  const painSeries = [
    { name: 'Pain before', points: attended.map((s) => ({ at: s.attendedAt ?? s.scheduledAt, value: s.painBefore })) },
    { name: 'Pain after', points: attended.map((s) => ({ at: s.attendedAt ?? s.scheduledAt, value: s.painAfter })) },
  ];

  return (
    <Page>
      <SpecialtyPatientBanner
        summary={r.patient}
        patientId={r.patientId}
        fallback={{ uhid: '', fullName: 'Patient' }}
      />
      <PageHeader
        title="Physiotherapy referral"
        subtitle={`Referred ${formatDateTime(r.referredAt)}${r.referredByName ? ` by ${r.referredByName}` : ''}${r.orderNumber ? ` · order ${r.orderNumber}` : ''}${r.assignedTherapistName ? ` · therapist ${r.assignedTherapistName}` : ''}`}
        breadcrumbs={[
          { label: 'Worklist', onClick: () => void navigate({ to: '/physio' }) },
          { label: 'Referrals', onClick: () => void navigate({ to: '/physio/referrals' }) },
          { label: r.patient?.fullName ?? 'Referral' },
        ]}
        actions={
          <>
            <span className={styles.badges}>
              <Badge appearance="tint" color={orderPriorityTone[r.priority]}>
                {orderPriorityLabel[r.priority]}
              </Badge>
              <StatusBadge
                status={r.status}
                tone={referralStatusTone[r.status]}
                label={referralStatusLabel[r.status]}
              />
            </span>
            {primary && (
              <Button appearance="primary" icon={primary.icon} onClick={() => setDialog(primary.dialog)}>
                {primary.label}
              </Button>
            )}
            {open && (canTreat || canRefer) && (
              <Menu>
                <MenuTrigger disableButtonEnhancement>
                  <Button icon={<MoreHorizontalRegular />} aria-label="More actions" />
                </MenuTrigger>
                <MenuPopover>
                  <MenuList>
                    {canTreat && (
                      <MenuItem icon={<ClipboardTaskListLtrRegular />} onClick={() => setDialog('assess')}>
                        Record assessment
                      </MenuItem>
                    )}
                    {canTreat && r.assessments.length > 0 && (
                      <MenuItem icon={<NotepadRegular />} onClick={() => setDialog('plan')}>
                        {r.plan ? 'Revise treatment plan' : 'Write treatment plan'}
                      </MenuItem>
                    )}
                    {canTreat && r.plan && progress.remaining > 0 && (
                      <MenuItem icon={<CalendarAddRegular />} onClick={() => setDialog('schedule')}>
                        Book sessions
                      </MenuItem>
                    )}
                    {canTreat && (r.status === 'Assessed' || r.status === 'InTreatment') && (
                      <MenuItem icon={<CheckmarkRegular />} onClick={() => setDialog('complete')}>
                        Complete referral
                      </MenuItem>
                    )}
                    {canRefer && canCancelReferral(r) && (
                      <MenuItem icon={<DismissCircleRegular />} onClick={() => setDialog('cancel')}>
                        Cancel referral
                      </MenuItem>
                    )}
                  </MenuList>
                </MenuPopover>
              </Menu>
            )}
          </>
        }
      />

      <MessageBar intent={step.action === 'closed' ? (r.status === 'Completed' ? 'success' : 'warning') : 'info'}>
        <MessageBarBody>
          <MessageBarTitle>{step.action === 'closed' ? referralStatusLabel[r.status] : 'Next'}</MessageBarTitle>
          {step.message}
          {r.status === 'Cancelled' && r.cancelReason ? ` Reason: ${r.cancelReason}` : ''}
        </MessageBarBody>
      </MessageBar>

      <Section title="Referral">
        <DetailGrid>
          <Detail label="Reason">{r.reason}</Detail>
          <Detail label="Service ordered">{r.serviceName}</Detail>
          <Detail label="Sessions prescribed">{r.sessionsOrdered}</Detail>
          <Detail label="Encounter">
            {r.admissionId ? 'In-patient (charges go to the IP bill)' : r.visitId ? 'Out-patient visit' : null}
          </Detail>
          {r.dischargeSummary && <Detail label="Discharge summary">{r.dischargeSummary}</Detail>}
          {r.closedAt && <Detail label="Closed">{formatDateTime(r.closedAt)}</Detail>}
        </DetailGrid>
      </Section>

      <Section title={`Assessments (${r.assessments.length})`}>
        {r.assessments.length === 0 ? (
          <Text className={styles.muted}>Not assessed yet.</Text>
        ) : (
          <Accordion multiple collapsible defaultOpenItems={[r.assessments.at(-1)?.id ?? '']}>
            {[...r.assessments].reverse().map((a) => (
              <AccordionItem key={a.id} value={a.id}>
                <AccordionHeader>
                  {assessmentTypeLabel[a.type]} · {formatDateTime(a.assessedAt)}
                  {a.assessedByName ? ` · ${a.assessedByName}` : ''}
                  {a.painScore !== null ? ` · pain ${a.painScore}/10` : ''}
                </AccordionHeader>
                <AccordionPanel>
                  <div className={styles.stack}>
                    <DetailGrid>
                      <Detail label="Chief complaint">{a.chiefComplaint}</Detail>
                      <Detail label="Pain">
                        {a.painScore !== null
                          ? `${a.painScore}/10${a.painLocation ? ` · ${a.painLocation}` : ''}`
                          : a.painLocation}
                      </Detail>
                      <Detail label="Functional status">{a.functionalStatus}</Detail>
                      <Detail label="Goals">{a.goals}</Detail>
                      <Detail label="Notes">{a.notes}</Detail>
                    </DetailGrid>
                    {a.rangeOfMotion.length > 0 && (
                      <Detail label="Range of motion">
                        {a.rangeOfMotion
                          .map(
                            (m) =>
                              `${m.joint} ${m.movement} (${m.side === 'NotApplicable' ? '—' : m.side}): active ${m.activeDegrees ?? '—'}°, passive ${m.passiveDegrees ?? '—'}°`,
                          )
                          .join('; ')}
                      </Detail>
                    )}
                    {a.strength.length > 0 && (
                      <Detail label="Muscle power (MRC)">
                        {a.strength
                          .map(
                            (m) =>
                              `${m.muscleGroup} (${m.side === 'NotApplicable' ? '—' : m.side}): ${MRC_GRADES[m.mrcGrade]?.label ?? m.mrcGrade}`,
                          )
                          .join('; ')}
                      </Detail>
                    )}
                  </div>
                </AccordionPanel>
              </AccordionItem>
            ))}
          </Accordion>
        )}
      </Section>

      <Section title="Treatment plan" description={r.plan ? `Revision ${r.plan.revision}` : undefined}>
        {!r.plan ? (
          <Text className={styles.muted}>
            {r.assessments.length ? 'No plan yet.' : 'The plan is written after the first assessment.'}
          </Text>
        ) : (
          <div className={styles.stack}>
            <DetailGrid>
              <Detail label="Modalities">{r.plan.modalities.map((m) => modalityLabel[m]).join(', ')}</Detail>
              <Detail label="Frequency">{`${r.plan.sessionsPerWeek} a week · ${r.plan.sessionMinutes} min`}</Detail>
              <Detail label="Start">{formatDateOnly(r.plan.startDate)}</Detail>
              <Detail label="Precautions">{r.plan.precautions}</Detail>
              <Detail label="Goals">{r.plan.goals}</Detail>
            </DetailGrid>
            {r.plan.exerciseProgramme && (
              <Detail label="Exercise programme">
                <span className={styles.pre}>{r.plan.exerciseProgramme}</span>
              </Detail>
            )}
            <div>
              <Text>
                {progress.attended} of {progress.planned} sessions given ·{' '}
                {progress.booked - progress.attended - progress.missed} booked ahead · {progress.missed} missed ·{' '}
                {progress.remaining} left to book
              </Text>
              <ProgressBar
                value={progress.planned ? progress.attended / progress.planned : 0}
                thickness="large"
                aria-label="Sessions given of the plan"
              />
            </div>
          </div>
        )}
      </Section>

      <Section
        title={`Sessions (${r.sessions.length})`}
        actions={
          canTreat &&
          open &&
          r.plan &&
          progress.remaining > 0 && (
            <Button icon={<CalendarAddRegular />} onClick={() => setDialog('schedule')}>
              Book sessions
            </Button>
          )
        }
        flush
      >
        <DataTable
          ariaLabel="Physiotherapy sessions"
          rows={r.sessions}
          getRowId={(s) => s.id}
          size="small"
          columns={[
            { key: 'n', header: '#', width: 50, render: (s) => s.sessionNumber },
            { key: 'when', header: 'When', render: (s) => formatDateTime(s.scheduledAt) },
            {
              key: 'status',
              header: 'Status',
              render: (s) => (
                <StatusBadge
                  status={s.status}
                  tone={physioSessionStatusTone[s.status]}
                  label={physioSessionStatusLabel[s.status]}
                />
              ),
            },
            {
              key: 'given',
              header: 'Given',
              priority: 'low',
              render: (s) =>
                s.modalitiesGiven.length
                  ? s.modalitiesGiven.map((m) => modalityLabel[m]).join(', ')
                  : (s.missedReason ?? '—'),
            },
            {
              key: 'pain',
              header: 'Pain',
              priority: 'low',
              render: (s) => {
                const change = painChange(s.painBefore, s.painAfter);
                return s.painBefore !== null || s.painAfter !== null
                  ? `${s.painBefore ?? '—'} → ${s.painAfter ?? '—'}${change !== null && change !== 0 ? ` (${change > 0 ? '+' : ''}${change})` : ''}`
                  : '—';
              },
            },
            { key: 'note', header: 'Progress note', priority: 'low', render: (s) => s.progressNote ?? '' },
            { key: 'by', header: 'Therapist', priority: 'low', render: (s) => s.therapistName ?? '—' },
            {
              key: 'bill',
              header: 'Billing',
              priority: 'low',
              render: (s) => (s.status === 'Attended' ? <ChargeBadge status={s.chargeStatus} /> : '—'),
            },
          ]}
          rowActions={
            canTreat
              ? (s) =>
                  s.status === 'Scheduled' && open ? (
                    <span className={styles.actions}>
                      <Button
                        size="small"
                        appearance="primary"
                        icon={<CheckmarkRegular />}
                        onClick={() => setAttending(s)}
                      >
                        Record
                      </Button>
                      <Button
                        size="small"
                        appearance="subtle"
                        icon={<CalendarArrowRightRegular />}
                        aria-label={`Move session ${s.sessionNumber}`}
                        onClick={() => setMoving(s)}
                      />
                      <Button
                        size="small"
                        appearance="subtle"
                        icon={<PersonProhibitedRegular />}
                        aria-label={`Close session ${s.sessionNumber}`}
                        onClick={() => setClosing(s)}
                      />
                    </span>
                  ) : s.chargeStatus === 'Failed' ? (
                    <Button
                      size="small"
                      icon={<ArrowClockwiseRegular />}
                      disabled={retry.isPending}
                      onClick={() => retry.mutate(s.id)}
                    >
                      Retry billing
                    </Button>
                  ) : null
              : undefined
          }
          emptyTitle="No sessions booked"
          emptyDescription={r.plan ? 'Book the sessions of the plan.' : 'Sessions are booked once the plan is written.'}
        />
      </Section>

      {attended.length > 1 && (
        <Section title="Pain over the course of treatment" description="Numeric rating scale, 0 (no pain) to 10.">
          <TrendChart
            series={painSeries}
            ariaLabel="Pain scores before and after each session"
            yMin={0}
            yMax={10}
            yName="pain"
            height={220}
          />
          <Caption1 className={styles.muted}>The values are listed in the sessions table above.</Caption1>
        </Section>
      )}

      {dialog === 'assess' && <AssessmentDialog referral={r} onClose={() => setDialog(null)} />}
      {dialog === 'plan' && <TreatmentPlanDialog referral={r} onClose={() => setDialog(null)} />}
      {dialog === 'schedule' && <ScheduleSessionsDialog referral={r} onClose={() => setDialog(null)} />}
      {attending && (
        <AttendDialog
          session={attending}
          plan={r.plan}
          patientName={r.patient?.fullName ?? 'Patient'}
          onClose={() => setAttending(null)}
        />
      )}
      {moving && (
        <RescheduleDialog sessionId={moving.id} scheduledAt={moving.scheduledAt} onClose={() => setMoving(null)} />
      )}
      <ReasonDialog
        open={dialog === 'complete'}
        title="Complete this referral?"
        description="Write the discharge summary: treatment given, outcome and home advice. Remaining booked sessions should be closed first."
        label="Discharge summary"
        minLength={10}
        maxLength={2000}
        confirmLabel="Complete referral"
        busy={complete.isPending}
        error={complete.error}
        onConfirm={(summary) => complete.mutate(summary)}
        onCancel={() => {
          setDialog(null);
          complete.reset();
        }}
      />
      <ReasonDialog
        open={dialog === 'cancel'}
        title="Cancel this referral?"
        description="Only possible before any treatment was given. Booked sessions are closed."
        confirmLabel="Cancel referral"
        destructive
        busy={cancel.isPending}
        error={cancel.error}
        presets={['Patient declined physiotherapy', 'Referred elsewhere', 'Ordered by mistake', 'Patient discharged']}
        onConfirm={(reason) => cancel.mutate(reason)}
        onCancel={() => {
          setDialog(null);
          cancel.reset();
        }}
      />
      <ReasonDialog
        open={closing !== null}
        title={`Close session ${closing?.sessionNumber ?? ''}?`}
        confirmLabel="Save"
        busy={close.isPending}
        error={close.error}
        option={{ label: 'The patient did not come (record as missed)', defaultChecked: true }}
        presets={['Patient did not come', 'Patient unwell', 'Therapist unavailable']}
        onConfirm={(reason, missed) => closing && close.mutate({ id: closing.id, reason, missed })}
        onCancel={() => {
          setClosing(null);
          close.reset();
        }}
      />
    </Page>
  );
}
