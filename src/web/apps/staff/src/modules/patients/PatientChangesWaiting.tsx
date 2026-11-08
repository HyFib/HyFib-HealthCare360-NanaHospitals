import { Link, MessageBar, MessageBarBody, MessageBarTitle } from '@fluentui/react-components';
import { formatDateTime } from '@hc360/core';
import { useNavigate } from '@tanstack/react-router';
import type { PendingPatientChange } from './api';

/** Who decides each kind of change to the patient's record, in words. */
const decidedBy: Record<string, string> = {
  'patients.identity': 'Someone who approves identity changes (medical records) decides it',
  'patients.deceased': 'Someone who approves identity changes (medical records) decides it',
  'patients.restriction_lift': 'Someone else who may open restricted records decides it',
};

/**
 * "Change waiting" on a patient's record (maker-checker, slice 7): an identity change, a VIP restriction lifted, or the
 * deceased status set or taken back, asked for and not yet approved. Until one is approved the record, its banner,
 * wristband, labels and what ABDM is told keep the present values; the request opens from here for whoever asked or may
 * decide it.
 */
export function PatientChangesWaiting({ changes }: { changes: PendingPatientChange[] | null | undefined }) {
  const navigate = useNavigate();
  if (!changes || changes.length === 0) {
    return null;
  }
  return (
    <MessageBar intent="warning" layout="multiline">
      <MessageBarBody>
        <MessageBarTitle>Change waiting for approval</MessageBarTitle>
        {changes.map((c) => (
          <div key={c.approvalId}>
            <Link
              inline
              onClick={() => void navigate({ to: '/approvals/$approvalId', params: { approvalId: c.approvalId } })}
            >
              {c.summary}
            </Link>
            , asked for by {c.requestedBy} {formatDateTime(c.requestedAt)}.{' '}
            {decidedBy[c.kind] ?? 'Another person decides it'}; the record shows its present details until then.
          </div>
        ))}
      </MessageBarBody>
    </MessageBar>
  );
}
