import { Button, makeStyles, MessageBar, MessageBarBody, MessageBarTitle, tokens } from '@fluentui/react-components';
import { formatDateTime } from '@hc360/core';
import { useNotify } from '@hc360/ui';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { mastersApi, mastersKeys, type PractitionerDto, type PractitionerHprCheck } from './api';

const useStyles = makeStyles({
  panel: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalS, alignItems: 'flex-start' },
});

const MISMATCH_LABELS: Record<string, string> = {
  name: 'the name',
  council: 'the medical council',
  registrationNumber: 'the registration number',
  status: 'the registration is not active',
};

/** What the latest check says, in a sentence, with when and by whom it was made. */
export function describeHprCheck(check: PractitionerHprCheck): {
  intent: 'success' | 'warning' | 'error';
  title: string;
  body: string;
} {
  const when = `Checked ${formatDateTime(check.checkedAt)} by ${check.checkedByName}${check.source === 'simulator' ? ', against the HPR simulator (not the live registry)' : ''}.`;
  if (!check.current) {
    return {
      intent: 'warning',
      title: 'Changed since it was checked',
      body: `The HPR ID, council, registration number or name was changed after the last check. Check again. ${when}`,
    };
  }
  if (check.result === 'Verified') {
    return {
      intent: 'success',
      title: 'HPR ID verified',
      body: `The registry's name, council and registration number match. ${when}`,
    };
  }
  if (check.result === 'NotFound') {
    return {
      intent: 'error',
      title: 'Not in the registry',
      body: `The registry has no professional with this HPR ID. ${when}`,
    };
  }
  const differs = check.mismatches.map((m) => MISMATCH_LABELS[m] ?? m).join(', ');
  const registry = [check.registryName, check.registryCouncil, check.registryNumber].filter(Boolean).join(', ');
  return {
    intent: 'error',
    title: 'Does not match the registry',
    body: `Differs: ${differs}. The registry has ${registry}. ${when}`,
  };
}

/** The practitioner's HPR verification: the latest check, and a button to check the saved HPR ID with the registry. */
export function HprCheckPanel({ practitioner, dirty }: { practitioner: PractitionerDto; dirty: boolean }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const check = useMutation({
    mutationFn: () => mastersApi.checkHpr(practitioner.id, practitioner.version),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: [...mastersKeys.practitioners, practitioner.id] }),
    onError: (error) => notify.error(error, 'The HPR ID could not be checked'),
  });
  const shown = practitioner.hprCheck ? describeHprCheck(practitioner.hprCheck) : null;
  return (
    <div className={styles.panel} aria-live="polite">
      {shown ? (
        <MessageBar intent={shown.intent} layout="multiline">
          <MessageBarBody>
            <MessageBarTitle>{shown.title}</MessageBarTitle>
            {shown.body}
          </MessageBarBody>
        </MessageBar>
      ) : (
        practitioner.hprId && <span>The HPR ID has not been checked with the registry.</span>
      )}
      <Button
        appearance="secondary"
        disabled={!practitioner.hprId || dirty || check.isPending}
        onClick={() => check.mutate()}
        title={dirty ? 'Save your changes first: the saved HPR ID is checked.' : undefined}
      >
        {check.isPending ? 'Checking…' : 'Check with the HPR registry'}
      </Button>
    </div>
  );
}
