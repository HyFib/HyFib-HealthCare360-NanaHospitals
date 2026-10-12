import {
  Button,
  Caption1,
  Checkbox,
  Field,
  Input,
  Label,
  makeStyles,
  MessageBar,
  MessageBarActions,
  MessageBarBody,
  MessageBarTitle,
  Spinner,
  Text,
  tokens,
} from '@fluentui/react-components';
import { SaveRegular } from '@fluentui/react-icons';
import { zodResolver } from '@hookform/resolvers/zod';
import { formatRelative, isApiError, useHotkey } from '@hc360/core';
import {
  applyServerErrors,
  FormGrid,
  LEAVE_KEEPING_DRAFT,
  NumberField,
  Section,
  TextAreaField,
  TextField,
  useLeaveGuard,
  useNotify,
} from '@hc360/ui';
import { useId, useState, type ReactElement } from 'react';
import { Controller, useForm, useWatch, type Control } from 'react-hook-form';
import { useFormDraft } from '@/lib/useFormDraft';
import { privacyApi, type BreachCreated, type BreachDetail } from './api';
import {
  breachFormSchema,
  formGaps,
  fromBreachDetail,
  newBreachDefaults,
  toSaveBreachRequest,
  type BreachFormValues,
} from './breachSchema';
import {
  BREACH_DATA_CATEGORIES,
  BREACH_NATURES,
  breachDataCategoryLabel,
  breachNatureLabel,
  breachNatureTerm,
  NOTICE_MAX_LENGTH,
} from './labels';
import { listText } from './rules';

const useStyles = makeStyles({
  form: { display: 'flex', flexDirection: 'column', gap: '16px' },
  stack: { display: 'flex', flexDirection: 'column', gap: '12px' },
  group: { display: 'flex', flexDirection: 'column', gap: '4px' },
  checks: { display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(260px, 1fr))', gap: '0 16px' },
  checkLabel: { display: 'flex', flexDirection: 'column', gap: '2px' },
  muted: { color: tokens.colorNeutralForeground3 },
  counter: { color: tokens.colorNeutralForeground3, textAlign: 'end' },
  counterNear: {
    color: tokens.colorPaletteMarigoldForeground1,
    fontWeight: tokens.fontWeightSemibold,
    textAlign: 'end',
  },
  footer: {
    position: 'sticky',
    bottom: 0,
    display: 'flex',
    justifyContent: 'flex-end',
    alignItems: 'center',
    gap: '12px',
    flexWrap: 'wrap',
    padding: '12px 16px',
    backgroundColor: tokens.colorNeutralBackground1,
    borderTop: `1px solid ${tokens.colorNeutralStroke2}`,
    boxShadow: tokens.shadow8,
    borderRadius: tokens.borderRadiusLarge,
    zIndex: 1,
  },
});

/** A date and time in hospital time (IST), for `<input type="datetime-local">`. */
function DateTimeField({
  control,
  name,
  label,
  hint,
  required,
}: {
  control: Control<BreachFormValues>;
  name: 'awareAt' | 'occurredFrom' | 'occurredTo';
  label: string;
  hint?: string;
  required?: boolean;
}) {
  return (
    <Controller
      control={control}
      name={name}
      render={({ field, fieldState }) => (
        <Field
          label={label}
          hint={hint}
          required={required}
          validationMessage={fieldState.error?.message}
          validationState={fieldState.error ? 'error' : 'none'}
        >
          <Input
            type="datetime-local"
            value={field.value}
            onChange={(_, d) => field.onChange(d.value)}
            onBlur={field.onBlur}
            ref={field.ref}
          />
        </Field>
      )}
    />
  );
}

/**
 * Tick boxes for a list of choices, stored as an array. A labelled group rather than a Field: a Field gives every
 * control inside it the same id, which would tie all the labels to the first box.
 */
function CheckList<T extends string>({
  label,
  hint,
  options,
  value,
  onChange,
  render,
}: {
  label: string;
  hint?: string;
  options: readonly T[];
  value: T[];
  onChange: (value: T[]) => void;
  render: (option: T) => ReactElement | string;
}) {
  const styles = useStyles();
  const labelId = useId();
  const hintId = useId();
  return (
    <div role="group" aria-labelledby={labelId} aria-describedby={hint ? hintId : undefined} className={styles.group}>
      <Label id={labelId}>{label}</Label>
      <div className={styles.checks}>
        {options.map((option) => (
          <Checkbox
            key={option}
            checked={value.includes(option)}
            onChange={(_, d) =>
              onChange(d.checked ? [...value.filter((v) => v !== option), option] : value.filter((v) => v !== option))
            }
            label={render(option)}
          />
        ))}
      </div>
      {hint && (
        <Caption1 id={hintId} className={styles.muted}>
          {hint}
        </Caption1>
      )}
    </div>
  );
}

