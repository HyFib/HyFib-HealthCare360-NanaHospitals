import {
  Badge,
  Button,
  Caption1,
  Divider,
  DrawerBody,
  DrawerHeader,
  DrawerHeaderTitle,
  Field,
  Input,
  makeStyles,
  Menu,
  MenuItem,
  MenuList,
  MenuPopover,
  MenuTrigger,
  MessageBar,
  MessageBarBody,
  OverlayDrawer,
  Radio,
  RadioGroup,
  Select,
  Spinner,
  Text,
  Textarea,
  tokens,
  useRestoreFocusTarget,
} from '@fluentui/react-components';
import {
  CallRegular,
  CommentRegular,
  DismissCircleRegular,
  DismissRegular,
  EditRegular,
  LinkRegular,
  MoreHorizontalRegular,
  SendRegular,
} from '@fluentui/react-icons';
import { formatDateOnly, formatDateTime, formatPhone, isApiError, todayIso } from '@hc360/core';
import {
  CloseRequestScope,
  ErrorState,
  LoadingState,
  StatusBadge,
  useCloseRequests,
  useNotify,
  useUnsavedChanges,
  useValuesChanged,
} from '@hc360/ui';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { Link } from '@tanstack/react-router';
import { useEffect, useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { SendFeedbackLinkDialog } from '@/modules/feedback/SendFeedbackLinkDialog';
import { StaffFeedbackDialog } from '@/modules/feedback/StaffFeedbackDialog';
import { addDays } from '@/modules/hr/shared/dates';
import { Detail, DetailGrid } from '@/modules/hr/shared/DetailGrid';
import { ReasonDialog } from '@/modules/hr/shared/ReasonDialog';
import { UserPicker, UserName } from '@/modules/hr/shared/UserPicker';
import { useUserName, type PickedUser } from '@/modules/hr/shared/users';
import { crmApi, crmKeys, crmQueries, type FollowUpDetail } from './api';
import {
  FOLLOW_UP_OUTCOMES,
  followUpOutcomeLabel,
  followUpSourceLabel,
  followUpStatusLabel,
  followUpTypeLabel,
  type FollowUpOutcome,
  type FollowUpPriority,
} from './labels';
import { Patient360Link } from './Patient360Link';
import { MAX_UNREACHABLE_ATTEMPTS, outcomeClosesTask } from './rules';

const useStyles = makeStyles({
  body: { display: 'flex', flexDirection: 'column', gap: '16px', paddingBottom: '16px' },
  call: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: '12px',
    flexWrap: 'wrap',
    padding: '12px 14px',
    borderRadius: tokens.borderRadiusLarge,
    backgroundColor: tokens.colorBrandBackground2,
  },
  phone: {
    fontSize: tokens.fontSizeBase500,
    fontWeight: tokens.fontWeightSemibold,
    fontFamily: tokens.fontFamilyMonospace,
  },
  outcomes: { display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(200px, 1fr))' },
  row: { display: 'flex', gap: '8px', flexWrap: 'wrap', alignItems: 'center' },
  muted: { color: tokens.colorNeutralForeground3 },
});

/**
 * One follow-up call: the full number (opening it is recorded as patient-record access), the call outcome, and the
 * follow-on actions (SMS reminder, reschedule, cancel, record feedback or send the feedback link).
 */
export function FollowUpDrawer({ followUpId, onClose }: { followUpId: string | null; onClose: () => void }) {
  const followUp = useQuery({ ...crmQueries.followUp(followUpId ?? ''), enabled: followUpId !== null });
  // A call outcome being typed is not thrown away without asking (SRS UX-08).
  const closing = useCloseRequests({ onClose });
  return (
    <OverlayDrawer open={followUpId !== null} position="end" size="medium" onOpenChange={closing.onOpenChange}>
      <DrawerHeader>
        <DrawerHeaderTitle
          action={
            <Button appearance="subtle" icon={<DismissRegular />} aria-label="Close" onClick={closing.requestClose} />
          }
        >
          {followUp.data ? followUp.data.patientName : 'Follow-up call'}
        </DrawerHeaderTitle>
      </DrawerHeader>
      <DrawerBody>
        {followUp.isLoading && <LoadingState />}
        {followUp.error && <ErrorState error={followUp.error} onRetry={() => void followUp.refetch()} />}
        <CloseRequestScope value={closing}>
          {followUp.data && (
            <FollowUpBody key={`${followUp.data.id}:${followUp.data.version}`} task={followUp.data} onClose={onClose} />
          )}
        </CloseRequestScope>
      </DrawerBody>
    </OverlayDrawer>
  );
}

