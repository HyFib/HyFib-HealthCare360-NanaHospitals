import { Body1, Button, Caption1, MessageBar, MessageBarBody, Text, tokens } from '@fluentui/react-components';
import {
  ArrowUndoRegular,
  ChatMultipleRegular,
  CheckmarkCircleRegular,
  EditRegular,
  LockClosedRegular,
  PersonArrowRightRegular,
  PersonFeedbackRegular,
  PlayRegular,
} from '@fluentui/react-icons';
import { formatDateTime, formatPhone, isApiError, useSession } from '@hc360/core';
import { ErrorState, LoadingState, Page, PageHeader, Section, useNotify } from '@hc360/ui';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { useDepartments } from '@/lib/lookups';
import { usePermission } from '@/lib/permissions';
import { Patient360Link } from '@/modules/crm/Patient360Link';
import { feedbackApi, feedbackKeys, feedbackQueries, type ComplaintDetail } from '@/modules/feedback/api';
import { ComplaintStatusBadge, PriorityBadge } from '@/modules/feedback/badges';
import { ComplaintEditDialog } from '@/modules/feedback/ComplaintEditDialog';
import {
  categoryLabel,
  complaintSourceLabel,
  FEEDBACK_PERMISSIONS as F,
  type ComplaintStatus,
} from '@/modules/feedback/labels';
import { ResponseDrawer } from '@/modules/feedback/ResponseDrawer';
import {
  complaintMoveLabel,
  complaintMoves,
  complaintNoteRequired,
  deadlineText,
  SLA_HOURS,
  turnaroundText,
} from '@/modules/feedback/rules';
import { Detail, DetailGrid } from '@/modules/hr/shared/DetailGrid';
import { MainAside, Stack } from '@/modules/hr/shared/layout';
import { RequirePermission } from '@/modules/hr/shared/NoAccess';
import { ReasonDialog } from '@/modules/hr/shared/ReasonDialog';
import { UserName } from '@/modules/hr/shared/UserPicker';
import { patientQueries } from '@/modules/patients/api';

export const Route = createFileRoute('/_app/feedback/complaints/$complaintId')({
  component: () => (
    <RequirePermission permission={F.complaintsManage} what="complaint handling">
      <ComplaintPage />
    </RequirePermission>
  ),
});

function moveIcon(from: ComplaintStatus, to: ComplaintStatus) {
  if (from === 'Resolved' && to === 'InProgress') {
    return <ArrowUndoRegular />;
  }
  return to === 'Resolved' ? <CheckmarkCircleRegular /> : to === 'Closed' ? <LockClosedRegular /> : <PlayRegular />;
}

function ComplaintPage() {
  const { complaintId } = Route.useParams();
  const navigate = useNavigate();
  const complaint = useQuery(feedbackQueries.complaint(complaintId));
  const crumbs = [
    { label: 'Feedback analytics', onClick: () => void navigate({ to: '/feedback' }) },
    { label: 'Complaints', onClick: () => void navigate({ to: '/feedback/complaints' }) },
  ];

  if (!complaint.data) {
    return (
      <Page>
        <PageHeader title="Complaint" icon={<ChatMultipleRegular />} breadcrumbs={crumbs} />
        {complaint.error ? (
          <ErrorState error={complaint.error} onRetry={() => void complaint.refetch()} />
        ) : (
          <LoadingState />
        )}
      </Page>
    );
  }
  return <ComplaintView complaint={complaint.data} fetchedAt={complaint.dataUpdatedAt} crumbs={crumbs} />;
}

