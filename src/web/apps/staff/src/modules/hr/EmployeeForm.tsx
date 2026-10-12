import { Button, Checkbox, makeStyles, MessageBar, MessageBarBody, Spinner, tokens } from '@fluentui/react-components';
import { SaveRegular, ShieldKeyholeRegular } from '@fluentui/react-icons';
import { zodResolver } from '@hookform/resolvers/zod';
import { humanize, useHotkey } from '@hc360/core';
import {
  ChoiceField,
  DateField,
  FormGrid,
  LookupField,
  Section,
  SelectField,
  SwitchField,
  TextAreaField,
  TextField,
  useLeaveGuard,
} from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { useEffect, type ReactNode } from 'react';
import { Controller, useForm, useWatch, type Control, type UseFormReturn } from 'react-hook-form';
import { searchPractitioners, toOptions, useDepartments } from '@/lib/lookups';
import { usePermission } from '@/lib/permissions';
import { hrQueries } from './api';
import { EmployeePicker } from './EmployeePicker';
import { employeeFormSchema, type EmployeeFormValues } from './employeeModel';
import {
  EDITABLE_EMPLOYEE_STATUSES,
  EMPLOYMENT_TYPES,
  employeeStatusLabel,
  PAYMENT_MODES,
  TITLES,
  WEEKDAYS,
} from './labels';
import { UserPicker } from './shared/UserPicker';
import { USER_DIRECTORY_PERMISSION, useUserName } from './shared/users';

const useStyles = makeStyles({
  form: { display: 'flex', flexDirection: 'column', gap: '16px' },
  gap: { height: '12px' },
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
});

export interface EmployeeFormProps {
  defaultValues: EmployeeFormValues;
  onSubmit: (values: EmployeeFormValues) => void;
  busy: boolean;
  submitLabel: string;
  /** Masked identifiers already on file (edit): shown as hints, never as values. */
  onFile?: { pan?: string | null };
  onFormReady?: (form: UseFormReturn<EmployeeFormValues>) => void;
  secondaryActions?: ReactNode;
  /** The employee being edited (so they are not offered as their own manager). */
  employeeId?: string;
}

