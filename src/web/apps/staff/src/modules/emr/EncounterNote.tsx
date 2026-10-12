import {
  Button,
  Caption1,
  Combobox,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Field,
  makeStyles,
  Option,
  OptionGroup,
  Spinner,
  Text,
  Textarea,
  tokens,
} from '@fluentui/react-components';
import { AddRegular, CalendarRegular } from '@fluentui/react-icons';
import { formatDateOnly, formatDateTime } from '@hc360/core';
import { DateField, FormGrid, SelectField, TextField, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { Controller, useWatch, type Control } from 'react-hook-form';
import { toOptions, useDepartments, usePractitioners } from '@/lib/lookups';
import { emrQueries, type EncounterDetail, type TemplateDto, type TemplateListItem } from './api';
import {
  addDaysIso,
  appendComplaint,
  appendText,
  applyTemplate,
  daysBetween,
  encounterFieldLabel,
  FOLLOW_UP_PRESETS,
  MIN_AMENDMENT_REASON,
  templateConflicts,
  type EncounterFormValues,
} from './encounterForm';
import { DictateButton } from './ui';
import type { EncounterNote } from './useEncounterNote';

const useStyles = makeStyles({
  stack: { display: 'flex', flexDirection: 'column', gap: '12px' },
  chips: { display: 'flex', gap: '6px', flexWrap: 'wrap', alignItems: 'center' },
  muted: { color: tokens.colorNeutralForeground3 },
  labelRow: { display: 'flex', justifyContent: 'space-between', alignItems: 'center', gap: '8px' },
  presets: { display: 'flex', gap: '6px', flexWrap: 'wrap', alignItems: 'flex-end' },
});

function NoteArea({
  control,
  name,
  label,
  rows,
  maxLength,
  disabled,
  placeholder,
  autoFocus,
}: {
  control: Control<EncounterFormValues>;
  name: 'chiefComplaint' | 'historyOfPresentIllness' | 'examination' | 'assessment' | 'plan' | 'followUpAdvice';
  label: string;
  rows: number;
  maxLength: number;
  disabled: boolean;
  placeholder?: string;
  autoFocus?: boolean;
}) {
  const styles = useStyles();
  return (
    <Controller
      control={control}
      name={name}
      render={({ field, fieldState }) => (
        <div>
          {!disabled && (
            <div className={styles.labelRow}>
              <span />
              <DictateButton onText={(text) => field.onChange(appendText(field.value, text))} />
            </div>
          )}
          <Field
            label={label}
            validationMessage={fieldState.error?.message}
            validationState={fieldState.error ? 'error' : 'none'}
          >
            <Textarea
              {...field}
              rows={rows}
              resize="vertical"
              maxLength={maxLength}
              disabled={disabled}
              placeholder={placeholder}
              autoFocus={autoFocus}
            />
          </Field>
        </div>
      )}
    />
  );
}

function TemplatePicker({ disabled, onPick }: { disabled: boolean; onPick: (template: TemplateListItem) => void }) {
  const templates = useQuery(emrQueries.templates());
  const [term, setTerm] = useState('');
  const items = (templates.data ?? []).filter(
    (t) =>
      !term.trim() || `${t.name} ${t.code} ${t.specialtyCode ?? ''}`.toLowerCase().includes(term.trim().toLowerCase()),
  );
  const groups = [...new Set(items.map((t) => (t.isPersonal ? 'My templates' : (t.specialtyCode ?? 'General'))))];
  return (
    <Field label="Template" hint="Pre-fills the note; you can edit everything">
      <Combobox
        freeform
        disabled={disabled}
        placeholder={templates.isLoading ? 'Loading…' : 'Choose a template'}
        value={term}
        selectedOptions={[]}
        onChange={(event) => setTerm(event.target.value)}
        onOptionSelect={(_, d) => {
          const picked = items.find((t) => t.id === d.optionValue);
          if (picked) {
            onPick(picked);
          }
          setTerm('');
        }}
      >
        {groups.map((group) => (
          <OptionGroup key={group} label={group}>
            {items
              .filter((t) => (t.isPersonal ? 'My templates' : (t.specialtyCode ?? 'General')) === group)
              .map((t) => (
                <Option key={t.id} value={t.id} text={t.name}>
                  {t.name}
                </Option>
              ))}
          </OptionGroup>
        ))}
      </Combobox>
    </Field>
  );
}

/** Chief complaint and history, with speciality templates and one-click common complaints. */
export function ComplaintSection({
  note,
  today,
  activeTemplate,
  onTemplateLoaded,
}: {
  note: EncounterNote;
  today: string;
  activeTemplate: TemplateDto | null;
  onTemplateLoaded: (template: TemplateDto) => void;
}) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const { form, locked } = note;
  const [pending, setPending] = useState<{ template: TemplateDto; conflicts: string[] } | null>(null);

  const apply = (template: TemplateDto, mode: 'fill-empty' | 'replace') => {
    form.reset(applyTemplate(form.getValues(), template, mode, today), { keepDefaultValues: true });
    onTemplateLoaded(template);
    setPending(null);
    notify.success(
      `Template "${template.name}" applied`,
      template.defaultOrders.length + template.medications.length > 0
        ? 'Its investigations and medicines can be added below.'
        : undefined,
    );
  };

  const load = useMutation({
    mutationFn: (id: string) => queryClient.fetchQuery(emrQueries.template(id)),
    onSuccess: (template) => {
      const conflicts = templateConflicts(form.getValues(), template);
      if (conflicts.length > 0) {
        setPending({ template, conflicts });
      } else {
        apply(template, 'fill-empty');
      }
    },
    onError: (error) => notify.error(error, 'Could not load the template'),
  });

  return (
    <div className={styles.stack}>
      {!locked && (
        <FormGrid columns={2}>
          <TemplatePicker disabled={load.isPending} onPick={(t) => load.mutate(t.id)} />
          <div />
        </FormGrid>
      )}
      {!locked && activeTemplate && activeTemplate.commonComplaints.length > 0 && (
        <div className={styles.chips} role="group" aria-label="Common complaints">
          <Caption1 className={styles.muted}>Common:</Caption1>
          {activeTemplate.commonComplaints.map((complaint) => (
            <Button
              key={complaint}
              size="small"
              icon={<AddRegular />}
              onClick={() =>
                form.setValue('chiefComplaint', appendComplaint(form.getValues('chiefComplaint'), complaint), {
                  shouldDirty: true,
                })
              }
            >
              {complaint}
            </Button>
          ))}
        </div>
      )}
      <NoteArea
        control={form.control}
        name="chiefComplaint"
        label="Chief complaint"
        rows={2}
        maxLength={1000}
        disabled={locked}
        placeholder="e.g. Fever for 3 days with cough"
        autoFocus={!locked}
      />
      <NoteArea
        control={form.control}
        name="historyOfPresentIllness"
        label="History of present illness"
        rows={5}
        maxLength={8000}
        disabled={locked}
      />
      <Dialog open={pending !== null} onOpenChange={(_, d) => !d.open && setPending(null)}>
        <DialogSurface>
          <DialogBody>
            <DialogTitle>Apply "{pending?.template.name}"?</DialogTitle>
            <DialogContent>
              <Text>
                You have already written{' '}
                {pending?.conflicts
                  .map((c) => encounterFieldLabel[c as keyof typeof encounterFieldLabel]?.toLowerCase() ?? c)
                  .join(', ')}
                . Keep your text and fill only the empty parts, or replace those parts with the template?
              </Text>
            </DialogContent>
            <DialogActions>
              <Button onClick={() => setPending(null)}>Cancel</Button>
              <Button onClick={() => pending && apply(pending.template, 'replace')}>Replace</Button>
              <Button appearance="primary" onClick={() => pending && apply(pending.template, 'fill-empty')}>
                Keep my text, fill the rest
              </Button>
            </DialogActions>
          </DialogBody>
        </DialogSurface>
      </Dialog>
    </div>
  );
}

