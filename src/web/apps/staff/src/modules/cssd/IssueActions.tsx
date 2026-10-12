import {
  Button,
  Caption1,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Field,
  Input,
  MessageBar,
  MessageBarBody,
  Spinner,
  Switch,
  tokens,
} from '@fluentui/react-components';
import { isApiError } from '@hc360/core';
import { useNotify } from '@hc360/ui';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { ReasonDialog } from '@/modules/inpatient/ReasonDialog';
import { fromIstInput, toIstInput } from '@/modules/inpatient/time';
import { PatientPicker, type PickedPatient } from '@/modules/patients/PatientPicker';
import { cssdApi, cssdKeys, type SetIssueDto } from './api';

/** Link an issued pack to the patient it was used on (traceability for recalls). */
export function RecordUseDialog({ issue, onClose }: { issue: SetIssueDto; onClose: () => void }) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [patient, setPatient] = useState<PickedPatient | null>(null);
  const [earlier, setEarlier] = useState(false);
  const [limits] = useState(() => ({ now: toIstInput(new Date()) }));
  const [usedAt, setUsedAt] = useState(limits.now);
  const record = useMutation({
    mutationFn: () =>
      cssdApi.recordUse(issue.id, { patientId: patient!.id, usedAt: earlier ? fromIstInput(usedAt) : null }),
    onSuccess: () => {
      notify.success(`${issue.packNumber ?? 'Pack'} recorded as used`, patient ? `On ${patient.fullName}.` : undefined);
      void queryClient.invalidateQueries({ queryKey: cssdKeys.all });
      onClose();
    },
  });
  const valid = patient !== null && (!earlier || fromIstInput(usedAt) !== null);

  return (
    <Dialog open onOpenChange={(_, d) => !d.open && !record.isPending && onClose()}>
      <DialogSurface style={{ maxWidth: 520 }}>
        <form
          onSubmit={(event) => {
            event.preventDefault();
            if (valid && !record.isPending) {
              record.mutate();
            }
          }}
        >
          <DialogBody>
            <DialogTitle>
              Record use of {issue.packNumber ?? 'pack'}
              <Caption1 block style={{ color: tokens.colorNeutralForeground3 }}>
                {issue.setName} · {issue.departmentName}
              </Caption1>
            </DialogTitle>
            <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
              <PatientPicker value={patient} onChange={setPatient} label="Used on patient" required />
              <Switch
                checked={earlier}
                onChange={(_, d) => setEarlier(d.checked)}
                label="Used earlier (within 7 days)"
              />
              {earlier && (
                <Field label="Used at" required>
                  <Input
                    type="datetime-local"
                    value={usedAt}
                    max={limits.now}
                    onChange={(_, d) => setUsedAt(d.value)}
                  />
                </Field>
              )}
              {record.error && (
                <MessageBar intent="error" layout="multiline">
                  <MessageBarBody>
                    {isApiError(record.error) ? record.error.userMessage : 'The use could not be recorded.'}
                  </MessageBarBody>
                </MessageBar>
              )}
            </DialogContent>
            <DialogActions>
              <Button onClick={onClose} disabled={record.isPending}>
                Cancel
              </Button>
              <Button
                type="submit"
                appearance="primary"
                disabled={!valid || record.isPending}
                icon={record.isPending ? <Spinner size="tiny" aria-hidden /> : undefined}
              >
                Record use
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
      </DialogSurface>
    </Dialog>
  );
}

/** Confirm what was done with a recalled pack (returned, patient informed, infection control notified). */
export function AcknowledgeRecallDialog({ issue, onClose }: { issue: SetIssueDto | null; onClose: () => void }) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const acknowledge = useMutation({
    mutationFn: (action: string) => cssdApi.acknowledgeRecall(issue!.id, action),
    onSuccess: () => {
      notify.success('Recall acknowledged');
      void queryClient.invalidateQueries({ queryKey: cssdKeys.all });
      onClose();
    },
  });
  return (
    <ReasonDialog
      open={issue !== null}
      title={issue ? `Acknowledge recall of ${issue.packNumber ?? 'pack'}` : 'Acknowledge recall'}
      description={
        issue?.patientId
          ? 'This pack was used on a patient. Record that infection control has been informed and what follow-up was arranged.'
          : 'Record what was done with the pack, for example returned unopened to CSSD.'
      }
      label="Action taken"
      placeholder="e.g. Pack returned to CSSD unopened; OT in charge informed"
      minLength={5}
      maxLength={500}
      confirmLabel="Acknowledge"
      busy={acknowledge.isPending}
      error={acknowledge.error}
      onConfirm={(action) => acknowledge.mutate(action)}
      onCancel={() => {
        acknowledge.reset();
        onClose();
      }}
    />
  );
}
