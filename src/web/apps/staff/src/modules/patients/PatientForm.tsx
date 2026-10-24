import {
  Badge,
  Button,
  Caption1,
  Field,
  makeStyles,
  Radio,
  RadioGroup,
  Spinner,
  Text,
  tokens,
} from '@fluentui/react-components';
import { PersonRegular, SaveRegular, WarningRegular } from '@fluentui/react-icons';
import { zodResolver } from '@hookform/resolvers/zod';
import { bloodGroupLabel, humanize, todayIso, useDebouncedValue, useHotkey, type BloodGroup } from '@hc360/core';
import {
  ChoiceField,
  DateField,
  FormGrid,
  type LeaveQuestion,
  NumberField,
  Section,
  SelectField,
  SwitchField,
  TextAreaField,
  TextField,
  useLeaveGuard,
} from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { useEffect, useMemo, type ReactNode } from 'react';
import { Controller, useForm, useWatch, type UseFormReturn } from 'react-hook-form';
import { enumOptions } from '@/lib/lookups';
import {
  GUARDIAN_RELATIONS,
  guardianRelationLabel,
  PATIENT_CATEGORIES,
  patientApi,
  type DuplicateCandidate,
  type DuplicateCheckRequest,
} from './api';
import { PatientPhotoField } from './PatientPhotoField';
import {
  INDIAN_STATES,
  isValidAbhaNumber,
  LANGUAGES,
  patientFormSchema,
  TITLES,
  type PatientFormValues,
} from './validation';

const BLOOD_GROUPS = Object.keys(bloodGroupLabel).filter((g) => g !== 'Unknown') as BloodGroup[];