/** Employee master form (HR-01). Keyboard first: Tab moves field to field, Ctrl+S saves. */
export function EmployeeForm(props: EmployeeFormProps) {
  const styles = useStyles();
  const form = useForm<EmployeeFormValues>({
    resolver: zodResolver(employeeFormSchema),
    defaultValues: props.defaultValues,
    mode: 'onTouched',
  });
  const { control, handleSubmit } = form;
  const [paymentMode, isNew, removePan] = useWatch({ control, name: ['paymentMode', 'isNew', 'removePan'] });
  const departments = useDepartments();
  const designations = useQuery(hrQueries.designations());
  const shifts = useQuery(hrQueries.shifts());
  const canPickUsers = usePermission(USER_DIRECTORY_PERMISSION);
  const { onFormReady } = props;

  useEffect(() => onFormReady?.(form), [form, onFormReady]);
  useHotkey('mod+s', () => void handleSubmit(props.onSubmit)(), { allowInInputs: true, enabled: !props.busy });
  // Leaving the screen or closing the window with unsaved changes asks first (SRS UX-08).
  useLeaveGuard({ dirty: form.formState.isDirty, busy: props.busy });

  return (
    <form onSubmit={handleSubmit(props.onSubmit)} noValidate className={styles.form} autoComplete="off">
      <Section title="Personal details">
        <FormGrid columns={4}>
          <SelectField
            control={control}
            name="title"
            label="Title"
            placeholder="—"
            options={TITLES.map((t) => ({ value: t, label: t }))}
          />
          <TextField control={control} name="firstName" label="First name" required autoFocus maxLength={100} />
          <TextField control={control} name="middleName" label="Middle name" maxLength={100} />
          <TextField control={control} name="lastName" label="Last name / surname" maxLength={100} />
          <ChoiceField
            control={control}
            name="gender"
            label="Gender"
            required
            options={[
              { value: 'Female', label: 'Female' },
              { value: 'Male', label: 'Male' },
              { value: 'Other', label: 'Other' },
            ]}
          />
          <DateField control={control} name="dateOfBirth" label="Date of birth" min="1930-01-01" />
          <TextField
            control={control}
            name="phone"
            label="Mobile number"
            type="tel"
            hint="10 digits; staff alerts use this number"
          />
          <TextField control={control} name="email" label="Email" type="email" />
        </FormGrid>
      </Section>

      <Section title="Address and emergency contact">
        <FormGrid columns={3}>
          <TextField control={control} name="address.line1" label="House / street" />
          <TextField control={control} name="address.line2" label="Area / locality" />
          <TextField control={control} name="address.city" label="City / town" />
          <TextField control={control} name="address.district" label="District" />
          <TextField control={control} name="address.state" label="State" />
          <TextField control={control} name="address.pinCode" label="PIN code" maxLength={6} />
          <TextField control={control} name="emergencyContactName" label="Emergency contact name" />
          <TextField control={control} name="emergencyContactPhone" label="Emergency contact phone" type="tel" />
        </FormGrid>
      </Section>

      <Section title="Job">
        <FormGrid columns={3}>
          <SelectField
            control={control}
            name="departmentId"
            label="Department"
            placeholder={departments.isLoading ? 'Loading…' : 'Choose'}
            options={toOptions(departments.data)}
          />
          <SelectField
            control={control}
            name="designationId"
            label="Designation"
            placeholder={designations.isLoading ? 'Loading…' : 'Choose'}
            options={(designations.data ?? []).map((d) => ({ value: d.id, label: d.name }))}
          />
          <SelectField
            control={control}
            name="employmentType"
            label="Employment type"
            required
            options={EMPLOYMENT_TYPES.map((t) => ({ value: t, label: t }))}
          />
          <SelectField
            control={control}
            name="status"
            label="Status"
            required
            options={EDITABLE_EMPLOYEE_STATUSES.map((s) => ({ value: s, label: employeeStatusLabel[s] }))}
            hint="Use the separation action when someone leaves."
          />
          <DateField control={control} name="joiningDate" label="Joining date" required />
          <DateField control={control} name="confirmationDate" label="Confirmation date" />
          <ManagerField control={control} employeeId={props.employeeId} />
          <SelectField
            control={control}
            name="defaultShiftId"
            label="Usual shift"
            placeholder="None (rostered)"
            options={(shifts.data ?? []).map((s) => ({ value: s.id, label: `${s.code} · ${s.name}` }))}
            hint="Used on days without a roster entry."
          />
          <SelectField
            control={control}
            name="weeklyOff"
            label="Fixed weekly off"
            placeholder="None (rostered)"
            options={WEEKDAYS.map((d) => ({ value: d, label: d }))}
          />
          <TextField
            control={control}
            name="biometricPin"
            label="Attendance terminal PIN"
            maxLength={24}
            hint="Exactly as enrolled on the fingerprint terminal."
          />
          <SwitchField
            control={control}
            name="isAttendanceExempt"
            label="Exempt from attendance (consultants, senior staff)"
          />
        </FormGrid>
      </Section>

      <Section
        title="Sign-in and doctor link"
        description="Link the employee to their HealthCare360 sign-in for self-service, and to the doctors' master if they see patients."
      >
        <FormGrid columns={2}>
          {canPickUsers || props.defaultValues.user ? (
            <LinkedUserField control={control} disabled={!canPickUsers} />
          ) : (
            <MessageBar intent="info">
              <MessageBarBody>An administrator with access to the staff list can link the sign-in.</MessageBarBody>
            </MessageBar>
          )}
          <LookupField
            control={control}
            name="practitionerId"
            label="Doctor / practitioner"
            load={searchPractitioners}
            hint="Only for clinicians in the doctors' master."
          />
        </FormGrid>
      </Section>

      <Section
        title="Pay and statutory details"
        description="PAN is stored encrypted and shown masked everywhere. It is never displayed in full after saving."
      >
        <FormGrid columns={4}>
          <SelectField
            control={control}
            name="paymentMode"
            label="Salary paid by"
            required
            options={PAYMENT_MODES.map((m) => ({ value: m, label: humanize(m) }))}
          />
          <TextField
            control={control}
            name="pan"
            label="PAN"
            maxLength={10}
            autoComplete="off"
            disabled={removePan}
            hint={props.onFile?.pan ? `On file: ${props.onFile.pan}. Leave blank to keep it.` : 'ABCDE1234F'}
          />
          <TextField control={control} name="uan" label="UAN (PF)" maxLength={12} hint="12 digits" />
          <TextField control={control} name="pfNumber" label="PF member number" maxLength={32} />
          <TextField control={control} name="esiNumber" label="ESI insurance number" maxLength={17} />
        </FormGrid>
        {!isNew && props.onFile?.pan && (
          <Controller
            control={control}
            name="removePan"
            render={({ field }) => (
              <Checkbox
                checked={field.value}
                onChange={(_, data) => field.onChange(Boolean(data.checked))}
                label="Remove the PAN on file"
              />
            )}
          />
        )}
      </Section>

      {isNew && paymentMode === 'BankTransfer' && (
        <Section
          title="Salary bank account"
          description="Typed twice to avoid mistakes. After saving only the last four digits are shown; later changes are audited and the employee is notified."
        >
          <FormGrid columns={4}>
            <TextField control={control} name="bankName" label="Bank name" autoComplete="off" />
            <TextField
              control={control}
              name="accountNumber"
              label="Account number"
              type="password"
              autoComplete="off"
            />
            <TextField
              control={control}
              name="confirmAccountNumber"
              label="Account number again"
              type="password"
              autoComplete="off"
            />
            <TextField
              control={control}
              name="ifsc"
              label="IFSC"
              maxLength={11}
              autoComplete="off"
              hint="e.g. SBIN0001234"
            />
          </FormGrid>
        </Section>
      )}

      <Section title="Notes">
        <TextAreaField control={control} name="notes" label="Notes for HR" rows={2} maxLength={1000} />
      </Section>

      <div className={styles.footer}>
        {props.secondaryActions}
        <Button
          type="submit"
          appearance="primary"
          size="large"
          icon={props.busy ? <Spinner size="tiny" /> : <SaveRegular />}
          disabled={props.busy}
        >
          {props.submitLabel}
        </Button>
      </div>
    </form>
  );
}

