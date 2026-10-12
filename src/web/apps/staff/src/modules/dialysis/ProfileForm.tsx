import {
  Button,
  Caption1,
  makeStyles,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  Spinner,
  tokens,
} from '@fluentui/react-components';
import { SaveRegular } from '@fluentui/react-icons';
import { zodResolver } from '@hookform/resolvers/zod';
import { todayIso } from '@hc360/core';
import {
  ChoiceField,
  DateField,
  FormGrid,
  FullRow,
  LookupField,
  NumberField,
  Section,
  SelectField,
  SwitchField,
  TextAreaField,
  TextField,
  useLeaveGuard,
  useNotify,
} from '@hc360/ui';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useForm, useWatch } from 'react-hook-form';
import { z } from 'zod';
import { searchPractitioners } from '@/lib/lookups';
import { applyFormErrors } from '@/modules/ot/shared/errors';
import { ActionError } from '@/modules/ot/shared/feedback';
import { CheckboxGroupField } from '@/modules/ot/shared/fields';
import { WEEKDAYS } from '@/modules/ot/shared/types';
import {
  dialysisApi,
  dialysisKeys,
  DIALYSIS_SHIFTS,
  REUSE_POLICIES,
  SERO_STATUSES,
  VASCULAR_ACCESS_TYPES,
  type DialysisProfileDto,
} from './api';
import { accessLabel, reuseLabel, seroLabel, shiftLabel, weekdayShort } from './labels';
import { effectiveMaxUses, isRescreeningDue } from './logic';

const useStyles = makeStyles({
  stack: { display: 'flex', flexDirection: 'column', gap: '16px' },
  footer: { display: 'flex', justifyContent: 'flex-end', gap: '8px' },
  muted: { color: tokens.colorNeutralForeground3 },
});

const profileSchema = z
  .object({
    dryWeightKg: z.number().nullable(),
    vascularAccess: z.enum(VASCULAR_ACCESS_TYPES),
    accessSite: z.string().trim().max(200),
    accessCreatedOn: z.string().nullable(),
    scheduleDays: z
      .array(z.enum(['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday']))
      .min(1, 'Choose the dialysis days.'),
    shift: z.enum(DIALYSIS_SHIFTS),
    sessionMinutes: z.number().nullable(),
    seroStatus: z.enum(SERO_STATUSES),
    seroTestedOn: z.string().nullable(),
    reusePolicy: z.enum(REUSE_POLICIES),
    maxDialyzerUses: z.number().nullable(),
    nephrologistId: z.string().nullable(),
    primaryDiagnosis: z.string().trim().max(300),
    anticoagulationNotes: z.string().trim().max(1000),
    isActive: z.boolean(),
  })
  .superRefine((v, ctx) => {
    if (v.dryWeightKg === null || v.dryWeightKg < 10 || v.dryWeightKg > 250) {
      ctx.addIssue({ code: 'custom', path: ['dryWeightKg'], message: 'Enter a dry weight between 10 and 250 kg.' });
    }
    if (v.sessionMinutes === null || v.sessionMinutes < 60 || v.sessionMinutes > 720) {
      ctx.addIssue({ code: 'custom', path: ['sessionMinutes'], message: 'Enter 60 to 720 minutes.' });
    }
    if (!v.seroTestedOn) {
      ctx.addIssue({
        code: 'custom',
        path: ['seroTestedOn'],
        message: 'Enter the date of the sero test (HBsAg, HCV, HIV).',
      });
    } else if (v.seroTestedOn > todayIso()) {
      ctx.addIssue({ code: 'custom', path: ['seroTestedOn'], message: 'The sero test date cannot be in the future.' });
    }
    if (v.reusePolicy === 'Reuse' && (v.maxDialyzerUses === null || v.maxDialyzerUses < 1 || v.maxDialyzerUses > 20)) {
      ctx.addIssue({ code: 'custom', path: ['maxDialyzerUses'], message: 'Enter 1 to 20 uses.' });
    }
  });

type ProfileValues = z.infer<typeof profileSchema>;

