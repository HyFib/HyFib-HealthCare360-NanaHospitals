import { Link, MessageBar, MessageBarBody, MessageBarTitle } from '@fluentui/react-components';
import { formatDateTime } from '@hc360/core';
import { useQuery } from '@tanstack/react-query';
import { useNavigate } from '@tanstack/react-router';
import { approvalQueries } from './api';

/**
 * The changes waiting for a second person's approval on one record (a bill's credit note, a patient's merge, a lab
 * report's correction), so the desk does not ask twice and sees why nothing has changed yet. The server lists only what
 * the user asked for or may decide.
 */
export function PendingApprovals({
  subjectType,
  subjectId,
}: {
  subjectType: 'Bill' | 'Patient' | 'LabReport';
  subjectId: string;
}) {
  const navigate = useNavigate();
  const requests = useQuery({
    ...approvalQueries.list({ view: 'Subject', status: 'Pending', subjectType, subjectId }),
    // While one waits, ask again every 30 s: it may be decided, withdrawn or lapse on another workstation.
    refetchInterval: (query) => (query.state.data?.items.some((r) => !r.isExpired) ? 30_000 : false),
  });
  const waiting = (requests.data?.items ?? []).filter((r) => !r.isExpired);
  if (waiting.length === 0) {
    return null;
  }
  return (
    <MessageBar intent="info" layout="multiline">
      <MessageBarBody>
        <MessageBarTitle>Waiting for approval</MessageBarTitle>
        {waiting.map((r) => (
          <div key={r.id}>
            <Link inline onClick={() => void navigate({ to: '/approvals/$approvalId', params: { approvalId: r.id } })}>
              {r.summary}
            </Link>
            , asked for by {r.requestedByName} {formatDateTime(r.requestedAt)}. Nothing changes until another person
            approves it.
          </div>
        ))}
      </MessageBarBody>
    </MessageBar>
  );
}