function FollowUpBody({ task, onClose }: { task: FollowUpDetail; onClose: () => void }) {
  const styles = useStyles();
  // Closing a dialog opened from the menu returns focus to the menu button, inside the drawer.
  const restoreFocusTarget = useRestoreFocusTarget();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const canFeedback = usePermission(['feedback.manage', 'crm.followups.manage']);
  const open = task.status === 'Open' || task.status === 'InProgress';
  const [outcome, setOutcome] = useState<FollowUpOutcome | ''>('');
  const [notes, setNotes] = useState('');
  const [nextDate, setNextDate] = useState(addDays(todayIso(), 1));
  const [busy, setBusy] = useState<string | null>(null);
  const [editing, setEditing] = useState(false);
  const [cancelling, setCancelling] = useState(false);
  const [capturing, setCapturing] = useState(false);
  const [sendingLink, setSendingLink] = useState(false);
  const [editChanged, setEditChanged] = useState(false);
  const closes = outcome ? outcomeClosesTask(outcome, task.attempts) : false;
  const guard = useUnsavedChanges({
    dirty: useValuesChanged({ outcome, notes, nextDate }) || (editing && editChanged),
    busy: busy !== null,
    onClose,
  });

  const refresh = () => void queryClient.invalidateQueries({ queryKey: crmKeys.followUps });

  const record = async () => {
    if (!outcome) {
      return;
    }
    setBusy('record');
    try {
      await crmApi.recordAttempt(task.id, {
        outcome,
        notes: notes.trim() || null,
        nextDueDate: closes ? null : nextDate,
      });
      notify.success(
        closes ? 'Call recorded; task done' : 'Call recorded',
        closes ? undefined : `Next call on ${formatDateOnly(nextDate)}.`,
      );
      refresh();
      if (closes) {
        onClose();
      }
    } catch (error) {
      notify.error(error, 'Could not record the call');
    } finally {
      setBusy(null);
    }
  };

  const remind = async () => {
    setBusy('remind');
    try {
      await crmApi.remindFollowUp(task.id);
      notify.success('Reminder SMS queued', 'It goes out through the SMS gateway shortly.');
      refresh();
    } catch (error) {
      notify.error(
        error,
        isApiError(error) && error.code === 'followup.no_consent'
          ? 'The patient has not agreed to SMS updates'
          : 'Could not send the reminder',
      );
    } finally {
      setBusy(null);
    }
  };

  const cancel = async (reason: string) => {
    setBusy('cancel');
    try {
      await crmApi.cancelFollowUp(task.id, reason);
      notify.success('Follow-up cancelled');
      setCancelling(false);
      refresh();
      onClose();
    } catch (error) {
      notify.error(error, 'Could not cancel the follow-up');
    } finally {
      setBusy(null);
    }
  };

  return (
    <div className={styles.body}>
      <div className={styles.row}>
        <Badge appearance="outline">{task.uhid}</Badge>
        <StatusBadge status={task.status} label={followUpStatusLabel[task.status]} />
        {task.priority === 'High' && <Badge color="danger">High priority</Badge>}
        {task.isOverdue && open && <Badge color="warning">Overdue</Badge>}
        <Patient360Link patientId={task.patientId} size="small" appearance="subtle" />
      </div>

      {task.phone ? (
        <div className={styles.call}>
          <span>
            <Caption1 block>Mobile</Caption1>
            <span className={styles.phone}>{formatPhone(task.phone)}</span>
          </span>
          <Button as="a" href={`tel:${task.phone}`} appearance="primary" icon={<CallRegular />}>
            Call
          </Button>
        </div>
      ) : (
        <MessageBar intent="warning">
          <MessageBarBody>
            No mobile number on record. Update the patient's record, or cancel this follow-up.
          </MessageBarBody>
        </MessageBar>
      )}

      <DetailGrid minColumnWidth={160}>
        <Detail label="Reason">{followUpTypeLabel[task.type]}</Detail>
        <Detail label="Due">{formatDateOnly(task.dueDate)}</Detail>
        <Detail label="Calls so far">{task.attempts}</Detail>
        <Detail label="With">
          {task.assigneeUserId ? <UserName id={task.assigneeUserId} /> : 'Anyone on the team'}
        </Detail>
        <Detail label="Created">{followUpSourceLabel[task.source]}</Detail>
        {task.reminderSentAt && <Detail label="Reminder SMS">{formatDateTime(task.reminderSentAt)}</Detail>}
        {task.outcome && <Detail label="Last outcome">{followUpOutcomeLabel[task.outcome]}</Detail>}
        {task.notes && (
          <Detail label="Notes" wide>
            {task.notes}
          </Detail>
        )}
        {task.outcomeNotes && (
          <Detail label="Last call notes" wide>
            {task.outcomeNotes}
          </Detail>
        )}
        {task.completedAt && <Detail label="Completed">{formatDateTime(task.completedAt)}</Detail>}
        <Detail label="Patient">
          <Link to="/patients/$patientId" params={{ patientId: task.patientId }}>
            Open the patient's record
          </Link>
        </Detail>
      </DetailGrid>

      {open && (
        <>
          <Divider />
          <Text weight="semibold">How did the call go?</Text>
          <RadioGroup
            value={outcome}
            onChange={(_, d) => setOutcome(d.value as FollowUpOutcome)}
            className={styles.outcomes}
            aria-label="Call outcome"
          >
            {FOLLOW_UP_OUTCOMES.map((o) => (
              <Radio key={o} value={o} label={followUpOutcomeLabel[o]} />
            ))}
          </RadioGroup>
          {outcome === 'NotReachable' && (
            <Caption1 className={styles.muted}>
              After {MAX_UNREACHABLE_ATTEMPTS} unanswered calls the task closes by itself. This will be call{' '}
              {task.attempts + 1}.
            </Caption1>
          )}
          <Field label="Notes">
            <Textarea value={notes} onChange={(_, d) => setNotes(d.value)} rows={2} maxLength={1000} />
          </Field>
          {outcome && !closes && (
            <Field label="Call again on">
              <Input type="date" value={nextDate} min={todayIso()} onChange={(_, d) => setNextDate(d.value)} />
            </Field>
          )}
          <div className={styles.row}>
            <Button
              appearance="primary"
              disabled={!outcome || busy !== null}
              icon={busy === 'record' ? <Spinner size="tiny" aria-hidden /> : undefined}
              onClick={() => void record()}
            >
              {closes ? 'Save and close task' : 'Save call'}
            </Button>
            <Button
              icon={busy === 'remind' ? <Spinner size="tiny" aria-hidden /> : <SendRegular />}
              disabled={busy !== null}
              onClick={() => void remind()}
            >
              {task.reminderSentAt ? 'Send the reminder again' : 'Send SMS reminder'}
            </Button>
            <Menu>
              <MenuTrigger disableButtonEnhancement>
                <Button icon={<MoreHorizontalRegular />} aria-label="More actions" {...restoreFocusTarget} />
              </MenuTrigger>
              <MenuPopover>
                <MenuList>
                  <MenuItem icon={<EditRegular />} onClick={() => setEditing(true)}>
                    Reschedule or reassign
                  </MenuItem>
                  {canFeedback && (
                    <MenuItem icon={<CommentRegular />} onClick={() => setCapturing(true)}>
                      Record feedback from this call
                    </MenuItem>
                  )}
                  {canFeedback && task.visitId && (
                    <MenuItem icon={<LinkRegular />} onClick={() => setSendingLink(true)}>
                      Send the feedback link by SMS
                    </MenuItem>
                  )}
                  <MenuItem icon={<DismissCircleRegular />} onClick={() => setCancelling(true)}>
                    Cancel this follow-up
                  </MenuItem>
                </MenuList>
              </MenuPopover>
            </Menu>
          </div>
        </>
      )}

      {editing && (
        <EditTask
          task={task}
          onChanged={setEditChanged}
          onDone={() => {
            setEditing(false);
            setEditChanged(false);
          }}
        />
      )}

      <ReasonDialog
        open={cancelling}
        title="Cancel this follow-up?"
        label="Reason"
        confirmLabel="Cancel follow-up"
        destructive
        busy={busy === 'cancel'}
        onConfirm={(reason) => void cancel(reason)}
        onCancel={() => setCancelling(false)}
      />
      {capturing && (
        <StaffFeedbackDialog
          open
          onClose={() => setCapturing(false)}
          patient={{ id: task.patientId, uhid: task.uhid, fullName: task.patientName }}
          visitId={task.visitId}
        />
      )}
      {sendingLink && task.visitId && (
        <SendFeedbackLinkDialog
          open
          onClose={() => setSendingLink(false)}
          patient={{ id: task.patientId, uhid: task.uhid, fullName: task.patientName }}
          visitId={task.visitId}
        />
      )}
      {guard.dialogs}
    </div>
  );
}

