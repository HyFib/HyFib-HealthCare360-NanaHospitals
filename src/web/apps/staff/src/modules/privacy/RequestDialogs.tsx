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
  makeStyles,
  MessageBar,
  MessageBarBody,
  Radio,
  RadioGroup,
  Select,
  Spinner,
  Text,
  Textarea,
  tokens,
} from '@fluentui/react-components';
import { zodResolver } from '@hookform/resolvers/zod';
import { useSession } from '@hc360/core';
import { applyServerErrors, ErrorState, TextField } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { Controller, useForm, useWatch } from 'react-hook-form';
import { z } from 'zod';
import { privacyApi, privacyQueries, type DataRequestDetail } from './api';
import {
  identityCheckLabel,
  REQUEST_OUTCOMES,
  requesterRelationLabel,
  requestOutcomeHelp,
  requestStatusLabel,
  STAFF_IDENTITY_CHECKS,
  staffIdentityCheckHelp,
  type RequestOutcome,
} from './labels';
import { officerChoices, outcomeBlocker, requestFacts, suggestedOutcome, suggestedReply } from './rules';
import type { RequestCommand } from './useRequestCommand';

const useStyles = makeStyles({
  content: { display: 'flex', flexDirection: 'column', gap: '12px' },
  choiceLabel: { display: 'flex', flexDirection: 'column', gap: '2px' },
  muted: { color: tokens.colorNeutralForeground3 },
  blocked: { color: tokens.colorPaletteMarigoldForeground1 },
});

function whoIsAsking(r: DataRequestDetail): string {
  return r.requestedBy === 'Self'
    ? `The patient, ${r.principalName}`
    : `${requesterRelationLabel[r.requestedBy]}${r.requesterName ? `, ${r.requesterName}` : ''}, for ${r.principalName}`;
}

// ------------------------------------------------------------------ identity

const identitySchema = z
  .object({
    check: z.enum(STAFF_IDENTITY_CHECKS).nullable(),
    note: z.string().trim().max(500),
  })
  .superRefine((v, ctx) => {
    if (!v.check) {
      ctx.addIssue({ code: 'custom', path: ['check'], message: 'Choose how you checked their identity.' });
    } else if (v.check === 'Other' && !v.note) {
      ctx.addIssue({ code: 'custom', path: ['note'], message: 'Say how the identity was checked.' });
    }
  });

type IdentityValues = z.infer<typeof identitySchema>;

/** Records that the requester's identity, and their authority to act for the patient, were checked. */
export function VerifyIdentityDialog({
  open,
  request,
  command,
  onClose,
}: {
  open: boolean;
  request: DataRequestDetail;
  command: RequestCommand;
  onClose: () => void;
}) {
  return (
    <Dialog open={open} onOpenChange={(_, d) => !d.open && !command.busy && onClose()}>
      <DialogSurface style={{ maxWidth: 580 }}>
        {open && <VerifyBody request={request} command={command} onClose={onClose} />}
      </DialogSurface>
    </Dialog>
  );
}