export type BreachFormMode = { kind: 'new' } | { kind: 'edit'; breach: BreachDetail };

/**
 * Records a personal data breach, or changes one: what happened, the impact and response, and the short notice for
 * the people affected. Saved locally as a draft every few seconds; Ctrl+S saves.
 */
export function BreachForm({
  mode,
  onSaved,
  onCancel,
  onConflict,
}: {
  mode: BreachFormMode;
  /** The saved breach: its id and number. */
  onSaved: (saved: BreachCreated) => void;
  onCancel: () => void;
  /** Someone else saved the breach meanwhile (409): reload the latest details. */
  onConflict?: () => void;
}) {
  const styles = useStyles();
  const notify = useNotify();
  const [busy, setBusy] = useState(false);
  const [initial] = useState(() => (mode.kind === 'new' ? newBreachDefaults() : fromBreachDetail(mode.breach)));
  const form = useForm<BreachFormValues>({
    resolver: zodResolver(breachFormSchema),
    defaultValues: initial,
    mode: 'onTouched',
  });
  const draft = useFormDraft(form, mode.kind === 'new' ? 'privacy-breach-new' : `privacy-breach-${mode.breach.id}`);
  const [description, consequences, mitigation, cause, remediation, principalSteps, principalNotice] = useWatch({
    control: form.control,
    name: ['description', 'consequences', 'mitigation', 'cause', 'remediation', 'principalSteps', 'principalNotice'],
  });
  const gaps = formGaps({ description, consequences, mitigation, cause, remediation, principalSteps, principalNotice });
  const noticeLength = principalNotice.length;

  const submit = async (values: BreachFormValues) => {
    setBusy(true);
    try {
      if (mode.kind === 'new') {
        const created = await privacyApi.createBreach(toSaveBreachRequest(values, null));
        draft.clear();
        onSaved(created);
      } else {
        await privacyApi.updateBreach(mode.breach.id, toSaveBreachRequest(values, mode.breach.version));
        draft.clear();
        onSaved({ id: mode.breach.id, breachNumber: mode.breach.breachNumber });
      }
    } catch (error) {
      if (isApiError(error) && error.status === 409) {
        notify.warning(
          'Someone else changed this breach',
          'The latest details have been loaded. Make your changes again; a draft of them may be offered to restore.',
        );
        onConflict?.();
      } else if (!applyServerErrors(error, form.setError)) {
        notify.error(error, 'Could not save the breach');
      }
    } finally {
      setBusy(false);
    }
  };

  useHotkey('mod+s', () => void form.handleSubmit(submit)(), { allowInInputs: true, enabled: !busy });

  // Leaving the screen or closing the window with unsaved changes asks first (SRS UX-08).
  useLeaveGuard({ dirty: form.formState.isDirty, busy, leaveQuestion: LEAVE_KEEPING_DRAFT });

  return (
    <form onSubmit={form.handleSubmit(submit)} noValidate className={styles.form}>
      {draft.saved && (
        <MessageBar intent="info">
          <MessageBarBody>
            Unsaved changes from {formatRelative(new Date(draft.saved.savedAt).toISOString())} were found on this
            computer.
          </MessageBarBody>
          <MessageBarActions>
            <Button onClick={draft.restore}>Restore them</Button>
            <Button appearance="subtle" onClick={draft.discard}>
              Discard
            </Button>
          </MessageBarActions>
        </MessageBar>
      )}

      <Section title="What happened">
        <div className={styles.stack}>
          <TextField
            control={form.control}
            name="title"
            label="Title"
            required
            autoFocus
            maxLength={200}
            placeholder="For example: lab reports e-mailed to the wrong patients"
          />
          <TextAreaField
            control={form.control}
            name="description"
            label="What happened"
            required
            rows={4}
            maxLength={4000}
            hint="Its nature, extent, timing and location, as far as they are known now. You can add to it later."
          />
          <FormGrid columns={3} minColumnWidth={220}>
            <DateTimeField
              control={form.control}
              name="awareAt"
              label="When the hospital became aware"
              required
              hint="Hospital time. The 72-hour clock for the Board’s report starts here."
            />
            <DateTimeField control={form.control} name="occurredFrom" label="When it started" hint="If known." />
            <DateTimeField control={form.control} name="occurredTo" label="When it ended" hint="If known." />
          </FormGrid>
          <Controller
            control={form.control}
            name="nature"
            render={({ field }) => (
              <CheckList
                label="What happened to the data"
                options={BREACH_NATURES}
                value={field.value}
                onChange={field.onChange}
                render={(n) => (
                  <span className={styles.checkLabel}>
                    <Text>{breachNatureLabel[n]}</Text>
                    <Caption1 className={styles.muted}>{breachNatureTerm[n]}</Caption1>
                  </span>
                )}
              />
            )}
          />
          <TextAreaField
            control={form.control}
            name="location"
            label="Where"
            rows={2}
            maxLength={500}
            placeholder="The systems, places or suppliers involved"
          />
          <Controller
            control={form.control}
            name="dataCategories"
            render={({ field }) => (
              <CheckList
                label="Personal data affected"
                options={BREACH_DATA_CATEGORIES}
                value={field.value}
                onChange={field.onChange}
                render={(c) => breachDataCategoryLabel[c]}
              />
            )}
          />
          <FormGrid columns={3} minColumnWidth={220}>
            <NumberField
              control={form.control}
              name="estimatedAffected"
              label="How many people, roughly"
              min={0}
              hint="Your best estimate. List the people on the breach page."
            />
          </FormGrid>
        </div>
      </Section>

      <Section
        title="Impact and response"
        description="The Board’s detailed report and the notice to the people affected are built from these answers."
      >
        <div className={styles.stack}>
          <TextAreaField
            control={form.control}
            name="consequences"
            label="Likely consequences for the people affected"
            rows={3}
            maxLength={4000}
            hint="Needed for the report and the notice."
          />
          <TextAreaField
            control={form.control}
            name="mitigation"
            label="What the hospital is doing to reduce the risk"
            rows={3}
            maxLength={4000}
            hint="Needed for the report and the notice."
          />
          <TextAreaField
            control={form.control}
            name="principalSteps"
            label="What people can do to protect themselves"
            rows={3}
            maxLength={4000}
            hint="Needed for the notice. For example: be careful of calls or messages asking for OTPs or bank details."
          />
          <TextAreaField
            control={form.control}
            name="cause"
            label="What led to it"
            rows={3}
            maxLength={4000}
            hint="The events, circumstances and reasons that led to the breach. Needed for the report."
          />
          <TextAreaField
            control={form.control}
            name="findings"
            label="Findings about who caused it (if any)"
            rows={2}
            maxLength={4000}
            hint="Any findings about the person who caused the breach, such as a staff member or a supplier. Leave it empty if there are none yet."
          />
          <TextAreaField
            control={form.control}
            name="remediation"
            label="What stops it happening again"
            rows={3}
            maxLength={4000}
            hint="Needed for the report."
          />
        </div>
      </Section>

      <Section
        title="Notice to the people affected"
        description="Sent by SMS with a link to the full notice in the patient portal. People with an e-mail address also get the full notice by e-mail."
      >
        <TextAreaField
          control={form.control}
          name="principalNotice"
          label="Short notice for SMS"
          rows={3}
          maxLength={NOTICE_MAX_LENGTH}
          placeholder="For example: Nana Hospitals: your lab report was e-mailed to someone else by mistake on 28 Sept. See the link for what we are doing and what you can do."
          hint="Plain words: what happened and where to read more. The link to the full notice is added."
        />
        <Caption1 block className={noticeLength > NOTICE_MAX_LENGTH - 40 ? styles.counterNear : styles.counter}>
          {noticeLength} of {NOTICE_MAX_LENGTH} characters
        </Caption1>
      </Section>

      {(gaps.report.length > 0 || gaps.notice.length > 0) && (
        <MessageBar intent="info" layout="multiline">
          <MessageBarBody>
            <MessageBarTitle>You can save now and add the rest later</MessageBarTitle>
            {gaps.report.length > 0 && <div>Still needed for the Board’s report: {listText(gaps.report)}.</div>}
            {gaps.notice.length > 0 && <div>Still needed before notices can be sent: {listText(gaps.notice)}.</div>}
          </MessageBarBody>
        </MessageBar>
      )}

      <div className={styles.footer}>
        <Caption1 className={styles.muted}>Ctrl+S saves</Caption1>
        <Button onClick={onCancel} disabled={busy}>
          Cancel
        </Button>
        <Button
          type="submit"
          appearance="primary"
          icon={busy ? <Spinner size="tiny" /> : <SaveRegular />}
          disabled={busy}
        >
          {mode.kind === 'new' ? 'Record breach' : 'Save changes'}
        </Button>
      </div>
    </form>
  );
}
