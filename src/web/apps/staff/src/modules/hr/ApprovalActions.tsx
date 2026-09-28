import { Button, Spinner, Tooltip } from '@fluentui/react-components';
import { CheckmarkRegular, DismissRegular } from '@fluentui/react-icons';
import { useNotify } from '@hc360/ui';
import { useState } from 'react';
import { ReasonDialog } from './shared/ReasonDialog';

/**
 * Approve (one click) and Reject (asks for the reason, which the employee sees) for a leave or attendance request.
 * `decide` performs the call and refreshes the lists; errors (own request, already decided, balance) are shown here.
 */
export function DecisionButtons({
  subject,
  decide,
  size = 'small',
}: {
  /** Used in labels and messages, e.g. "Priya Raman's leave". */
  subject: string;
  decide: (approve: boolean, note: string | null) => Promise<unknown>;
  size?: 'small' | 'medium';
}) {
  const notify = useNotify();
  const [busy, setBusy] = useState<'approve' | 'reject' | null>(null);
  const [rejecting, setRejecting] = useState(false);

  const run = async (approve: boolean, note: string | null) => {
    setBusy(approve ? 'approve' : 'reject');
    try {
      await decide(approve, note);
      notify.success(approve ? 'Approved' : 'Rejected', subject);
      setRejecting(false);
    } catch (error) {
      notify.error(error, approve ? 'Could not approve' : 'Could not reject');
    } finally {
      setBusy(null);
    }
  };

  return (
    <span style={{ display: 'inline-flex', gap: 4 }}>
      <Tooltip content={`Approve ${subject}`} relationship="label">
        <Button
          size={size}
          appearance="primary"
          icon={busy === 'approve' ? <Spinner size="extra-tiny" /> : <CheckmarkRegular />}
          disabled={busy !== null}
          onClick={() => void run(true, null)}
        >
          Approve
        </Button>
      </Tooltip>
      <Tooltip content={`Reject ${subject}`} relationship="label">
        <Button size={size} icon={<DismissRegular />} disabled={busy !== null} onClick={() => setRejecting(true)}>
          Reject
        </Button>
      </Tooltip>
      <ReasonDialog
        open={rejecting}
        title="Reject this request?"
        description={`The reason is shown to the employee (${subject}).`}
        label="Reason for rejecting"
        confirmLabel="Reject"
        destructive
        busy={busy === 'reject'}
        onConfirm={(note) => void run(false, note)}
        onCancel={() => setRejecting(false)}
      />
    </span>
  );
}