function VerifyBody({
  request,
  command,
  onClose,
}: {
  request: DataRequestDetail;
  command: RequestCommand;
  onClose: () => void;
}) {
  const styles = useStyles();
  const form = useForm<IdentityValues>({
    resolver: zodResolver(identitySchema),
    defaultValues: { check: null, note: '' },
  });

  const submit = async (values: IdentityValues) => {
    if (!values.check) {
      return;
    }
    const check = values.check;
    const result = await command.run(() => privacyApi.verifyIdentity(request.id, check, values.note.trim() || null), {
      success: 'Identity confirmed',
      failure: 'Could not record the identity check',
      onValidation: (error) => applyServerErrors(error, form.setError),
    });
    if (result.ok) {
      onClose();
    }
  };

  return (
    <form onSubmit={form.handleSubmit(submit)} noValidate>
      <DialogBody>
        <DialogTitle>Confirm the requester’s identity</DialogTitle>
        <DialogContent className={styles.content}>
          <Text>
            Check who is asking, and that they may act for the patient: for example a parent of a child, or the nominee
            the patient named. Data is given out, changed or erased only after this check.
          </Text>
          <Caption1 className={styles.muted}>Asking: {whoIsAsking(request)}</Caption1>
          <Controller
            control={form.control}
            name="check"
            render={({ field, fieldState }) => (
              <Field
                label="How did you check?"
                required
                validationMessage={fieldState.error?.message}
                validationState={fieldState.error ? 'error' : 'none'}
              >
                <RadioGroup value={field.value ?? ''} onChange={(_, d) => field.onChange(d.value)}>
                  {STAFF_IDENTITY_CHECKS.map((check, index) => (
                    <Radio
                      key={check}
                      value={check}
                      autoFocus={index === 0}
                      label={
                        <span className={styles.choiceLabel}>
                          <Text>{identityCheckLabel[check]}</Text>
                          <Caption1 className={styles.muted}>{staffIdentityCheckHelp[check]}</Caption1>
                        </span>
                      }
                    />
                  ))}
                </RadioGroup>
              </Field>
            )}
          />
          <TextField
            control={form.control}
            name="note"
            label="Note"
            maxLength={500}
            hint="Say how, when it was not a photo ID or someone staff know. Do not write down ID numbers."
          />
        </DialogContent>
        <DialogActions>
          <Button onClick={onClose} disabled={command.busy}>
            Cancel
          </Button>
          <Button
            type="submit"
            appearance="primary"
            disabled={command.busy}
            icon={command.busy ? <Spinner size="tiny" /> : undefined}
          >
            Confirm identity
          </Button>
        </DialogActions>
      </DialogBody>
    </form>
  );
}

// ------------------------------------------------------------------ assign

/** Gives the request an owner (or none). */
export function AssignDialog({
  open,
  request,
  command,
  onClose,
}: {
  open: boolean;
  request: DataRequestDetail;
  command: RequestCommand;
  onClose: () => void;
}) {
  return (
    <Dialog open={open} onOpenChange={(_, d) => !d.open && !command.busy && onClose()}>
      <DialogSurface style={{ maxWidth: 520 }}>
        {open && <AssignBody request={request} command={command} onClose={onClose} />}
      </DialogSurface>
    </Dialog>
  );
}

function AssignBody({
  request,
  command,
  onClose,
}: {
  request: DataRequestDetail;
  command: RequestCommand;
  onClose: () => void;
}) {
  const styles = useStyles();
  const meId = useSession((s) => s.user?.id);
  const officers = useQuery(privacyQueries.officers());
  const current = request.assignedToUserId
    ? { id: request.assignedToUserId, name: request.assignedToName ?? 'Current owner' }
    : null;
  const [userId, setUserId] = useState(current?.id ?? '');
  const choices = officers.data ? officerChoices(officers.data, meId, current) : [];
  const changed = userId !== (current?.id ?? '');

  const save = async (next: string | null) => {
    const name = next ? (officers.data?.find((o) => o.id === next)?.displayName ?? current?.name ?? null) : null;
    const result = await command.run(() => privacyApi.assign(request.id, next), {
      success: next
        ? next === meId
          ? 'You own this request now'
          : `Assigned to ${name ?? 'the officer'}`
        : 'Unassigned',
      failure: 'Could not change the owner',
    });
    if (result.ok) {
      onClose();
    }
  };

  return (
    <form
      onSubmit={(event) => {
        event.preventDefault();
        if (userId && changed) {
          void save(userId);
        }
      }}
    >
      <DialogBody>
        <DialogTitle>Who handles {request.requestNumber}?</DialogTitle>
        <DialogContent className={styles.content}>
          {officers.error ? (
            <ErrorState error={officers.error} onRetry={() => void officers.refetch()} />
          ) : !officers.data ? (
            <Spinner size="small" label="Loading the officers…" labelPosition="after" />
          ) : (
            <Field
              label="Owner"
              required
              hint="Staff who handle privacy requests in this facility. The owner sees it under “Mine” in the queue."
            >
              <Select value={userId} onChange={(_, d) => setUserId(d.value)} autoFocus>
                <option value="" disabled>
                  Choose a member of staff
                </option>
                {choices.map((choice) => (
                  <option key={choice.id} value={choice.id}>
                    {choice.label}
                  </option>
                ))}
              </Select>
            </Field>
          )}
          {officers.data && officers.data.length === 0 && (
            <Caption1 className={styles.muted}>
              Nobody in this facility holds the grievance officer’s access yet. Ask the hospital administrator to give
              it to the right role.
            </Caption1>
          )}
        </DialogContent>
        <DialogActions>
          <Button onClick={onClose} disabled={command.busy}>
            Cancel
          </Button>
          {current && (
            <Button onClick={() => void save(null)} disabled={command.busy}>
              Unassign
            </Button>
          )}
          <Button
            type="submit"
            appearance="primary"
            disabled={command.busy || !userId || !changed}
            icon={command.busy ? <Spinner size="tiny" /> : undefined}
          >
            Assign
          </Button>
        </DialogActions>
      </DialogBody>
    </form>
  );
}

