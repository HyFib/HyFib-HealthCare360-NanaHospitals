import {
  Button,
  Caption1,
  makeStyles,
  mergeClasses,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  Spinner,
  Text,
  tokens,
} from '@fluentui/react-components';
import {
  CalendarArrowRightRegular,
  CheckmarkCircleRegular,
  CircleRegular,
  DismissCircleRegular,
  DoorArrowRightRegular,
  PersonAvailableRegular,
  PlayRegular,
  RecordRegular,
  SubtractCircleRegular,
} from '@fluentui/react-icons';
import { formatDateTime, formatTime } from '@hc360/core';
import { ConfirmDialog, Section, useNotify } from '@hc360/ui';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useState, type ReactNode } from 'react';
import { usePermission } from '@/lib/permissions';
import { otApi, otKeys, type SafetyPhase, type SurgeryDetail } from './api';
import { PreOpChecklistPanel, SafetyPhasePanel } from './ChecklistPanels';
import { completeGate, receiveGate, scheduleGate, startGate, surgerySteps, type StepId, type StepState } from './logic';
import { formatDuration, minutesBetween } from './shared/datetime';
import { ActionError, Detail, DetailGrid } from './shared/feedback';
import { useNow } from './shared/hooks';

const useStyles = makeStyles({
  layout: {
    display: 'grid',
    gridTemplateColumns: 'minmax(230px, 280px) minmax(0, 1fr)',
    gap: '16px',
    alignItems: 'start',
    '@media (max-width: 960px)': { gridTemplateColumns: 'minmax(0, 1fr)' },
  },
  rail: { display: 'flex', flexDirection: 'column', gap: '2px', position: 'sticky', top: '88px' },
  step: {
    display: 'grid',
    gridTemplateColumns: '24px minmax(0, 1fr)',
    gap: '2px 10px',
    alignItems: 'start',
    textAlign: 'start',
    width: '100%',
    padding: '8px 10px',
    border: 'none',
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: 'transparent',
    color: tokens.colorNeutralForeground1,
    cursor: 'pointer',
    ':hover': { backgroundColor: tokens.colorSubtleBackgroundHover },
    ':focus-visible': { outline: `2px solid ${tokens.colorStrokeFocus2}` },
  },
  selected: {
    backgroundColor: tokens.colorBrandBackground2,
    ':hover': { backgroundColor: tokens.colorBrandBackground2Hover },
  },
  icon: { fontSize: '22px', display: 'flex' },
  done: { color: tokens.colorPaletteGreenForeground1 },
  current: { color: tokens.colorBrandForeground1 },
  upcoming: { color: tokens.colorNeutralForeground4 },
  skipped: { color: tokens.colorNeutralForeground3 },
  stopped: { color: tokens.colorPaletteRedForeground1 },
  muted: { color: tokens.colorNeutralForeground3 },
  content: { display: 'flex', flexDirection: 'column', gap: '12px' },
  actionRow: { display: 'flex', gap: '12px', alignItems: 'center', flexWrap: 'wrap' },
  bigAction: { minHeight: '48px', fontSize: tokens.fontSizeBase400 },
});

function StepIcon({ state }: { state: StepState }) {
  const styles = useStyles();
  switch (state) {
    case 'done':
      return <CheckmarkCircleRegular className={mergeClasses(styles.icon, styles.done)} aria-label="Done" />;
    case 'current':
      return <RecordRegular className={mergeClasses(styles.icon, styles.current)} aria-label="Next to do" />;
    case 'skipped':
      return <SubtractCircleRegular className={mergeClasses(styles.icon, styles.skipped)} aria-label="Skipped" />;
    case 'stopped':
      return <DismissCircleRegular className={mergeClasses(styles.icon, styles.stopped)} aria-label="Stopped" />;
    default:
      return <CircleRegular className={mergeClasses(styles.icon, styles.upcoming)} aria-label="Not yet" />;
  }
}

const PHASE_OF: Partial<Record<StepId, SafetyPhase>> = {
  'sign-in': 'SignIn',
  'time-out': 'TimeOut',
  'sign-out': 'SignOut',
};

/**
 * The day of surgery as one guided sequence: booked → pre-op checklist → received → WHO Sign-in → Time-out →
 * incision → Sign-out → out of theatre. Each step shows who did it and when; steps the server would refuse are
 * explained instead of failing.
 */