export function ExaminationSection({ note }: { note: EncounterNote }) {
  const styles = useStyles();
  const { form, locked } = note;
  return (
    <div className={styles.stack}>
      <NoteArea
        control={form.control}
        name="examination"
        label="Examination"
        rows={4}
        maxLength={8000}
        disabled={locked}
        placeholder="General and systemic examination"
      />
      <NoteArea
        control={form.control}
        name="assessment"
        label="Assessment"
        rows={3}
        maxLength={4000}
        disabled={locked}
        placeholder="Clinical impression"
      />
      <NoteArea
        control={form.control}
        name="plan"
        label="Plan"
        rows={4}
        maxLength={8000}
        disabled={locked}
        placeholder="Investigations, treatment and counselling"
      />
    </div>
  );
}

/** Follow-up date (one click for common intervals) and internal or external referral. */
export function FollowUpSection({ note, today }: { note: EncounterNote; today: string }) {
  const styles = useStyles();
  const { form, locked } = note;
  const [departmentId, followUpDate] = useWatch({
    control: form.control,
    name: ['referralDepartmentId', 'followUpDate'],
  });
  const departments = useDepartments('Clinical');
  const practitioners = usePractitioners(departmentId);
  return (
    <div className={styles.stack}>
      <div className={styles.presets}>
        <DateField
          control={form.control}
          name="followUpDate"
          label="Follow-up on"
          min={today}
          max={addDaysIso(today, 730)}
          disabled={locked}
        />
        {!locked &&
          FOLLOW_UP_PRESETS.map((preset) => (
            <Button
              key={preset.days}
              size="small"
              icon={<CalendarRegular />}
              onClick={() =>
                form.setValue('followUpDate', addDaysIso(today, preset.days), {
                  shouldDirty: true,
                  shouldValidate: true,
                })
              }
            >
              {preset.label}
            </Button>
          ))}
        {!locked && followUpDate && (
          <Button
            size="small"
            appearance="subtle"
            onClick={() => form.setValue('followUpDate', null, { shouldDirty: true })}
          >
            No follow-up
          </Button>
        )}
      </div>
      {followUpDate && (
        <Caption1 className={styles.muted}>
          {formatDateOnly(followUpDate)} · in {daysBetween(today, followUpDate)} days
        </Caption1>
      )}
      <NoteArea
        control={form.control}
        name="followUpAdvice"
        label="Follow-up advice"
        rows={2}
        maxLength={1000}
        disabled={locked}
        placeholder="e.g. Review with CBC report; come earlier if fever persists"
      />
      <FormGrid columns={3}>
        <SelectField
          control={form.control}
          name="referralDepartmentId"
          label="Refer to department"
          placeholder="No internal referral"
          options={toOptions(departments.data)}
          disabled={locked}
        />
        <SelectField
          control={form.control}
          name="referralPractitionerId"
          label="Refer to doctor"
          placeholder={departmentId ? 'Any doctor' : 'Choose a department first'}
          options={toOptions(practitioners.data)}
          disabled={locked || !departmentId}
        />
        <TextField
          control={form.control}
          name="referralExternal"
          label="Or refer outside"
          placeholder="Hospital or specialist"
          maxLength={300}
          disabled={locked}
        />
      </FormGrid>
      <TextField
        control={form.control}
        name="referralReason"
        label="Reason for referral"
        maxLength={1000}
        disabled={locked}
        hint="Required when referring. The referred doctor is notified when you sign."
      />
    </div>
  );
}