// ------------------------------------------------------------------ answer

const answerSchema = z.object({
  outcome: z
    .enum(REQUEST_OUTCOMES)
    .nullable()
    .refine((v) => Boolean(v), 'Choose the outcome.'),
  response: z.string().trim().min(1, 'Write the reply.').max(4000),
});

type AnswerValues = z.infer<typeof answerSchema>;

export interface AnswerPrefill {
  outcome: RequestOutcome;
  reply: string;
}

/**
 * Closes the request with its outcome and the reply, which goes to the requester by SMS or e-mail (with the grievance
 * officer's contact) and appears in the patient portal.
 */
export function AnswerDialog({
  open,
  request,
  prefill,
  erasureSummary,
  command,
  onClose,
}: {
  open: boolean;
  request: DataRequestDetail;
  /** Starting outcome and reply, for example after an erasure. */
  prefill: AnswerPrefill | null;
  /** The latest erasure recorded on the request, to start the reply with. */
  erasureSummary: string | null;
  command: RequestCommand;
  onClose: () => void;
}) {
  return (
    <Dialog open={open} onOpenChange={(_, d) => !d.open && !command.busy && onClose()}>
      <DialogSurface style={{ maxWidth: 680 }}>
        {open && (
          <AnswerBody
            request={request}
            prefill={prefill}
            erasureSummary={erasureSummary}
            command={command}
            onClose={onClose}
          />
        )}
      </DialogSurface>
    </Dialog>
  );
}