function ManagerField({ control, employeeId }: { control: Control<EmployeeFormValues>; employeeId?: string }) {
  return (
    <Controller
      control={control}
      name="reportingManager"
      render={({ field, fieldState }) => (
        <EmployeePicker
          label="Reporting manager"
          value={field.value ? { id: field.value.id, code: '', name: field.value.name } : null}
          onChange={(picked) =>
            field.onChange(picked && picked.id !== employeeId ? { id: picked.id, name: picked.name } : null)
          }
          hint="Approves this employee's leave and attendance corrections."
          validationMessage={fieldState.error?.message}
        />
      )}
    />
  );
}

function LinkedUserField({ control, disabled }: { control: Control<EmployeeFormValues>; disabled: boolean }) {
  const user = useWatch({ control, name: 'user' });
  const resolved = useUserName(user?.id);
  return (
    <Controller
      control={control}
      name="user"
      render={({ field, fieldState }) => (
        <UserPicker
          label="HealthCare360 sign-in"
          value={field.value ? { id: field.value.id, name: field.value.name || resolved || 'Linked sign-in' } : null}
          onChange={field.onChange}
          disabled={disabled}
          validationMessage={fieldState.error?.message}
          hint={
            <span>
              <ShieldKeyholeRegular aria-hidden /> Lets the employee use self-service (leave, payslips, roster).
            </span>
          }
        />
      )}
    />
  );
}
