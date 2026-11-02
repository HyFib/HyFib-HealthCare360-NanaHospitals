import { Link, MessageBar, MessageBarBody, MessageBarTitle } from '@fluentui/react-components';
import { formatDateTime } from '@hc360/core';
import { useNavigate } from '@tanstack/react-router';

/**
 * Access asked for and waiting for a second administrator: what it gives, who asked and when, and a link to the
 * request, where the one who asked can withdraw it and the one who approves decides it.
 */
export function PendingGrantNotice({
  approvalId,
  what,
  requestedBy,
  requestedAt,
}: {
  approvalId: string;
  what: string;
  requestedBy: string;
  requestedAt: string;
}) {
  const navigate = useNavigate();
  return (
    <MessageBar intent="warning" layout="multiline">
      <MessageBarBody>
        <MessageBarTitle>Waiting for approval</MessageBarTitle>
        {what}, asked for by {requestedBy} on {formatDateTime(requestedAt)}. Another administrator who may approve
        grants decides it; until then it is not given.{' '}
        <Link inline onClick={() => void navigate({ to: '/approvals/$approvalId', params: { approvalId } })}>
          Open the request
        </Link>
      </MessageBarBody>
    </MessageBar>
  );
}