export function SurgeryStepper({
  booking,
  selected,
  onSelect,
  onAdvance,
  onSchedule,
}: {
  booking: SurgeryDetail;
  selected: StepId;
  onSelect: (step: StepId) => void;
  /** Called after an action completes a step, so the view can move to the next one. */
  onAdvance: () => void;
  onSchedule: () => void;
}) {
  const styles = useStyles();
  const steps = surgerySteps(booking);
  const canChecklist = usePermission('ot.checklists.record');
  const phase = PHASE_OF[selected];

  let content: ReactNode;
  if (selected === 'schedule') {
    content = <ScheduleStep booking={booking} onSchedule={onSchedule} />;
  } else if (selected === 'pre-op') {
    content = <PreOpChecklistPanel booking={booking} canRecord={canChecklist} onComplete={onAdvance} />;
  } else if (selected === 'receive') {
    content = <ReceiveStep booking={booking} onDone={onAdvance} />;
  } else if (phase) {
    content = <SafetyPhasePanel booking={booking} phase={phase} canRecord={canChecklist} onComplete={onAdvance} />;
  } else if (selected === 'start') {
    content = <StartStep booking={booking} onDone={onAdvance} />;
  } else {
    content = <CompleteStep booking={booking} onDone={onAdvance} />;
  }

  const current = steps.find((s) => s.id === selected);

  return (
    <div className={styles.layout}>
      <nav aria-label="Day of surgery steps">
        <ol className={styles.rail} style={{ listStyle: 'none', margin: 0, padding: 0 }}>
          {steps.map((step) => (
            <li key={step.id}>
              <button
                type="button"
                className={mergeClasses(styles.step, step.id === selected && styles.selected)}
                aria-current={step.state === 'current' ? 'step' : undefined}
                aria-pressed={step.id === selected}
                onClick={() => onSelect(step.id)}
              >
                <StepIcon state={step.state} />
                <span>
                  <Text weight={step.state === 'current' ? 'bold' : 'semibold'} block>
                    {step.title}
                  </Text>
                  <Caption1 className={styles.muted} block>
                    {step.state === 'done' && step.at
                      ? formatTime(step.at)
                      : step.state === 'skipped'
                        ? 'Skipped'
                        : step.moment}
                  </Caption1>
                </span>
              </button>
            </li>
          ))}
        </ol>
      </nav>
      <Section title={current?.title} description={current?.moment}>
        <div className={styles.content}>{content}</div>
      </Section>
    </div>
  );
}

function ScheduleStep({ booking, onSchedule }: { booking: SurgeryDetail; onSchedule: () => void }) {
  const styles = useStyles();
  const canManage = usePermission('ot.bookings.manage');
  const gate = scheduleGate(booking);
  const hasSlot = booking.scheduledStart !== null && booking.status !== 'Requested' && booking.status !== 'Postponed';
  return (
    <>
      {hasSlot ? (
        <DetailGrid>
          <Detail label="Theatre">{booking.theatreName}</Detail>
          <Detail label="Starts">{formatDateTime(booking.scheduledStart)}</Detail>
          <Detail label="Ends">{formatDateTime(booking.scheduledEnd)}</Detail>
          <Detail label="Booked time">
            {booking.scheduledStart && booking.scheduledEnd
              ? formatDuration(minutesBetween(booking.scheduledStart, booking.scheduledEnd))
              : null}
          </Detail>
        </DetailGrid>
      ) : (
        <MessageBar intent="warning" layout="multiline">
          <MessageBarBody>
            <MessageBarTitle>
              {booking.status === 'Postponed' ? 'Postponed — needs a new slot' : 'Waiting for a theatre and time'}
            </MessageBarTitle>
            {booking.status === 'Postponed' && booking.postponeReason ? `Reason: ${booking.postponeReason}. ` : ''}
            {booking.preferredDate ? `Preferred date: ${booking.preferredDate}. ` : ''}
            The WHO checklist starts once the surgery is scheduled.
          </MessageBarBody>
        </MessageBar>
      )}
      {canManage && gate.allowed && (
        <div className={styles.actionRow}>
          <Button
            appearance={hasSlot ? 'secondary' : 'primary'}
            icon={<CalendarArrowRightRegular />}
            onClick={onSchedule}
          >
            {hasSlot ? 'Move to another slot' : 'Schedule surgery'}
          </Button>
        </div>
      )}
    </>
  );
}

