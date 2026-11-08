import {
  Button,
  Caption1,
  Field,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  Spinner,
  Text,
  Textarea,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import { formatDateTime, isApiError } from '@hc360/core';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { pharmacyApi, pharmacyKeys, type QueueEntryDetail, type QueueLineDto } from './api';
import { unacknowledgedOverrides } from './overrideAcknowledgement';

const useStyles = makeStyles({
  line: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXS,
    paddingBlock: tokens.spacingVerticalS,
  },
  actions: { display: 'flex', gap: tokens.spacingHorizontalS, alignItems: 'flex-end', flexWrap: 'wrap' },
  note: { minWidth: '240px', flexGrow: 1 },
});

/**
 * The prescriber signed these lines over a blocking safety alert (an allergy, a contraindicated interaction, a dose above
 * the maximum). The pharmacist reads the alert and the prescriber's reason and acknowledges each line from their own
 * sign-in before it is dispensed or issued; the acknowledgement is audited with their name.
 */
export function SafetyOverrides({ entry, canAcknowledge }: { entry: QueueEntryDetail; canAcknowledge: boolean }) {
  const overridden = entry.lines.filter((l) => Boolean(l.safetyOverride));
  if (overridden.length === 0) {
    return null;
  }
  const waiting = unacknowledgedOverrides(entry.lines).length;
  return (
    <MessageBar intent={waiting > 0 ? 'error' : 'warning'} layout="multiline">
      <MessageBarBody>
        <MessageBarTitle>Prescribed over a safety alert</MessageBarTitle>
        <Text block>
          Prescriber&apos;s reason: <Text weight="semibold">{entry.safetyOverrideReason ?? 'not recorded'}</Text>
        </Text>
        {overridden.map((line) => (
          <OverrideLine key={line.id} entry={entry} line={line} canAcknowledge={canAcknowledge} />
        ))}
      </MessageBarBody>
    </MessageBar>
  );
}

function OverrideLine({
  entry,
  line,
  canAcknowledge,
}: {
  entry: QueueEntryDetail;
  line: QueueLineDto;
  canAcknowledge: boolean;
}) {
  const styles = useStyles();
  const queryClient = useQueryClient();
  const [note, setNote] = useState('');
  const acknowledge = useMutation({
    mutationFn: () => pharmacyApi.acknowledgeOverride(entry.id, line.id, note.trim() || null),
    onSuccess: () => void queryClient.invalidateQueries({ queryKey: pharmacyKeys.queue }),
  });
  return (
    <div className={styles.line}>
      <Text weight="semibold">{line.drugName}</Text>
      <Text>{line.safetyOverride}</Text>
      {line.overrideAcknowledgedAt ? (
        <Caption1>
          {`Acknowledged by ${line.overrideAcknowledgedBy ?? 'a pharmacist'} on ${formatDateTime(line.overrideAcknowledgedAt)}`}
          {line.overrideAcknowledgementNote ? ` — ${line.overrideAcknowledgementNote}` : ''}
        </Caption1>
      ) : canAcknowledge ? (
        <div className={styles.actions}>
          <Field label={`What you did about ${line.drugName} (optional)`} className={styles.note}>
            <Textarea
              value={note}
              maxLength={500}
              rows={1}
              onChange={(_, d) => setNote(d.value)}
              placeholder="e.g. Spoke to the doctor, patient counselled on the reaction to watch for"
            />
          </Field>
          <Button
            appearance="primary"
            disabled={acknowledge.isPending}
            icon={acknowledge.isPending ? <Spinner size="tiny" aria-hidden /> : undefined}
            onClick={() => acknowledge.mutate()}
          >
            Acknowledge
          </Button>
        </div>
      ) : (
        <Caption1>A pharmacist acknowledges this before it is dispensed.</Caption1>
      )}
      {acknowledge.error && (
        <Caption1 role="alert">
          {isApiError(acknowledge.error) ? acknowledge.error.userMessage : 'Could not acknowledge it.'}
        </Caption1>
      )}
    </div>
  );
}
