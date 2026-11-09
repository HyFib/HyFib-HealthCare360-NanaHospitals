import { ReasonDialog } from '../common/ReasonDialog';

/**
 * Asks why a lab safety setting is loosened (a critical limit widened or removed, automatic verification switched on),
 * where the lab asks for a second pathologist: the reason goes with the request the other pathologist decides.
 */
export function SecondPathologistReasonDialog({
  open,
  what,
  busy,
  onConfirm,
  onCancel,
}: {
  open: boolean;
  what: string;
  busy?: boolean;
  onConfirm: (reason: string) => void;
  onCancel: () => void;
}) {
  return (
    <ReasonDialog
      open={open}
      title="A second pathologist decides this"
      description={`${what} loosens a lab safety setting, so it waits for another pathologist's approval. Say why.`}
      label="Why"
      minLength={10}
      maxLength={500}
      confirmLabel="Ask for approval"
      busy={busy}
      onConfirm={onConfirm}
      onCancel={onCancel}
    />
  );
}