function toValues(profile: DialysisProfileDto | null): ProfileValues {
  return {
    dryWeightKg: profile?.dryWeightKg ?? null,
    vascularAccess: profile?.vascularAccess ?? 'AVF',
    accessSite: profile?.accessSite ?? '',
    accessCreatedOn: profile?.accessCreatedOn ?? null,
    scheduleDays: profile?.scheduleDays ?? ['Monday', 'Wednesday', 'Friday'],
    shift: profile?.shift ?? 'Morning',
    sessionMinutes: profile?.sessionMinutes ?? 240,
    seroStatus: profile?.seroStatus ?? 'Negative',
    seroTestedOn: profile?.seroTestedOn ?? null,
    reusePolicy: profile?.reusePolicy ?? 'Reuse',
    maxDialyzerUses: profile?.maxDialyzerUses ?? 8,
    nephrologistId: profile?.nephrologistId ?? null,
    primaryDiagnosis: profile?.primaryDiagnosis ?? '',
    anticoagulationNotes: profile?.anticoagulationNotes ?? '',
    isActive: profile?.isActive ?? true,
  };
}

/**
 * Enrolment in the dialysis unit (DIA-01): dry weight, weekly slot, vascular access, dialyzer reuse and — most
 * important for infection control — the sero status, which decides the machines the patient may use.
 */