function AnswerBody({
  request,
  prefill,
  erasureSummary,
  command,
  onClose,
}: {
  request: DataRequestDetail;
  prefill: AnswerPrefill | null;
  erasureSummary: string | null;
  command: RequestCommand;
  onClose: () => void;
}) {
  const styles = useStyles();
  const facts = requestFacts(request);
  const suggestionFor = (outcome: RequestOutcome | null) =>
    outcome ? suggestedReply({ type: request.type, outcome, corrections: request.corrections, erasureSummary }) : '';
  const [initial] = useState<AnswerValues>(() => {
    if (prefill) {
      return { outcome: prefill.outcome, response: prefill.reply };
    }
    const suggested = suggestedOutcome(facts, {
      keptByLaw: request.retainedPermanently || request.retainedUntil !== null,
    });
    const outcome = outcomeBlocker(facts, suggested) ? null : suggested;
    return { outcome, response: suggestionFor(outcome) };
  });
  const form = useForm<AnswerValues>({ resolver: zodResolver(answerSchema), defaultValues: initial });
  const outcome = useWatch({ control: form.control, name: 'outcome' });
  const blocker = outcome ? outcomeBlocker(facts, outcome) : null;

  const submit = async (values: AnswerValues) => {
    if (!values.outcome || outcomeBlocker(facts, values.outcome)) {
      return;
    }
    const chosen = values.outcome;
    const result = await command.run(() => privacyApi.close(request.id, chosen, values.response.trim()), {
      success: `Request ${requestStatusLabel[chosen].toLowerCase()}`,
      successBody: 'The reply is on its way to the requester.',
      failure: 'Could not send the answer',
      onValidation: (error) => applyServerErrors(error, form.setError),
    });
    if (result.ok) {
      onClose();
    }
  };

  const chooseOutcome = (next: RequestOutcome) => {
    // Swap the suggested reply for the new outcome's, unless the officer has already written their own.
    if (form.getValues('response').trim() === suggestionFor(form.getValues('outcome')).trim()) {
      form.setValue('response', suggestionFor(next), { shouldDirty: true });
    }
    form.setValue('outcome', next, { shouldDirty: true, shouldValidate: true });
  };

  return (
    <form onSubmit={form.handleSubmit(submit)} noValidate>
      <DialogBody>
        <DialogTitle>Answer {request.requestNumber}</DialogTitle>
        <DialogContent className={styles.content}>
          <Controller
            control={form.control}
            name="outcome"
            render={({ field, fieldState }) => (
              <Field
                label="Outcome"
                required
                validationMessage={fieldState.error?.message}
                validationState={fieldState.error ? 'error' : 'none'}
              >
                <RadioGroup
                  value={field.value ?? ''}
                  onChange={(_, d) => chooseOutcome(d.value as RequestOutcome)}
                  aria-label="Outcome"
                >
                  {REQUEST_OUTCOMES.map((o) => {
                    const reason = outcomeBlocker(facts, o);
                    return (
                      <Radio
                        key={o}
                        value={o}
                        disabled={Boolean(reason)}
                        label={
                          <span className={styles.choiceLabel}>
                            <Text weight="semibold">{requestStatusLabel[o]}</Text>
                            <Caption1 className={reason ? styles.blocked : styles.muted}>
                              {reason ?? requestOutcomeHelp[o]}
                            </Caption1>
                          </span>
                        }
                      />
                    );
                  })}
                </RadioGroup>
              </Field>
            )}
          />
          {blocker && (
            <MessageBar intent="warning">
              <MessageBarBody>{blocker}</MessageBarBody>
            </MessageBar>
          )}
          <Controller
            control={form.control}
            name="response"
            render={({ field, fieldState }) => (
              <Field
                label="Reply to the requester"
                required
                validationMessage={fieldState.error?.message}
                validationState={fieldState.error ? 'error' : 'none'}
                hint={
                  outcome === 'Declined'
                    ? 'Say why the request is declined, and on what grounds. Ctrl+Enter sends.'
                    : 'Say what was done. It is sent by SMS or e-mail with the grievance officer’s contact, and shown in the patient portal. Ctrl+Enter sends.'
                }
              >
                <Textarea
                  value={field.value}
                  onChange={(_, d) => field.onChange(d.value)}
                  onBlur={field.onBlur}
                  ref={field.ref}
                  rows={6}
                  maxLength={4000}
                  resize="vertical"
                  autoFocus={Boolean(initial.outcome)}
                  onKeyDown={(event) => {
                    if (event.key === 'Enter' && (event.ctrlKey || event.metaKey)) {
                      event.preventDefault();
                      void form.handleSubmit(submit)();
                    }
                  }}
                />
              </Field>
            )}
          />
          {request.type === 'Access' && outcome && outcome !== 'Declined' && (
            <Caption1 className={styles.muted}>
              The data summary as it is now is kept with the request, as the record of what was given.
            </Caption1>
          )}
          {request.type === 'Erasure' && outcome && outcome !== 'Declined' && request.erasedItems.length === 0 && (
            <MessageBar intent="info">
              <MessageBarBody>Nothing has been erased for this request yet.</MessageBarBody>
            </MessageBar>
          )}
        </DialogContent>
        <DialogActions>
          <Button onClick={onClose} disabled={command.busy}>
            Cancel
          </Button>
          <Button
            type="submit"
            appearance="primary"
            disabled={command.busy || Boolean(blocker)}
            icon={command.busy ? <Spinner size="tiny" /> : undefined}
          >
            Send answer
          </Button>
        </DialogActions>
      </DialogBody>
    </form>
  );
}
