import { Badge, Button, Caption1, Field, Input, Spinner } from '@fluentui/react-components';
import { AddRegular, DismissRegular } from '@fluentui/react-icons';
import { zodResolver } from '@hookform/resolvers/zod';
import { isApiError, normalizePhone } from '@hc360/core';
import { applyServerErrors, ErrorState, FormGrid, SwitchField, TextAreaField, TextField, useNotify } from '@hc360/ui';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useRef, useState } from 'react';
import { Controller, useForm } from 'react-hook-form';
import { z } from 'zod';
import { EditorDrawer } from '@/modules/hr/shared/EditorDrawer';
import { crmApi, crmKeys, crmQueries, type ReferringDoctorDetail } from './api';

const schema = z.object({
  name: z.string().trim().min(1, 'Enter the doctor’s name.').max(200),
  clinic: z.string().trim().max(200),
  phone: z
    .string()
    .trim()
    .refine((v) => !v || normalizePhone(v) !== null, 'Enter a valid phone number.'),
  email: z
    .string()
    .trim()
    .max(256)
    .refine((v) => !v || /^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(v), 'Enter a valid email address.'),
  specialty: z.string().trim().max(100),
  city: z.string().trim().max(100),
  registrationNumber: z.string().trim().max(64),
  alternateNames: z.array(z.string()).max(10, 'At most 10 other spellings.'),
  notes: z.string().trim().max(2000),
  isActive: z.boolean(),
});

type Values = z.infer<typeof schema>;

/**
 * Add or edit a referring doctor. "Other spellings" are how front desks type the name in "Referred by"
 * (e.g. "Dr RK", "Kumar Clinic"); referrals are counted by matching them.
 */
export function ReferringDoctorDrawer({
  doctorId,
  prefillName,
  onClose,
}: {
  doctorId: string | null;
  prefillName?: string;
  onClose: () => void;
}) {
  const doctor = useQuery({ ...crmQueries.referringDoctor(doctorId ?? ''), enabled: doctorId !== null });
  if (doctorId && !doctor.data) {
    return (
      <EditorDrawer open title="Referring doctor" dirty={false} onClose={onClose} onSubmit={(e) => e.preventDefault()}>
        {doctor.error ? (
          <ErrorState error={doctor.error} onRetry={() => void doctor.refetch()} />
        ) : (
          <Spinner label="Loading…" />
        )}
      </EditorDrawer>
    );
  }
  return <DoctorForm doctor={doctor.data ?? null} prefillName={prefillName} onClose={onClose} />;
}