function ComplaintView({
  complaint: c,
  fetchedAt,
  crumbs,
}: {
  complaint: ComplaintDetail;
  /** When the details were loaded; the deadline text is worked out against it. */
  fetchedAt: number;
  crumbs: { label: string; onClick: () => void }[];
}) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const me = useSession((s) => s.user);
  const departments = useDepartments();
  const canReadFeedback = usePermission(F.read);
  const canReadPatient = usePermission('patients.read');
  const patient = useQuery({
    ...patientQueries.detail(c.patientId ?? ''),
    enabled: Boolean(c.patientId) && canReadPatient,
    retry: false,
    staleTime: 5 * 60_000,
  });
  const [moving, setMoving] = useState<ComplaintStatus | null>(null);
  const [busy, setBusy] = useState(false);
  const [editing, setEditing] = useState(false);
  const [responseOpen, setResponseOpen] = useState(false);

  const department = c.departmentId
    ? (departments.data?.find((d) => d.id === c.departmentId)?.name ?? 'Other department')
    : null;
  const open = c.status === 'Open' || c.status === 'InProgress';
  const due = deadlineText(c.dueAt, fetchedAt);
  const turnaround = c.resolvedAt ? (Date.parse(c.resolvedAt) - Date.parse(c.raisedAt)) / 3_600_000 : null;

  const refresh = () => void queryClient.invalidateQueries({ queryKey: feedbackKeys.complaints });

  const move = async (to: ComplaintStatus, note: string | null) => {
    setBusy(true);
    try {
      await feedbackApi.changeComplaintStatus(c.id, to, note);
      notify.success(
        to === 'Resolved'
          ? 'Complaint resolved'
          : to === 'Closed'
            ? 'Complaint closed'
            : c.status === 'Resolved'
              ? 'Complaint reopened'
              : 'Handling started',
        to === 'Resolved' && turnaroundTextFor(c) ? `Turnaround ${turnaroundTextFor(c)}.` : undefined,
      );
      setMoving(null);
      refresh();
      void queryClient.invalidateQueries({ queryKey: feedbackKeys.analytics });
    } catch (error) {
      if (isApiError(error) && error.code === 'complaint.invalid_transition') {
        notify.warning(
          'The complaint has moved on',
          'Someone else changed its stage. The latest details have been loaded.',
        );
        setMoving(null);
        refresh();
      } else {
        notify.error(error, 'Could not change the stage');
      }
    } finally {
      setBusy(false);
    }
  };

  const assignToMe = async () => {
    if (!me) return;
    setBusy(true);
    try {
      await feedbackApi.updateComplaint(c.id, {
        ownerUserId: me.id,
        priority: c.priority,
        departmentId: c.departmentId,
        category: c.category,
        version: c.version,
      });
      notify.success('You own this complaint now');
      refresh();
    } catch (error) {
      notify.error(error, 'Could not take the complaint');
      refresh();
    } finally {
      setBusy(false);
    }
  };

  const moves = complaintMoves(c.status);

  return (
    <Page>
      <PageHeader
        title={
          <span style={{ display: 'inline-flex', gap: 8, alignItems: 'center', flexWrap: 'wrap' }}>
            <span style={{ fontFamily: tokens.fontFamilyMonospace }}>{c.complaintNumber}</span>
            <ComplaintStatusBadge status={c.status} isOverdue={c.isOverdue} />
            <PriorityBadge priority={c.priority} />
          </span>
        }
        subtitle={`${categoryLabel(c.category)} · ${complaintSourceLabel[c.source]} · raised ${formatDateTime(c.raisedAt)}`}
        icon={<ChatMultipleRegular />}
        breadcrumbs={crumbs}
        actions={
          <>
            {open && c.ownerUserId !== me?.id && (
              <Button icon={<PersonArrowRightRegular />} onClick={() => void assignToMe()} disabled={busy}>
                Assign to me
              </Button>
            )}
            {moves.map((to) => {
              const primary =
                (c.status === 'Resolved' && to === 'Closed') || (c.status !== 'Resolved' && to === 'Resolved');
              return (
                <Button
                  key={to}
                  appearance={primary ? 'primary' : 'secondary'}
                  icon={moveIcon(c.status, to)}
                  disabled={busy}
                  onClick={() => (complaintNoteRequired(c.status, to) ? setMoving(to) : void move(to, null))}
                >
                  {complaintMoveLabel(c.status, to)}
                </Button>
              );
            })}
          </>
        }
      />

      <MainAside>
        <Stack>
          <Section title="What the complainant said">
            <Body1 as="p" style={{ whiteSpace: 'pre-wrap', overflowWrap: 'anywhere', margin: 0 }}>
              {c.description}
            </Body1>
            {c.feedbackResponseId && canReadFeedback && (
              <Button icon={<PersonFeedbackRegular />} style={{ marginTop: 12 }} onClick={() => setResponseOpen(true)}>
                Open the feedback response
              </Button>
            )}
          </Section>
          {c.resolution && (
            <Section title="Resolution">
              <Body1 as="p" style={{ whiteSpace: 'pre-wrap', overflowWrap: 'anywhere', margin: 0 }}>
                {c.resolution}
              </Body1>
              {c.resolvedAt && <Caption1>Resolved {formatDateTime(c.resolvedAt)}</Caption1>}
            </Section>
          )}
          {c.status === 'Resolved' && (
            <MessageBar intent="info">
              <MessageBarBody>
                Close the complaint once the complainant is satisfied, or reopen it if the problem is not fixed.
              </MessageBarBody>
            </MessageBar>
          )}
        </Stack>
        <Stack>
          <Section title="Deadline">
            <DetailGrid minColumnWidth={160}>
              {open ? (
                <Detail label="Resolve by">
                  <span style={{ color: due.overdue ? tokens.colorPaletteRedForeground1 : undefined, fontWeight: 600 }}>
                    {due.text}
                  </span>
                  <Caption1 block>{formatDateTime(c.dueAt)}</Caption1>
                </Detail>
              ) : (
                <Detail label="Turnaround">{turnaround === null ? null : turnaroundText(turnaround)}</Detail>
              )}
              <Detail label="Allowed">{SLA_HOURS[c.priority]} hours</Detail>
              {c.closedAt && <Detail label="Closed">{formatDateTime(c.closedAt)}</Detail>}
              {c.reopenCount > 0 && (
                <Detail label="Reopened">{c.reopenCount === 1 ? 'Once' : `${c.reopenCount} times`}</Detail>
              )}
            </DetailGrid>
          </Section>
          <Section
            title="Handling"
            actions={
              <Button size="small" icon={<EditRegular />} onClick={() => setEditing(true)} disabled={busy}>
                Change
              </Button>
            }
          >
            <DetailGrid minColumnWidth={160}>
              <Detail label="Owner">{c.ownerUserId ? <UserName id={c.ownerUserId} /> : 'Not assigned'}</Detail>
              <Detail label="Priority">{c.priority}</Detail>
              <Detail label="Department">{department}</Detail>
              <Detail label="About">{categoryLabel(c.category)}</Detail>
            </DetailGrid>
          </Section>
          <Section title="Complainant">
            <DetailGrid minColumnWidth={160}>
              <Detail label="Name">{c.contactName ?? patient.data?.fullName}</Detail>
              <Detail label="Phone">{c.contactPhone ? formatPhone(c.contactPhone) : null}</Detail>
              {c.patientId && (
                <Detail label="Patient" wide>
                  <span style={{ display: 'inline-flex', gap: 8, alignItems: 'center', flexWrap: 'wrap' }}>
                    <Text>
                      {patient.data ? `${patient.data.fullName} · ${patient.data.uhid}` : 'Registered patient'}
                    </Text>
                    <Patient360Link patientId={c.patientId} size="small" appearance="subtle" />
                  </span>
                </Detail>
              )}
            </DetailGrid>
            {!c.contactPhone && !c.patientId && (
              <Caption1>No contact details were recorded (for example an anonymous QR response).</Caption1>
            )}
          </Section>
        </Stack>
      </MainAside>

      <ReasonDialog
        open={moving !== null}
        title={moving === 'Resolved' ? 'Mark the complaint resolved' : 'Reopen the complaint'}
        description={
          moving === 'Resolved'
            ? 'Describe what was done. The resolution is kept with the complaint and counts for the turnaround time.'
            : 'Say why it is being reopened. The deadline stays as it was.'
        }
        label={moving === 'Resolved' ? 'How was it resolved?' : 'Why reopen it?'}
        maxLength={2000}
        confirmLabel={moving === 'Resolved' ? 'Mark resolved' : 'Reopen'}
        busy={busy}
        onConfirm={(note) => moving && void move(moving, note)}
        onCancel={() => setMoving(null)}
      />
      <ComplaintEditDialog open={editing} onClose={() => setEditing(false)} complaint={c} />
      <ResponseDrawer responseId={responseOpen ? c.feedbackResponseId : null} onClose={() => setResponseOpen(false)} />
    </Page>
  );
}

/** Turnaround so far, for the "resolved" message. */
function turnaroundTextFor(c: ComplaintDetail): string | null {
  const hours = (Date.now() - Date.parse(c.raisedAt)) / 3_600_000;
  return Number.isFinite(hours) && hours >= 0 ? turnaroundText(hours) : null;
}
