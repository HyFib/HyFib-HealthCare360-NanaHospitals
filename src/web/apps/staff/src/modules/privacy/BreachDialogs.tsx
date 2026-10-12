import {
  Button,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Field,
  Input,
  makeStyles,
  Spinner,
  Text,
} from '@fluentui/react-components';
import { zodResolver } from '@hookform/resolvers/zod';
import { formatDateTime, isApiError } from '@hc360/core';
import { CloseRequestScope, TextField, useCloseRequests, useUnsavedChanges } from '@hc360/ui';
import { useState } from 'react';
import { Controller, useForm } from 'react-hook-form';
import { z } from 'zod';
import { fromIstInput, nowIstInput } from '@/modules/hr/shared/dates';
import { privacyApi, type BreachDetail } from './api';
import { extensionMinimum } from './rules';
import type { BreachCommand } from './useBreachCommand';

export type BreachStepKind = 'boardInformed' | 'extension' | 'reportSubmitted';

const steps: Record<
  BreachStepKind,
  { title: string; description: string; dateLabel: string; confirm: string; success: string }
> = {
  boardInformed: {
    title: 'Record that the Board was told',
    description:
      'Record when the Data Protection Board was first told about the breach, with what was known then, and any reference it gave.',
    dateLabel: 'When the Board was told',
    confirm: 'Record',
    success: 'Recorded that the Board was told',
  },
  extension: {
    title: 'Record an extension from the Board',
    description:
      'Only when the Board allowed more time for the detailed report, on the hospital’s written request. The clock then runs to the new deadline.',
    dateLabel: 'New deadline for the report',
    confirm: 'Record extension',
    success: 'Extension recorded',
  },
  reportSubmitted: {
    title: 'Record that the report was sent to the Board',
    description: 'Record when the detailed report went to the Board, and any reference it gave.',
    dateLabel: 'When the report was sent',
    confirm: 'Record',
    success: 'Recorded that the report was sent',
  },
};

/** A few minutes of slack for workstation clocks that run fast. */
const CLOCK_SLACK_MS = 5 * 60_000;

function stepSchema(kind: BreachStepKind, breach: BreachDetail) {
  return z
    .object({
      at: z.string().min(1, 'Enter the date and time.'),
      reference: z.string().trim().max(100),
    })
    .superRefine((v, ctx) => {
      const iso = fromIstInput(v.at);
      if (!iso) {
        ctx.addIssue({ code: 'custom', path: ['at'], message: 'Enter the date and time.' });
        return;
      }
      const at = Date.parse(iso);
      if (kind === 'extension') {
        const minimum = extensionMinimum(breach.awareAt);
        if (at <= Date.parse(minimum)) {
          ctx.addIssue({
            code: 'custom',
            path: ['at'],
            message: `Choose a time after ${formatDateTime(minimum)}: 72 hours after the hospital became aware.`,
          });
        }
        return;
      }
      const earliest = kind === 'boardInformed' ? breach.awareAt : (breach.boardInformedAt ?? breach.awareAt);
      if (at < Date.parse(earliest) - 60_000) {
        ctx.addIssue({
          code: 'custom',
          path: ['at'],
          message:
            kind === 'boardInformed'
              ? `It cannot be before the hospital became aware (${formatDateTime(earliest)}).`
              : `It cannot be before the Board was first told (${formatDateTime(earliest)}).`,
        });
      } else if (at > Date.now() + CLOCK_SLACK_MS) {
        ctx.addIssue({ code: 'custom', path: ['at'], message: 'It cannot be in the future.' });
      }
    });
}

type StepValues = { at: string; reference: string };

/** Board told, extension granted, or report sent: when, and the Board's reference. */
export function BreachStepDialog({
  kind,
  breach,
  command,
  onClose,
}: {
  kind: BreachStepKind | null;
  breach: BreachDetail;
  command: BreachCommand;
  onClose: () => void;
}) {
  const closing = useCloseRequests({ onClose });
  return (
    <Dialog open={kind !== null} onOpenChange={closing.onOpenChange}>
      <DialogSurface style={{ maxWidth: 560 }}>
        <CloseRequestScope value={closing}>
          {kind && <StepBody kind={kind} breach={breach} command={command} onClose={onClose} />}
        </CloseRequestScope>
      </DialogSurface>
    </Dialog>
  );
}

const useStyles = makeStyles({
  content: { display: 'flex', flexDirection: 'column', gap: '12px' },
});

function StepBody({
  kind,
  breach,
  command,
  onClose,
}: {
  kind: BreachStepKind;
  breach: BreachDetail;
  command: BreachCommand;
  onClose: () => void;
}) {
  const styles = useStyles();
  const text = steps[kind];
  const [schema] = useState(() => stepSchema(kind, breach));
  const form = useForm<StepValues>({
    resolver: zodResolver(schema),
    defaultValues: { at: kind === 'extension' ? '' : nowIstInput(), reference: breach.boardReference ?? '' },
  });

  const submit = async (values: StepValues) => {
    const at = fromIstInput(values.at);
    if (!at) {
      return;
    }
    const reference = values.reference.trim() || null;
    const action =
      kind === 'boardInformed'
        ? () => privacyApi.boardInformed(breach.id, at, reference)
        : kind === 'extension'
          ? () => privacyApi.extension(breach.id, at, reference)
          : () => privacyApi.reportSubmitted(breach.id, at, reference);
    const result = await command.run(action, {
      success: text.success,
      failure: 'Could not record it',
      onValidation: (error) => {
        if (!isApiError(error) || !error.isValidation) {
          return false;
        }
        // The server names the time "at" or "until"; both are the one date field here.
        for (const [key, messages] of Object.entries(error.fieldErrors)) {
          form.setError(key === 'reference' ? 'reference' : 'at', { type: 'server', message: messages[0] });
        }
        return true;
      },
    });
    if (result.ok) {
      onClose();
    }
  };

  const guard = useUnsavedChanges({ dirty: form.formState.isDirty, busy: command.busy, onClose });

  return (
    <form onSubmit={form.handleSubmit(submit)} noValidate>
      <DialogBody>
        <DialogTitle>{text.title}</DialogTitle>
        <DialogContent className={styles.content}>
          <Text>{text.description}</Text>
          <Controller
            control={form.control}
            name="at"
            render={({ field, fieldState }) => (
              <Field
                label={text.dateLabel}
                required
                hint="Hospital time."
                validationMessage={fieldState.error?.message}
                validationState={fieldState.error ? 'error' : 'none'}
              >
                <Input
                  type="datetime-local"
                  value={field.value}
                  onChange={(_, d) => field.onChange(d.value)}
                  onBlur={field.onBlur}
                  ref={field.ref}
                  autoFocus
                />
              </Field>
            )}
          />
          <TextField
            control={form.control}
            name="reference"
            label="The Board’s reference (if any)"
            maxLength={100}
            autoComplete="off"
          />
        </DialogContent>
        <DialogActions>
          <Button onClick={guard.requestClose} disabled={command.busy}>
            Cancel
          </Button>
          <Button
            type="submit"
            appearance="primary"
            disabled={command.busy}
            icon={command.busy ? <Spinner size="tiny" aria-hidden /> : undefined}
          >
            {text.confirm}
          </Button>
        </DialogActions>
      </DialogBody>
      {guard.dialogs}
    </form>
  );
}