function DoctorForm({
  doctor,
  prefillName,
  onClose,
}: {
  doctor: ReferringDoctorDetail | null;
  prefillName?: string;
  onClose: () => void;
}) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [alias, setAlias] = useState('');
  // Adding or removing a spelling takes focus back to the box, for the next one: both buttons go away or turn off.
  const aliasInput = useRef<HTMLInputElement>(null);
  const form = useForm<Values>({
    resolver: zodResolver(schema),
    defaultValues: {
      name: doctor?.name ?? prefillName ?? '',
      clinic: doctor?.clinic ?? '',
      phone: doctor?.phone ?? '',
      email: doctor?.email ?? '',
      specialty: doctor?.specialty ?? '',
      city: doctor?.city ?? '',
      registrationNumber: doctor?.registrationNumber ?? '',
      alternateNames: doctor?.alternateNames ?? [],
      notes: doctor?.notes ?? '',
      isActive: doctor?.isActive ?? true,
    },
  });
  // The version the form was filled from, not a newer one read since (REL-04).
  const [loadedVersion] = useState(doctor?.version ?? null);

  const submit = async (v: Values) => {
    try {
      await crmApi.saveReferringDoctor(doctor?.id ?? null, {
        name: v.name.trim(),
        clinic: v.clinic.trim() || null,
        phone: v.phone.trim() ? normalizePhone(v.phone) : null,
        email: v.email.trim() || null,
        specialty: v.specialty.trim() || null,
        city: v.city.trim() || null,
        registrationNumber: v.registrationNumber.trim() || null,
        alternateNames: v.alternateNames,
        notes: v.notes.trim() || null,
        isActive: v.isActive,
        version: loadedVersion,
      });
      notify.success('Referring doctor saved', 'Referral counts are worked out again from visit records.');
      void queryClient.invalidateQueries({ queryKey: crmKeys.referrals });
      onClose();
    } catch (error) {
      if (isApiError(error) && error.code === 'referral.duplicate_doctor') {
        form.setError('registrationNumber', { type: 'server', message: error.userMessage });
      } else if (!applyServerErrors(error, form.setError)) {
        notify.error(error, 'Could not save the doctor');
        if (isApiError(error) && error.code === 'concurrency') {
          // Someone else changed the doctor meanwhile: opened again, the form shows their version.
          void queryClient.invalidateQueries({ queryKey: crmKeys.referrals });
        }
      }
    }
  };

  return (
    <EditorDrawer
      open
      title={doctor ? doctor.name : 'Add referring doctor'}
      dirty={form.formState.isDirty}
      onClose={onClose}
      onSubmit={form.handleSubmit(submit)}
      busy={form.formState.isSubmitting}
    >
      <TextField control={form.control} name="name" label="Name" required autoFocus placeholder="Dr. R. Kumar" />
      <FormGrid columns={2} minColumnWidth={180}>
        <TextField control={form.control} name="clinic" label="Clinic or hospital" />
        <TextField control={form.control} name="specialty" label="Specialty" />
        <TextField control={form.control} name="phone" label="Phone" type="tel" />
        <TextField control={form.control} name="email" label="Email" type="email" />
        <TextField control={form.control} name="city" label="City / area" />
        <TextField control={form.control} name="registrationNumber" label="Medical council registration" />
      </FormGrid>
      <Controller
        control={form.control}
        name="alternateNames"
        render={({ field, fieldState }) => {
          const add = () => {
            const value = alias.trim();
            if (value && !field.value.some((n) => n.toLowerCase() === value.toLowerCase()) && field.value.length < 10) {
              field.onChange([...field.value, value]);
            }
            setAlias('');
          };
          return (
            <Field
              label="Other spellings in 'Referred by'"
              hint="Names the front desk may type for this doctor. A one-word name must match the whole text."
              validationMessage={fieldState.error?.message}
              validationState={fieldState.error ? 'error' : 'none'}
            >
              <div style={{ display: 'flex', gap: 6, flexWrap: 'wrap', marginBottom: 6 }}>
                {field.value.length === 0 && <Caption1>None yet.</Caption1>}
                {field.value.map((name) => (
                  <Badge key={name} appearance="tint" size="large">
                    {name}
                    <Button
                      size="small"
                      appearance="transparent"
                      icon={<DismissRegular />}
                      aria-label={`Remove ${name}`}
                      onClick={() => {
                        aliasInput.current?.focus();
                        field.onChange(field.value.filter((n) => n !== name));
                      }}
                    />
                  </Badge>
                ))}
              </div>
              <div style={{ display: 'flex', gap: 6 }}>
                <Input
                  ref={aliasInput}
                  value={alias}
                  onChange={(_, d) => setAlias(d.value)}
                  maxLength={200}
                  placeholder="For example: Dr RK, Anna Nagar"
                  onKeyDown={(event) => {
                    if (event.key === 'Enter') {
                      event.preventDefault();
                      add();
                    }
                  }}
                  style={{ flex: 1 }}
                />
                <Button
                  icon={<AddRegular />}
                  onClick={() => {
                    aliasInput.current?.focus();
                    add();
                  }}
                  disabled={!alias.trim() || field.value.length >= 10}
                >
                  Add
                </Button>
              </div>
            </Field>
          );
        }}
      />
      <TextAreaField control={form.control} name="notes" label="Notes" rows={2} maxLength={2000} />
      <SwitchField control={form.control} name="isActive" label="Active" />
    </EditorDrawer>
  );
}