function EditTask({
  task,
  onChanged,
  onDone,
}: {
  task: FollowUpDetail;
  /** Whether the changes typed here are unsaved, for the drawer's own question on closing. */
  onChanged: (changed: boolean) => void;
  onDone: () => void;
}) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [dueDate, setDueDate] = useState(task.dueDate);
  const [priority, setPriority] = useState<FollowUpPriority>(task.priority);
  const currentName = useUserName(task.assigneeUserId);
  // undefined = unchanged, so the current assignee's name can load after the form opens.
  const [assignee, setAssignee] = useState<PickedUser | null | undefined>(undefined);
  const shownAssignee =
    assignee !== undefined
      ? assignee
      : task.assigneeUserId
        ? { id: task.assigneeUserId, name: currentName ?? '…' }
        : null;
  const [notes, setNotes] = useState(task.notes ?? '');
  const [busy, setBusy] = useState(false);
  const changed = useValuesChanged({ dueDate, priority, assignee, notes });
  useEffect(() => onChanged(changed), [changed, onChanged]);
  const guard = useUnsavedChanges({ dirty: changed, busy, onClose: onDone });

  const save = async () => {
    setBusy(true);
    try {
      await crmApi.updateFollowUp(task.id, {
        dueDate,
        assigneeUserId: assignee === undefined ? task.assigneeUserId : (assignee?.id ?? null),
        priority,
        notes: notes.trim() || null,
        version: task.version,
      });
      notify.success('Follow-up updated');
      void queryClient.invalidateQueries({ queryKey: crmKeys.followUps });
      onDone();
    } catch (error) {
      notify.error(error, 'Could not update the follow-up');
    } finally {
      setBusy(false);
    }
  };

  return (
    <div
      style={{
        display: 'flex',
        flexDirection: 'column',
        gap: 10,
        padding: 12,
        borderRadius: 8,
        border: `1px solid ${tokens.colorNeutralStroke2}`,
      }}
    >
      <Text weight="semibold">Reschedule or reassign</Text>
      <Field label="Due on">
        <Input type="date" value={dueDate} min={todayIso()} onChange={(_, d) => setDueDate(d.value)} />
      </Field>
      <Field label="Priority">
        <Select value={priority} onChange={(_, d) => setPriority(d.value as FollowUpPriority)}>
          <option value="Normal">Normal</option>
          <option value="High">High</option>
        </Select>
      </Field>
      <UserPicker label="Assign to" value={shownAssignee} onChange={setAssignee} />
      <Field label="Notes">
        <Textarea value={notes} onChange={(_, d) => setNotes(d.value)} rows={2} maxLength={1000} />
      </Field>
      <div style={{ display: 'flex', gap: 8 }}>
        <Button
          appearance="primary"
          disabled={busy || !dueDate}
          icon={busy ? <Spinner size="tiny" aria-hidden /> : undefined}
          onClick={() => void save()}
        >
          Save
        </Button>
        <Button onClick={guard.requestClose} disabled={busy}>
          Cancel
        </Button>
      </div>
      {guard.dialogs}
    </div>
  );
}
