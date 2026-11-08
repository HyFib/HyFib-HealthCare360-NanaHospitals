import {
  Button,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Field,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  Radio,
  RadioGroup,
  Spinner,
  Textarea,
} from '@fluentui/react-components';
import { formatDateOnly } from '@hc360/core';
import { useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { ActionError } from '@/modules/ot/shared/feedback';
import {
  dialysisApi,
  dialysisKeys,
  dialysisQueries,
  SERO_STATUSES,
  type DialysisProfileDto,
  type SeroStatus,
} from './api';
import { seroLabel } from './labels';
import { infectionStatusWaits, infectionTested } from './logic';

/**
 * Changing a dialysis patient's infection status (maker-checker, slice 15): only on a verified HBsAg, HCV or HIV result
 * of the patient from the last 90 days, for the infection concerned. A sero-negative patient found positive is isolated
 * at once; taking a patient off a positive status waits for a second person, never the one who asked.
 */
export function InfectionStatusDialog({ profile, onClose }: { profile: DialysisProfileDto; onClose: () => void }) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [status, setStatus] = useState<SeroStatus | null>(null);
  const [resultId, setResultId] = useState<string | null>(null);
  const [reason, setReason] = useState('');
  const results = useQuery(dialysisQueries.infectionResults(profile.patientId));
  const tested = status ? infectionTested(profile.seroStatus, status) : null;
  const matching = (results.data ?? []).filter((r) => r.infection === tested);
  const waits = status !== null && infectionStatusWaits(profile.seroStatus, status);
  const change = useMutation({
    mutationFn: () =>
      dialysisApi.changeInfectionStatus(profile.patientId, {
        seroStatus: status!,
        labResultId: resultId!,
        reason: reason.trim(),
        version: profile.version,
      }),
    onSuccess: (asked) => {
      if (asked) {
        notify.success(
          'Change asked for',
          `${asked.summary}. It waits for a second person; the patient stays as they are until then.`,
        );
      } else {
        notify.success(
          'Infection status changed',
          `The patient is now ${seroLabel[status!].toLowerCase()}: only machines of that category can be assigned.`,
        );
      }

      void queryClient.invalidateQueries({ queryKey: dialysisKeys.all });
      onClose();
    },
  });
  const ready = status !== null && resultId !== null && reason.trim().length >= 5;

  return (
    <Dialog open onOpenChange={(_, d) => !d.open && !change.isPending && onClose()}>
      <DialogSurface style={{ maxWidth: 600 }}>
        <form
          onSubmit={(e) => {
            e.preventDefault();
            if (ready) {
              change.mutate();
            }
          }}
          noValidate
        >
          <DialogBody>
            <DialogTitle>Change infection status</DialogTitle>
            <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
              <Field label={`Now ${seroLabel[profile.seroStatus].toLowerCase()}. Change to`} required>
                <RadioGroup
                  value={status ?? ''}
                  onChange={(_, d) => {
                    setStatus(d.value as SeroStatus);
                    setResultId(null);
                  }}
                >
                  {SERO_STATUSES.filter((s) => s !== profile.seroStatus).map((s) => (
                    <Radio key={s} value={s} label={seroLabel[s]} />
                  ))}
                </RadioGroup>
              </Field>
              {status && (
                <Field
                  label={`The ${tested === 'HBsAg' ? 'HBsAg' : tested === 'HCV' ? 'hepatitis C' : 'HIV'} result it rests on`}
                  required
                  validationMessage={
                    results.isSuccess && matching.length === 0
                      ? 'No verified result for this infection from the last 90 days. Order the test first.'
                      : undefined
                  }
                >
                  {results.isLoading ? (
                    <Spinner size="tiny" label="Loading the patient's results…" />
                  ) : (
                    <RadioGroup value={resultId ?? ''} onChange={(_, d) => setResultId(d.value)}>
                      {matching.map((r) => (
                        <Radio
                          key={r.id}
                          value={r.id}
                          label={`${r.testName}: ${r.value ?? '—'}${r.flag ? ` (${r.flag})` : ''}, verified ${formatDateOnly(r.verifiedOn)}`}
                        />
                      ))}
                    </RadioGroup>
                  )}
                </Field>
              )}
              <Field label="Reason" required hint="At least 5 characters.">
                <Textarea value={reason} maxLength={500} onChange={(_, d) => setReason(d.value)} />
              </Field>
              {status && (
                <MessageBar intent={waits ? 'warning' : 'info'} layout="multiline">
                  <MessageBarBody>
                    <MessageBarTitle>{waits ? 'Waits for a second person' : 'Made at once'}</MessageBarTitle>
                    {waits
                      ? `Taking the patient off ${seroLabel[profile.seroStatus].toLowerCase()} machines waits for someone who approves infection status changes (not you). The patient stays ${seroLabel[profile.seroStatus].toLowerCase()} until then.`
                      : `The patient is isolated now: only ${seroLabel[status].toLowerCase()} machines can be assigned. Reassign sessions already booked.`}
                  </MessageBarBody>
                </MessageBar>
              )}
              <ActionError error={change.error} />
            </DialogContent>
            <DialogActions>
              <Button onClick={onClose} disabled={change.isPending}>
                Cancel
              </Button>
              <Button type="submit" appearance="primary" disabled={!ready || change.isPending}>
                {waits ? 'Ask for the change' : 'Change status'}
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
      </DialogSurface>
    </Dialog>
  );
}
