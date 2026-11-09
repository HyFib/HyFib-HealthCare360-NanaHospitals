import { useNotify } from '@hc360/ui';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { ReasonDialog } from '@/modules/inpatient/ReasonDialog';
import { ambulanceApi, ambulanceKeys, type AmbulanceTrip } from './api';

/**
 * Waiving a completed trip's charge (maker-checker, slice 15): asked for with the reason, it waits for someone who
 * approves waivers, never the one who asked, and the charge stays on the bill until then. Where the hospital lets the
 * desk waive alone, it is made at once.
 */
export function WaiveDialog({ trip, onClose }: { trip: AmbulanceTrip; onClose: () => void }) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const waive = useMutation({
    mutationFn: (reason: string) => ambulanceApi.waive(trip.id, reason, trip.version),
    onSuccess: (result) => {
      void queryClient.invalidateQueries({ queryKey: ambulanceKeys.all });
      if ('kind' in result) {
        notify.success(
          'Waiver asked for',
          `${result.summary}. The charge stays on the bill until someone who approves waivers decides.`,
        );
      } else {
        notify.success('Charge waived', `${result.number} is not charged.`);
      }

      onClose();
    },
  });

  return (
    <ReasonDialog
      open
      title={`Waive the charge of ${trip.number}?`}
      description={`${trip.patientName}, ${trip.kilometres ?? 0} km. The waiver waits for someone who approves waivers (not you); the charge stays on the patient's bill until then.`}
      label="Why the charge is waived"
      placeholder="e.g. Free transport under the state scheme"
      minLength={5}
      maxLength={300}
      confirmLabel="Ask to waive"
      busy={waive.isPending}
      error={waive.error}
      onConfirm={(reason) => waive.mutate(reason)}
      onCancel={onClose}
    />
  );
}
