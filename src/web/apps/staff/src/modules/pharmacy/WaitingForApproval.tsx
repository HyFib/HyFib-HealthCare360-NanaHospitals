import { Link, MessageBar, MessageBarBody, MessageBarTitle } from '@fluentui/react-components';
import { formatDateTime } from '@hc360/core';
import { useNavigate } from '@tanstack/react-router';
import type { PendingApproval } from './api';

/**
 * A cancellation, a return or a ward return waiting for a second person, on the invoice or the issue, for everyone who
 * opens it: nothing has moved yet (no refund, no stock back, no credit), so nobody asks twice or hands money over. The
 * request opens for whoever asked it, and for whoever may decide it (`canDecide`).
 */
export function WaitingForApproval({
  pending,
  decidedBy,
  canDecide,
}: {
  pending: readonly PendingApproval[] | null | undefined;
  /** Who decides, in words: "someone who approves refunds", "another pharmacist". */
  decidedBy: string;
  canDecide: boolean;
}) {
  const navigate = useNavigate();
  if (!pending || pending.length === 0) {
    return null;
  }
  return (
    <MessageBar intent="info" layout="multiline">
      <MessageBarBody>
        <MessageBarTitle>Asked for approval</MessageBarTitle>
        {pending.map((p) => (
          <div key={p.approvalId}>
            {p.askedByYou || canDecide ? (
              <Link
                inline
                onClick={() => void navigate({ to: '/approvals/$approvalId', params: { approvalId: p.approvalId } })}
              >
                {p.summary}
              </Link>
            ) : (
              p.summary
            )}
            , asked for by {p.requestedBy} {formatDateTime(p.requestedAt)}. Nothing changes until {decidedBy} approves
            it.
          </div>
        ))}
      </MessageBarBody>
    </MessageBar>
  );
}