function ReceiveStep({ booking, onDone }: { booking: SurgeryDetail; onDone: () => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const canRecord = usePermission('ot.checklists.record');
  const gate = receiveGate(booking);
  const receive = useMutation({
    mutationFn: () => otApi.receive(booking.id),
    onSuccess: () => {
      notify.success('Patient received in pre-op');
      void queryClient.invalidateQueries({ queryKey: otKeys.all });
      onDone();
    },
  });

  if (booking.receivedInPreOpAt) {
    return <Text>Received in the pre-op area at {formatDateTime(booking.receivedInPreOpAt)}.</Text>;
  }
  return (
    <>
      <Text>
        Confirm the identity band against the case sheet, then receive the patient into the pre-operative holding area.
      </Text>
      {!gate.allowed && (
        <MessageBar intent="info">
          <MessageBarBody>{gate.reason}</MessageBarBody>
        </MessageBar>
      )}
      <ActionError error={receive.error} />
      {canRecord && (
        <div className={styles.actionRow}>
          <Button
            appearance="primary"
            className={styles.bigAction}
            icon={receive.isPending ? <Spinner size="tiny" aria-hidden /> : <PersonAvailableRegular />}
            disabled={!gate.allowed || receive.isPending}
            onClick={() => receive.mutate()}
          >
            Receive patient in pre-op
          </Button>
        </div>
      )}
    </>
  );
}

function StartStep({ booking, onDone }: { booking: SurgeryDetail; onDone: () => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const canPerform = usePermission('ot.surgery.perform');
  const gate = startGate(booking);
  const [confirming, setConfirming] = useState(false);
  const now = useNow(30_000);
  const start = useMutation({
    mutationFn: () => otApi.start(booking.id),
    onSuccess: () => {
      setConfirming(false);
      notify.success('Surgery started', 'Incision time recorded.');
      void queryClient.invalidateQueries({ queryKey: otKeys.all });
      onDone();
    },
    onError: () => setConfirming(false),
  });

  if (booking.startedAt) {
    return (
      <Text>
        Incision at {formatDateTime(booking.startedAt)}
        {booking.status === 'InProgress'
          ? ` · ${formatDuration(minutesBetween(booking.startedAt, new Date(now).toISOString()))} ago`
          : ''}
        .
      </Text>
    );
  }
  return (
    <>
      <Text>Knife to skin. The incision time is recorded when you press the button.</Text>
      {!gate.allowed && (
        <MessageBar intent="warning" layout="multiline">
          <MessageBarBody>
            <MessageBarTitle>Surgery cannot start yet</MessageBarTitle>
            {gate.reason}
          </MessageBarBody>
        </MessageBar>
      )}
      <ActionError error={start.error} />
      {canPerform ? (
        <div className={styles.actionRow}>
          <Button
            appearance="primary"
            className={styles.bigAction}
            icon={<PlayRegular />}
            disabled={!gate.allowed || start.isPending}
            onClick={() => setConfirming(true)}
          >
            Start surgery (incision now)
          </Button>
        </div>
      ) : (
        <Caption1 className={styles.muted}>The operating surgeon records the start of surgery.</Caption1>
      )}
      <ConfirmDialog
        open={confirming}
        title="Start surgery now?"
        confirmLabel="Start surgery"
        busy={start.isPending}
        onConfirm={() => start.mutate()}
        onCancel={() => setConfirming(false)}
      >
        The WHO Sign-in and Time-out are complete. The incision time is recorded as now and cannot be changed.
      </ConfirmDialog>
    </>
  );
}

function CompleteStep({ booking, onDone }: { booking: SurgeryDetail; onDone: () => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const canPerform = usePermission('ot.surgery.perform');
  const gate = completeGate(booking);
  const [confirming, setConfirming] = useState(false);
  const complete = useMutation({
    mutationFn: () => otApi.complete(booking.id),
    onSuccess: () => {
      setConfirming(false);
      notify.success('Surgery completed', 'OT charges were posted to the bill.');
      void queryClient.invalidateQueries({ queryKey: otKeys.all });
      onDone();
    },
    onError: () => setConfirming(false),
  });

  if (booking.completedAt) {
    const start = booking.inRoomAt ?? booking.startedAt;
    return (
      <DetailGrid>
        <Detail label="Out of theatre">{formatDateTime(booking.completedAt)}</Detail>
        <Detail label="Time in theatre">
          {start ? formatDuration(minutesBetween(start, booking.completedAt)) : null}
        </Detail>
        <Detail label="Surgery (incision to end)">
          {booking.startedAt ? formatDuration(minutesBetween(booking.startedAt, booking.completedAt)) : null}
        </Detail>
      </DetailGrid>
    );
  }
  return (
    <>
      <Text>
        The patient leaves the theatre. Theatre time, procedure, surgeon and anaesthetist fees are posted to the bill.
      </Text>
      {!gate.allowed && (
        <MessageBar intent="warning" layout="multiline">
          <MessageBarBody>
            <MessageBarTitle>Not yet</MessageBarTitle>
            {gate.reason}
          </MessageBarBody>
        </MessageBar>
      )}
      <ActionError error={complete.error} />
      {canPerform ? (
        <div className={styles.actionRow}>
          <Button
            appearance="primary"
            className={styles.bigAction}
            icon={<DoorArrowRightRegular />}
            disabled={!gate.allowed || complete.isPending}
            onClick={() => setConfirming(true)}
          >
            Complete surgery (patient out)
          </Button>
        </div>
      ) : (
        <Caption1 className={styles.muted}>The operating surgeon completes the surgery.</Caption1>
      )}
      <ConfirmDialog
        open={confirming}
        title="Complete this surgery?"
        confirmLabel="Complete surgery"
        busy={complete.isPending}
        onConfirm={() => complete.mutate()}
        onCancel={() => setConfirming(false)}
      >
        The WHO Sign-out is complete. Wheels-out time is recorded as now and the OT charges are posted to the
        patient&apos;s bill.
      </ConfirmDialog>
    </>
  );
}