export function ProfileForm({
  patientId,
  profile,
  onSaved,
}: {
  patientId: string;
  profile: DialysisProfileDto | null;
  onSaved?: () => void;
}) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const form = useForm<ProfileValues>({ resolver: zodResolver(profileSchema), defaultValues: toValues(profile) });
  const [seroStatus, reusePolicy, maxDialyzerUses, seroTestedOn] = useWatch({
    control: form.control,
    name: ['seroStatus', 'reusePolicy', 'maxDialyzerUses', 'seroTestedOn'],
  });
  const [nephrologistItems] = useState(() =>
    profile?.nephrologistId && profile.nephrologistName
      ? [{ id: profile.nephrologistId, code: '', name: profile.nephrologistName }]
      : undefined,
  );
  const effective = effectiveMaxUses(reusePolicy, seroStatus, maxDialyzerUses ?? 1);
  const seroChanged = profile !== null && profile.seroStatus !== seroStatus;
  const rescreenDue = seroTestedOn ? isRescreeningDue(seroTestedOn, todayIso()) : false;

  const save = useMutation({
    mutationFn: (v: ProfileValues) =>
      dialysisApi.saveProfile(patientId, {
        dryWeightKg: v.dryWeightKg ?? 0,
        vascularAccess: v.vascularAccess,
        accessSite: v.accessSite || null,
        accessCreatedOn: v.accessCreatedOn,
        scheduleDays: v.scheduleDays,
        shift: v.shift,
        sessionMinutes: v.sessionMinutes ?? 240,
        seroStatus: v.seroStatus,
        seroTestedOn: v.seroTestedOn ?? todayIso(),
        reusePolicy: v.reusePolicy,
        maxDialyzerUses: v.reusePolicy === 'Reuse' ? (v.maxDialyzerUses ?? 1) : 1,
        nephrologistId: v.nephrologistId,
        primaryDiagnosis: v.primaryDiagnosis || null,
        anticoagulationNotes: v.anticoagulationNotes || null,
        isActive: v.isActive,
        version: profile?.version ?? null,
      }),
    onSuccess: (_, values) => {
      // What the form shows is saved now: leaving does not ask (SRS UX-08).
      form.reset(values);
      notify.success(profile ? 'Dialysis profile saved' : 'Patient enrolled in the dialysis unit');
      void queryClient.invalidateQueries({ queryKey: dialysisKeys.all });
      onSaved?.();
    },
    onError: (error) => {
      applyFormErrors(error, form.setError);
    },
  });

  // Leaving the screen or closing the window with unsaved changes asks first (SRS UX-08).
  useLeaveGuard({ dirty: form.formState.isDirty, busy: save.isPending });

  return (
    <form onSubmit={form.handleSubmit((v) => save.mutate(v))} noValidate className={styles.stack}>
      <Section
        title="Sero status and machine segregation"
        description="Hepatitis B, hepatitis C and HIV spread through shared machines, so each machine serves one category only."
      >
        <FormGrid columns={2}>
          <FullRow>
            <ChoiceField
              control={form.control}
              name="seroStatus"
              label="Sero status"
              required
              options={SERO_STATUSES.map((s) => ({ value: s, label: seroLabel[s] }))}
            />
          </FullRow>
          <DateField
            control={form.control}
            name="seroTestedOn"
            label="Tested on"
            required
            max={todayIso()}
            hint="Sero-negative patients are re-screened every 3 months."
          />
        </FormGrid>
        {seroChanged && (
          <MessageBar intent="warning" layout="multiline" style={{ marginTop: 12 }}>
            <MessageBarBody>
              <MessageBarTitle>Changing the sero status changes the machines this patient may use</MessageBarTitle>
              From now on only {seroLabel[seroStatus].toLowerCase()} machines can be assigned. Sessions already booked
              on a machine of another category are refused when they start — reassign them.
            </MessageBarBody>
          </MessageBar>
        )}
        {rescreenDue && !seroChanged && (
          <MessageBar intent="warning" style={{ marginTop: 12 }}>
            <MessageBarBody>
              The sero test is more than 3 months old. Arrange re-screening and update the date.
            </MessageBarBody>
          </MessageBar>
        )}
      </Section>

      <Section title="Dialysis prescription">
        <FormGrid columns={3}>
          <NumberField
            control={form.control}
            name="dryWeightKg"
            label="Dry weight (kg)"
            required
            min={10}
            max={250}
            step={0.1}
            hint="Target weight after dialysis."
          />
          <SelectField
            control={form.control}
            name="shift"
            label="Usual shift"
            options={DIALYSIS_SHIFTS.map((s) => ({ value: s, label: shiftLabel[s] }))}
          />
          <NumberField
            control={form.control}
            name="sessionMinutes"
            label="Session length (minutes)"
            required
            min={60}
            max={720}
            step={15}
          />
          <FullRow>
            <CheckboxGroupField
              control={form.control}
              name="scheduleDays"
              label="Dialysis days"
              required
              options={WEEKDAYS.map((d) => ({ value: d, label: weekdayShort[d] }))}
              hint="Patients due on a day but not booked show on the board."
            />
          </FullRow>
        </FormGrid>
      </Section>

      <Section title="Vascular access">
        <FormGrid columns={3}>
          <SelectField
            control={form.control}
            name="vascularAccess"
            label="Access"
            options={VASCULAR_ACCESS_TYPES.map((a) => ({ value: a, label: accessLabel[a] }))}
          />
          <TextField
            control={form.control}
            name="accessSite"
            label="Site"
            maxLength={200}
            placeholder="e.g. Left radio-cephalic AVF"
          />
          <DateField control={form.control} name="accessCreatedOn" label="Created on" max={todayIso()} />
        </FormGrid>
      </Section>

      <Section title="Dialyzer reuse">
        <FormGrid columns={2}>
          <ChoiceField
            control={form.control}
            name="reusePolicy"
            label="Policy"
            options={REUSE_POLICIES.map((p) => ({ value: p, label: reuseLabel[p] }))}
          />
          <NumberField
            control={form.control}
            name="maxDialyzerUses"
            label="Maximum uses per dialyzer"
            min={1}
            max={20}
            disabled={reusePolicy === 'SingleUse' || seroStatus === 'HBsAg'}
          />
        </FormGrid>
        <Caption1 className={styles.muted}>
          {seroStatus === 'HBsAg'
            ? 'HBsAg-positive patients never take part in reuse: every session uses a new dialyzer.'
            : `Each dialyzer is used ${effective === 1 ? 'once' : `up to ${effective} times`}, only for this patient.`}
        </Caption1>
      </Section>

      <Section title="Care">
        <FormGrid columns={2}>
          <LookupField
            control={form.control}
            name="nephrologistId"
            label="Nephrologist"
            load={searchPractitioners}
            initialItems={nephrologistItems}
            placeholder="Type a name"
          />
          <TextField
            control={form.control}
            name="primaryDiagnosis"
            label="Primary diagnosis"
            maxLength={300}
            placeholder="e.g. CKD stage 5 — diabetic nephropathy"
          />
          <FullRow>
            <TextAreaField
              control={form.control}
              name="anticoagulationNotes"
              label="Anticoagulation notes"
              rows={2}
              maxLength={1000}
            />
          </FullRow>
          <SwitchField
            control={form.control}
            name="isActive"
            label="Active in the dialysis unit"
            hint="Inactive patients are not shown as due and cannot be booked."
          />
        </FormGrid>
      </Section>

      <ActionError error={save.error} />
      <div className={styles.footer}>
        <Button
          type="submit"
          appearance="primary"
          size="large"
          icon={save.isPending ? <Spinner size="tiny" /> : <SaveRegular />}
          disabled={save.isPending}
        >
          {profile ? 'Save profile' : 'Enrol patient'}
        </Button>
      </div>
    </form>
  );
}
