import { Link, MessageBar, MessageBarBody, MessageBarTitle } from '@fluentui/react-components';
import { formatDateTime } from '@hc360/core';
import { useNavigate } from '@tanstack/react-router';

/**
 * A change asked for and waiting for a second person (maker-checker): what it changes, who asked and when, who decides,
 * and a link to the request, where the one who asked can withdraw it and the one who approves decides it. Access given
 * to a user or a role by default; a price says its own who decides.
 */
export function PendingGrantNotice({
  approvalId,
  what,
  requestedBy,
  requestedAt,
  decides = 'Another administrator who may approve grants decides it; until then it is not given.',
}: {
  approvalId: string;
  what: string;
  requestedBy: string;
  requestedAt: string;
  /** Who decides, and what holds until then. */
  decides?: string;
}) {
  const navigate = useNavigate();
  return (
    <MessageBar intent="warning" layout="multiline">
      <MessageBarBody>
        <MessageBarTitle>Waiting for approval</MessageBarTitle>
        {what}, asked for by {requestedBy} on {formatDateTime(requestedAt)}. {decides}{' '}
        <Link inline onClick={() => void navigate({ to: '/approvals/$approvalId', params: { approvalId } })}>
          Open the request
        </Link>
      </MessageBarBody>
    </MessageBar>
  );
}