/** Asks for the amendment reason (at least 10 characters) and records the amendment. */
export function AmendDialog({
  open,
  busy,
  onConfirm,
  onCancel,
}: {
  open: boolean;
  busy: boolean;
  onConfirm: (reason: string) => void;
  onCancel: () => void;
}) {
  return (
    <Dialog open={open} onOpenChange={(_, d) => !d.open && !busy && onCancel()} modalType="alert">
      <DialogSurface>{open && <AmendBody busy={busy} onConfirm={onConfirm} onCancel={onCancel} />}</DialogSurface>
    </Dialog>
  );
}

function AmendBody({
  busy,
  onConfirm,
  onCancel,
}: {
  busy: boolean;
  onConfirm: (reason: string) => void;
  onCancel: () => void;
}) {
  const [reason, setReason] = useState('');
  const length = reason.trim().length;
  return (
    <DialogBody>
      <DialogTitle>Record the amendment</DialogTitle>
      <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
        <Text>
          The signed note is replaced by your corrected text. The original is kept word for word in the amendment
          history with your name and reason.
        </Text>
        <Field
          label="What is corrected and why"
          required
          hint={`At least ${MIN_AMENDMENT_REASON} characters (${length} so far).`}
        >
          <Textarea
            autoFocus
            value={reason}
            onChange={(_, d) => setReason(d.value.slice(0, 500))}
            rows={3}
            resize="vertical"
          />
        </Field>
      </DialogContent>
      <DialogActions>
        <Button onClick={onCancel} disabled={busy}>
          Go back
        </Button>
        <Button
          appearance="primary"
          disabled={length < MIN_AMENDMENT_REASON || busy}
          icon={busy ? <Spinner size="tiny" /> : undefined}
          onClick={() => onConfirm(reason.trim())}
        >
          Record amendment
        </Button>
      </DialogActions>
    </DialogBody>
  );
}

/** Previous versions of an amended encounter (read-only, newest first). */
export function AmendmentHistory({ encounter }: { encounter: EncounterDetail }) {
  const styles = useStyles();
  if (encounter.amendments.length === 0) {
    return null;
  }
  return (
    <div className={styles.stack}>
      {encounter.amendments.map((a) => {
        let previous: Partial<Record<string, string | null>> = {};
        try {
          previous = JSON.parse(a.previousContent) as Partial<Record<string, string | null>>;
        } catch {
          previous = {};
        }
        return (
          <details key={a.id}>
            <summary>
              <Text weight="semibold">{formatDateTime(a.amendedAt)}</Text> · {a.reason}
            </summary>
            <div className={styles.stack} style={{ padding: '8px 0 0 16px' }}>
              {Object.entries(encounterFieldLabel).map(([key, label]) =>
                previous[key] ? (
                  <div key={key}>
                    <Caption1 className={styles.muted}>{label} (before)</Caption1>
                    <Text block style={{ whiteSpace: 'pre-wrap' }}>
                      {previous[key]}
                    </Text>
                  </div>
                ) : null,
              )}
            </div>
          </details>
        );
      })}
    </div>
  );
}