const useStyles = makeStyles({
  layout: {
    display: 'grid',
    gridTemplateColumns: 'minmax(0, 1fr) 320px',
    gap: '16px',
    alignItems: 'start',
    '@media (max-width: 1100px)': { gridTemplateColumns: 'minmax(0, 1fr)' },
  },
  main: { display: 'flex', flexDirection: 'column', gap: '16px', minWidth: 0 },
  aside: { position: 'sticky', top: '16px', display: 'flex', flexDirection: 'column', gap: '16px' },
  ageRow: { display: 'flex', gap: '12px', alignItems: 'flex-end', flexWrap: 'wrap' },
  footer: {
    position: 'sticky',
    bottom: 0,
    display: 'flex',
    justifyContent: 'flex-end',
    gap: '8px',
    padding: '12px 16px',
    backgroundColor: tokens.colorNeutralBackground1,
    borderTop: `1px solid ${tokens.colorNeutralStroke2}`,
    boxShadow: tokens.shadow8,
    borderRadius: tokens.borderRadiusLarge,
    zIndex: 1,
  },
  candidate: {
    display: 'flex',
    flexDirection: 'column',
    gap: '2px',
    padding: '10px 12px',
    borderRadius: tokens.borderRadiusMedium,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  candidateHeader: { display: 'flex', justifyContent: 'space-between', gap: '8px', alignItems: 'center' },
  muted: { color: tokens.colorNeutralForeground3 },
  list: { display: 'flex', flexDirection: 'column', gap: '8px' },
});

export interface PatientFormProps {
  defaultValues: PatientFormValues;
  onSubmit: (values: PatientFormValues) => void;
  busy: boolean;
  submitLabel: string;
  /** Live duplicate search while registering (off when editing). */
  checkDuplicates?: boolean;
  onOpenCandidate?: (candidate: DuplicateCandidate) => void;
  onFormReady?: (form: UseFormReturn<PatientFormValues>) => void;
  identifierHints?: { abha?: string | null; aadhaarLast4?: string | null };
  extraSections?: ReactNode;
  secondaryActions?: ReactNode;
  /** Asked instead of "Leave without saving?" (registration keeps a draft on this computer). */
  leaveQuestion?: LeaveQuestion;
  /**
   * Show the WhatsApp opt-in switch (registration). For a registered patient it is recorded from their consents, as it
   * belongs to the number rather than to the form.
   */
  askWhatsAppOptIn?: boolean;
}

/**
 * Registration/edit form (SRS FO-01, EHR-01). Keyboard first: Tab moves field to field, Ctrl+S saves. Either the
 * date of birth or the age is captured; identifiers are validated like on the server (ABHA number 14 digits, Aadhaar
 * check digit, ABHA address at @abdm).
 */
export function PatientForm(props: PatientFormProps) {
  const styles = useStyles();
  const form = useForm<PatientFormValues>({
    resolver: zodResolver(patientFormSchema),
    defaultValues: props.defaultValues,
    mode: 'onTouched',
  });
  const { control, handleSubmit } = form;
  const ageMode = useWatch({ control, name: 'ageMode' });
  const phone = useWatch({ control, name: 'phone' });
  const { onFormReady } = props;

  useEffect(() => onFormReady?.(form), [form, onFormReady]);
  useHotkey('mod+s', () => void handleSubmit(props.onSubmit)(), { allowInInputs: true, enabled: !props.busy });
  // Leaving the screen or closing the window with unsaved changes asks first (SRS UX-08).
  useLeaveGuard({ dirty: form.formState.isDirty, busy: props.busy, leaveQuestion: props.leaveQuestion });

  return (
    <form onSubmit={handleSubmit(props.onSubmit)} noValidate>
      <div className={styles.layout}>
        <div className={styles.main}>
          <Section title="Patient details">
            <FormGrid columns={4}>
              <SelectField
                control={control}
                name="title"
                label="Title"
                placeholder="—"
                options={TITLES.map((t) => ({ value: t, label: t }))}
              />
              <TextField
                control={control}
                name="firstName"
                label="First name"
                required
                autoFocus
                autoComplete="off"
                maxLength={100}
              />
              <TextField control={control} name="middleName" label="Middle name" autoComplete="off" maxLength={100} />
              <TextField
                control={control}
                name="lastName"
                label="Last name / surname"
                autoComplete="off"
                maxLength={100}
              />
            </FormGrid>
            <div style={{ height: 12 }} />
            <FormGrid columns={4}>
              <ChoiceField
                control={control}
                name="gender"
                label="Gender"
                required
                options={[
                  { value: 'Male', label: 'Male' },
                  { value: 'Female', label: 'Female' },
                  { value: 'Other', label: 'Other' },
                ]}
              />
              <div style={{ gridColumn: 'span 2' }}>
                <Controller
                  control={control}
                  name="ageMode"
                  render={({ field }) => (
                    <Field label="Date of birth or age" required>
                      <RadioGroup layout="horizontal" value={field.value} onChange={(_, d) => field.onChange(d.value)}>
                        <Radio value="dob" label="Date of birth" />
                        <Radio value="age" label="Age only" />
                      </RadioGroup>
                    </Field>
                  )}
                />
                <div className={styles.ageRow}>
                  {ageMode === 'dob' ? (
                    <DateField
                      control={control}
                      name="dateOfBirth"
                      label="Date of birth"
                      min="1900-01-01"
                      max={todayIso()}
                    />
                  ) : (
                    <>
                      <NumberField control={control} name="ageYears" label="Years" min={0} max={130} />
                      <NumberField control={control} name="ageMonths" label="Months" min={0} max={11} />
                    </>
                  )}
                </div>
              </div>
              <SelectField
                control={control}
                name="bloodGroup"
                label="Blood group"
                placeholder="Not known"
                options={BLOOD_GROUPS.map((g) => ({ value: g, label: bloodGroupLabel[g] }))}
              />
            </FormGrid>
            <div style={{ height: 12 }} />
            <FormGrid columns={4}>
              <SelectField
                control={control}
                name="guardianRelation"
                label="Relation"
                placeholder="—"
                options={GUARDIAN_RELATIONS.map((r) => ({ value: r, label: guardianRelationLabel[r] }))}
              />
              <TextField
                control={control}
                name="guardianName"
                label="Father / husband / guardian name"
                autoComplete="off"
              />
              <SelectField
                control={control}
                name="maritalStatus"
                label="Marital status"
                placeholder="—"
                options={enumOptions(['Single', 'Married', 'Divorced', 'Widowed', 'Separated'] as const)}
              />
              <SelectField
                control={control}
                name="category"
                label="Patient category"
                options={PATIENT_CATEGORIES.map((c) => ({ value: c, label: humanize(c) }))}
              />
            </FormGrid>
          </Section>

          <Section title="Contact">
            <FormGrid columns={4}>
              <TextField
                control={control}
                name="phone"
                label="Mobile number"
                type="tel"
                autoComplete="off"
                hint="Used for SMS/WhatsApp updates and patient portal sign-in"
              />
              <TextField
                control={control}
                name="alternatePhone"
                label="Alternate phone"
                type="tel"
                autoComplete="off"
              />
              <TextField control={control} name="email" label="Email" type="email" autoComplete="off" />
              <SelectField
                control={control}
                name="preferredLanguage"
                label="Preferred language"
                placeholder="—"
                options={LANGUAGES.map((l) => ({ value: l, label: l }))}
              />
            </FormGrid>
            {props.askWhatsAppOptIn && (
              <>
                <div style={{ height: 12 }} />
                <SwitchField
                  control={control}
                  name="whatsAppOptIn"
                  label="Patient agrees to WhatsApp reminders and updates on this number"
                  disabled={!phone.trim()}
                  hint={
                    phone.trim()
                      ? 'Ask the patient. Reminders, feedback requests and offers go on WhatsApp only with this agreement; bills and reports go unless the number replies STOP.'
                      : 'Enter the mobile number first.'
                  }
                />
                <div style={{ height: 12 }} />
                <SwitchField
                  control={control}
                  name="privacyNoticeGiven"
                  label="Patient was given the privacy notice"
                  hint="Hand over or show the notice: what the hospital records, why, and whom to contact. HC360 records which version they had."
                />
              </>
            )}
            <div style={{ height: 12 }} />
            <FormGrid columns={3}>
              <TextField control={control} name="address.line1" label="House / street" autoComplete="off" />
              <TextField control={control} name="address.line2" label="Area / locality" autoComplete="off" />
              <TextField control={control} name="address.city" label="City / town / village" autoComplete="off" />
              <TextField control={control} name="address.district" label="District" autoComplete="off" />
              <SelectField
                control={control}
                name="address.state"
                label="State"
                placeholder="—"
                options={INDIAN_STATES.map((s) => ({ value: s, label: s }))}
              />
              <TextField control={control} name="address.pinCode" label="PIN code" autoComplete="off" maxLength={6} />
            </FormGrid>
          </Section>

          <Section title="Emergency contact">
            <FormGrid columns={3}>
              <TextField control={control} name="emergencyContactName" label="Name" autoComplete="off" />
              <TextField control={control} name="emergencyContactPhone" label="Phone" type="tel" autoComplete="off" />
              <TextField control={control} name="emergencyContactRelation" label="Relationship" autoComplete="off" />
            </FormGrid>
          </Section>

          <Section
            title="Health IDs"
            description="ABHA is stored encrypted. For Aadhaar only the last 4 digits are kept, never the full number."
          >
            <FormGrid columns={3}>
              <TextField
                control={control}
                name="abhaNumber"
                label="ABHA number"
                autoComplete="off"
                maxLength={17}
                hint={
                  props.identifierHints?.abha
                    ? `On file: ${props.identifierHints.abha}. Leave blank to keep it.`
                    : '14 digits, as on the ABHA card: 91-1234-5678-9010'
                }
              />
              <TextField
                control={control}
                name="abhaAddress"
                label="ABHA address"
                autoComplete="off"
                hint="e.g. name@abdm (name@sbx on the ABDM sandbox)"
              />
              <TextField
                control={control}
                name="aadhaarNumber"
                label="Aadhaar number"
                autoComplete="off"
                maxLength={14}
                hint={
                  props.identifierHints?.aadhaarLast4
                    ? `On file: XXXX XXXX ${props.identifierHints.aadhaarLast4}. Leave blank to keep it.`
                    : 'Optional. Used only to avoid duplicate records.'
                }
              />
            </FormGrid>
          </Section>

          <Section title="Other">
            <FormGrid columns={3}>
              <TextField control={control} name="occupation" label="Occupation" autoComplete="off" />
              <TextField control={control} name="religion" label="Religion" autoComplete="off" />
              <TextField control={control} name="nationality" label="Nationality" autoComplete="off" />
            </FormGrid>
            <div style={{ height: 12 }} />
            <SwitchField
              control={control}
              name="isVip"
              label="Restricted record (VIP, staff or high-profile patient)"
              hint="Only authorised staff can open it, and every access is audited."
            />
            <TextAreaField control={control} name="notes" label="Notes for the front desk" rows={2} maxLength={2000} />
          </Section>

          {props.extraSections}
        </div>

        <aside className={styles.aside}>
          <Section title="Photo">
            <PhotoBlock control={control} />
          </Section>
          {props.checkDuplicates && <DuplicatePanel control={control} onOpen={props.onOpenCandidate} />}
        </aside>
      </div>

      <div className={styles.footer}>
        {props.secondaryActions}
        <Button
          type="submit"
          appearance="primary"
          size="large"
          icon={props.busy ? <Spinner size="tiny" aria-hidden /> : <SaveRegular />}
          disabled={props.busy}
        >
          {props.submitLabel}
        </Button>
      </div>
    </form>
  );
}

/** The patient's photo (SRS FO-01). The initials shown until there is a photo follow the name as it is typed. */
function PhotoBlock({ control }: { control: UseFormReturn<PatientFormValues>['control'] }) {
  const [firstName, lastName] = useWatch({ control, name: ['firstName', 'lastName'] });
  return (
    <Controller
      control={control}
      name="photoFileId"
      render={({ field, fieldState }) => (
        <PatientPhotoField
          value={field.value}
          onChange={field.onChange}
          name={`${firstName} ${lastName}`}
          error={fieldState.error?.message}
        />
      )}
    />
  );
}

/** Live "may already be registered" check as the front desk types (SRS FO-02). */
function DuplicatePanel({
  control,
  onOpen,
}: {
  control: UseFormReturn<PatientFormValues>['control'];
  onOpen?: (candidate: DuplicateCandidate) => void;
}) {
  const styles = useStyles();
  const [firstName, lastName, dateOfBirth, phone, abhaNumber, aadhaarNumber] = useWatch({
    control,
    name: ['firstName', 'lastName', 'dateOfBirth', 'phone', 'abhaNumber', 'aadhaarNumber'],
  });
  // Debounce a string (not a fresh object each render) so the timer only restarts when the input really changes.
  const key = useDebouncedValue(
    JSON.stringify({
      firstName: firstName.trim() || null,
      lastName: lastName.trim() || null,
      dateOfBirth: dateOfBirth || null,
      phone: phone.replace(/\D/g, '').length >= 10 ? phone : null,
      abhaNumber: isValidAbhaNumber(abhaNumber) ? abhaNumber : null,
      aadhaarNumber: aadhaarNumber.replace(/\D/g, '').length === 12 ? aadhaarNumber : null,
    }),
    500,
  );
  const request = useMemo(() => JSON.parse(key) as DuplicateCheckRequest, [key]);
  const enough =
    Boolean(request.abhaNumber || request.aadhaarNumber || request.phone) ||
    Boolean(request.firstName && request.firstName.length >= 2 && (request.lastName || request.dateOfBirth));

  const duplicates = useQuery({
    queryKey: ['patients', 'duplicates', request],
    queryFn: ({ signal }) => patientApi.duplicates(request, signal),
    enabled: enough,
    staleTime: 30_000,
  });

  const candidates = duplicates.data ?? [];

  return (
    <Section
      title="Possible matches"
      description={
        enough
          ? 'Existing patients with similar details'
          : 'Type the name with the mobile number or date of birth to check for existing records.'
      }
    >
      {duplicates.isFetching && <Spinner size="tiny" label="Checking…" labelPosition="after" />}
      {enough && !duplicates.isFetching && candidates.length === 0 && (
        <Text className={styles.muted}>
          <PersonRegular /> No existing patient matches.
        </Text>
      )}
      <div className={styles.list}>
        {candidates.map((c) => (
          <div key={c.id} className={styles.candidate}>
            <div className={styles.candidateHeader}>
              <Text weight="semibold">{c.fullName}</Text>
              <Badge
                appearance="tint"
                color={c.score >= 80 ? 'danger' : 'warning'}
                icon={c.score >= 80 ? <WarningRegular /> : undefined}
              >
                {c.score >= 80 ? 'Likely same' : 'Similar'}
              </Badge>
            </div>
            <Caption1>
              {c.uhid} · {c.age ?? '—'} / {c.gender.charAt(0)}
              {c.phoneMasked ? ` · ${c.phoneMasked}` : ''}
            </Caption1>
            <Caption1 className={styles.muted}>{c.reason}</Caption1>
            {onOpen && (
              <div>
                <Button size="small" appearance="subtle" onClick={() => onOpen(c)}>
                  Open this record
                </Button>
              </div>
            )}
          </div>
        ))}
      </div>
    </Section>
  );
}
