import { Checkbox, makeStyles, MessageBar, MessageBarBody } from '@fluentui/react-components';
import { zodResolver } from '@hookform/resolvers/zod';
import {
  CheckboxGroup,
  ConfirmDialog,
  FormGrid,
  FullRow,
  NumberField,
  SelectField,
  SwitchField,
  TextAreaField,
  TextField,
  useNotify,
  type OptionItem,
} from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { Controller, useForm, useWatch, type Control } from 'react-hook-form';
import { useDepartments } from '@/lib/lookups';
import { askedForApproval } from '@/modules/admin/api';
import { PendingGrantNotice } from '@/modules/admin/PendingGrantNotice';
import {
  mastersApi,
  mastersQueries,
  refreshAfterMasterChange,
  type FacilityDto,
  type PractitionerDto,
  type PractitionerWork,
  type SpecialtyDto,
} from './api';
import {
  displayNameOf,
  optionsWithCurrent,
  PERMANENT_CODE_HINT,
  practitionerSchema,
  toPractitionerForm,
  toPractitionerRequest,
  type PractitionerForm,
} from './forms';
import { ImageField } from './ImageField';
import { GENDER_LABELS, GENDERS, isDoctor, PRACTITIONER_KIND_LABELS, PRACTITIONER_KINDS, TITLES } from './labels';
import { LoadingDrawer, MasterDrawer } from './MasterDrawer';
import { EditorForm, EditorSection } from './parts';
import { showSaveError, workAheadOf } from './saveErrors';
import { useEditorGuard } from './useEditorGuard';

const useStyles = makeStyles({
  facilities: { display: 'flex', flexDirection: 'column', gap: '2px' },
  work: { margin: 0, paddingLeft: '20px' },
});

/** Add a doctor or other clinician (`practitionerId` null), or change one. */
export function PractitionerEditorDrawer({
  practitionerId,
  onClose,
}: {
  practitionerId: string | null;
  onClose: () => void;
}) {
  const existing = useQuery({ ...mastersQueries.practitioner(practitionerId ?? ''), enabled: practitionerId !== null });
  // Filled from the record as read now, not from an older copy kept from before (REL-04).
  if (practitionerId !== null && (!existing.isFetchedAfterMount || !existing.data)) {
    return <LoadingDrawer error={existing.error} what="record" size="large" onClose={onClose} />;
  }
  return <PractitionerEditor practitioner={existing.data ?? null} onClose={onClose} />;
}

function PractitionerEditor({ practitioner, onClose }: { practitioner: PractitionerDto | null; onClose: () => void }) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const specialties = useQuery(mastersQueries.specialties());
  const facilities = useQuery(mastersQueries.facilities());
  const departments = useDepartments();
  const form = useForm<PractitionerForm>({
    resolver: zodResolver(practitionerSchema),
    defaultValues: toPractitionerForm(practitioner),
    mode: 'onTouched',
  });
  // The version the form was filled from, not a newer one read since (REL-04).
  const [loadedVersion] = useState(practitioner?.version ?? null);
  // What the doctor still has ahead, when the server asked first before taking them out of use.
  const [workAhead, setWorkAhead] = useState<{ values: PractitionerForm; work: PractitionerWork[] } | null>(null);
  const save = useMutation({
    mutationFn: ({ values, anyway }: { values: PractitionerForm; anyway: boolean }) =>
      mastersApi.savePractitioner(practitioner?.id ?? null, {
        ...toPractitionerRequest(values, practitioner, loadedVersion),
        ...(anyway ? { takeOutOfUseAnyway: true } : {}),
      }),
    onSuccess: (result, { anyway }) => {
      setWorkAhead(null);
      const asked = askedForApproval(result);
      if (asked) {
        // A doctor someone signs in as: the registration number, HPR ID or signature waits; the rest is saved.
        notify.success(
          'Asked for approval',
          `${asked.summary}. Another administrator who may approve grants decides it; the rest of the record is saved.`,
        );
      } else if (anyway) {
        notify.warning(
          'Taken out of use',
          'Nobody can book or assign this doctor now. Move or cancel what was still booked with them: their appointments, ' +
            'the patients admitted under them and their surgeries.',
        );
      } else {
        notify.success(
          practitioner ? 'Saved' : 'Added',
          'Booking, billing and documents use the new details from now on.',
        );
      }
      refreshAfterMasterChange(queryClient);
    },
    onError: (error, { values }) => {
      const work = workAheadOf(error);
      if (work) {
        setWorkAhead({ values, work });
        return;
      }
      setWorkAhead(null);
      showSaveError(error, { form, notify, what: 'record', onConflict: () => refreshAfterMasterChange(queryClient) });
    },
  });
  const guard = useEditorGuard({ dirty: form.formState.isDirty, busy: save.isPending, onClose });

  const departmentOptions: OptionItem[] = (departments.data ?? []).map((d) => ({ value: d.id, label: d.name }));
  if (practitioner?.departmentId && !departmentOptions.some((o) => o.value === practitioner.departmentId)) {
    departmentOptions.unshift({
      value: practitioner.departmentId,
      label: `${practitioner.departmentName ?? 'Current department'} (not offered here)`,
    });
  }

  return (
    <MasterDrawer
      title={practitioner ? practitioner.displayName : 'New doctor or clinician'}
      size="large"
      guard={guard}
      busy={save.isPending}
      submitLabel={practitioner ? 'Save' : 'Add'}
      onSubmit={form.handleSubmit((values) => save.mutate({ values, anyway: false }, { onSuccess: guard.close }))}
    >
      <WorkAheadDialog
        work={workAhead?.work ?? null}
        busy={save.isPending}
        onCancel={() => setWorkAhead(null)}
        onConfirm={() =>
          workAhead && save.mutate({ values: workAhead.values, anyway: true }, { onSuccess: guard.close })
        }
      />
      {practitioner?.pendingIdentity ? (
        <PendingGrantNotice
          approvalId={practitioner.pendingIdentity.approvalId}
          what={`A new registration number, HPR ID or signature for ${practitioner.displayName}`}
          requestedBy={practitioner.pendingIdentity.requestedBy}
          requestedAt={practitioner.pendingIdentity.requestedAt}
          decides="Another administrator who may approve grants decides it; until then their documents carry the ones shown here."
        />
      ) : (
        (practitioner?.linkedAccounts?.length ?? 0) > 0 && (
          <MessageBar intent="info" layout="multiline">
            <MessageBarBody>
              {practitioner?.linkedAccounts?.join(', ')} signs in as {practitioner?.displayName}. A new registration
              number, HPR ID or signature waits for another administrator who may approve grants; the rest is saved at
              once.
            </MessageBarBody>
          </MessageBar>
        )
      )}
      <PractitionerFields
        control={form.control}
        editing={practitioner !== null}
        specialties={specialties.data}
        currentSpecialtyId={practitioner?.specialtyId ?? null}
        departmentOptions={departmentOptions}
        facilities={facilities.data ?? []}
      />
    </MasterDrawer>
  );
}

/**
 * Asks before taking a doctor out of use while work is still ahead, as the server named it: each kind of work with the
 * first few by number. Going ahead saves again, saying so.
 */
export function WorkAheadDialog({
  work,
  busy,
  onCancel,
  onConfirm,
}: {
  work: PractitionerWork[] | null;
  busy: boolean;
  onCancel: () => void;
  onConfirm: () => void;
}) {
  const styles = useStyles();
  return (
    <ConfirmDialog
      open={work !== null}
      title="This doctor still has work ahead"
      confirmLabel="Take out of use anyway"
      destructive
      busy={busy}
      onCancel={onCancel}
      onConfirm={onConfirm}
    >
      <p>Out of use, nobody can book or assign them again. Still ahead:</p>
      <ul className={styles.work}>
        {work?.map((w) => (
          <li key={w.kind}>
            {w.words}
            {w.numbers.length > 0 && ` (${w.numbers.join(', ')}${w.count > w.numbers.length ? ', …' : ''})`}
          </li>
        ))}
      </ul>
      <p>These have to be moved to another doctor or cancelled.</p>
    </ConfirmDialog>
  );
}

/** A doctor's or clinician's record: name and role, registration, where they work, fees, booking, photo and signature. */
export function PractitionerFields({
  control,
  editing,
  specialties,
  currentSpecialtyId,
  departmentOptions,
  facilities,
}: {
  control: Control<PractitionerForm>;
  editing: boolean;
  specialties: readonly SpecialtyDto[] | undefined;
  currentSpecialtyId: string | null;
  departmentOptions: OptionItem[];
  facilities: readonly FacilityDto[];
}) {
  const [title, firstName, lastName, kind] = useWatch({ control, name: ['title', 'firstName', 'lastName', 'kind'] });
  const doctor = isDoctor(kind);
  const shownAs = displayNameOf({ title, firstName, lastName });
  const titles: OptionItem[] = TITLES.map((t) => ({ value: t, label: t }));
  if (title && !TITLES.some((t) => t === title)) {
    titles.push({ value: title, label: title });
  }

  return (
    <EditorForm>
      <EditorSection title="Name and role" description={shownAs ? `Shown as ${shownAs}` : undefined}>
        <FormGrid columns={3} minColumnWidth={160}>
          <SelectField control={control} name="title" label="Title" placeholder="None" options={titles} />
          <TextField
            control={control}
            name="firstName"
            label="First name"
            required
            autoFocus={!editing}
            maxLength={100}
          />
          <TextField control={control} name="lastName" label="Last name" maxLength={100} />
          <SelectField
            control={control}
            name="kind"
            label="Role"
            required
            options={PRACTITIONER_KINDS.map((k) => ({ value: k, label: PRACTITIONER_KIND_LABELS[k] }))}
          />
          <SelectField
            control={control}
            name="gender"
            label="Gender"
            options={GENDERS.map((g) => ({ value: g, label: GENDER_LABELS[g] }))}
          />
          <TextField
            control={control}
            name="code"
            label="Code"
            required
            disabled={editing}
            maxLength={16}
            hint={editing ? PERMANENT_CODE_HINT : "Such as DR012. It can't be changed later."}
          />
        </FormGrid>
      </EditorSection>

      <EditorSection
        title="Qualifications and registration"
        description={
          doctor ? 'Printed under the signature on prescriptions, reports and discharge summaries.' : undefined
        }
      >
        <FormGrid columns={2}>
          <FullRow>
            <TextField
              control={control}
              name="qualification"
              label="Qualifications"
              maxLength={200}
              hint="Such as MBBS, MD (General Medicine)."
            />
          </FullRow>
          <TextField
            control={control}
            name="registrationCouncil"
            label="Medical council"
            maxLength={128}
            hint="Such as Tamil Nadu Medical Council."
          />
          <TextField
            control={control}
            name="registrationNumber"
            label="Registration number"
            required={doctor}
            maxLength={64}
            hint={doctor ? 'Doctors need it to consult and sign.' : undefined}
          />
          <TextField
            control={control}
            name="hprId"
            label="HPR ID"
            autoComplete="off"
            maxLength={17}
            hint="14 digits, from ABDM's Healthcare Professionals Registry. It identifies the doctor in ABDM health records and insurance claims."
          />
        </FormGrid>
      </EditorSection>

      <EditorSection title="Department and speciality">
        <FormGrid columns={2}>
          <SelectField
            control={control}
            name="departmentId"
            label="Department"
            placeholder="None"
            options={departmentOptions}
          />
          <SelectField
            control={control}
            name="specialtyId"
            label="Speciality"
            placeholder="None"
            options={optionsWithCurrent(specialties, currentSpecialtyId)}
          />
        </FormGrid>
        {facilities.length > 1 && <FacilitiesPicker control={control} facilities={facilities} />}
      </EditorSection>

      <EditorSection title="Consultation" description="What the front office charges, and how patients can book.">
        <FormGrid columns={3} minColumnWidth={180}>
          <NumberField
            control={control}
            name="consultationFeeNew"
            label="Fee for a new visit (₹)"
            min={0}
            step={50}
            required
          />
          <NumberField
            control={control}
            name="consultationFeeFollowUp"
            label="Fee for a follow-up (₹)"
            min={0}
            step={50}
            required
          />
          <NumberField
            control={control}
            name="followUpValidityDays"
            label="Follow-up within (days)"
            min={0}
            max={90}
            required
            hint="A visit this soon after a paid one is charged the follow-up fee."
          />
        </FormGrid>
        <SwitchField
          control={control}
          name="availableForOnlineBooking"
          label="Patients can book online"
          hint="On the patient portal and the hospital's booking page."
        />
        <SwitchField
          control={control}
          name="availableForTelehealth"
          label="Offers video consultations"
          hint="Patients can book a video consultation with this doctor."
        />
      </EditorSection>

      <EditorSection title="Contact">
        <FormGrid columns={2}>
          <TextField control={control} name="phone" label="Mobile" type="tel" maxLength={20} />
          <TextField control={control} name="email" label="E-mail" type="email" maxLength={256} />
        </FormGrid>
      </EditorSection>

      <EditorSection title="Profile, photo and signature">
        <TextField
          control={control}
          name="languages"
          label="Languages spoken"
          maxLength={600}
          hint="Separate them with commas, such as English, Tamil, Hindi."
        />
        <TextAreaField
          control={control}
          name="bio"
          label="About"
          rows={4}
          maxLength={2000}
          hint="Shown to patients booking online."
        />
        <FormGrid columns={2}>
          <ImageField
            control={control}
            name="photoFileId"
            label="Photo"
            kind="photo"
            hint="Shown to patients booking online."
          />
          <ImageField
            control={control}
            name="signatureFileId"
            label="Signature"
            kind="signature"
            hint="Printed above the name on prescriptions, reports and discharge summaries. Sign in dark ink on white paper, then scan or photograph it."
          />
        </FormGrid>
      </EditorSection>

      <EditorSection title="Use">
        <SwitchField
          control={control}
          name="isActive"
          label="In use"
          hint="Someone not in use can't be booked or chosen for new visits. Appointments already booked stay; reschedule them from Appointments."
        />
      </EditorSection>
    </EditorForm>
  );
}

/** Where the practitioner consults: every facility, or the ones ticked. Shown only when there is more than one. */
function FacilitiesPicker({
  control,
  facilities,
}: {
  control: Control<PractitionerForm>;
  facilities: readonly FacilityDto[];
}) {
  const styles = useStyles();
  const allFacilities = useWatch({ control, name: 'allFacilities' });
  return (
    <>
      <SwitchField control={control} name="allFacilities" label="Consults at every facility" />
      {!allFacilities && (
        <Controller
          control={control}
          name="facilityIds"
          render={({ field, fieldState }) => {
            const chosen = field.value;
            const shown = facilities.filter((f) => f.isActive || chosen.includes(f.id));
            return (
              <CheckboxGroup
                label="Consults at"
                validationMessage={fieldState.error?.message}
                validationState={fieldState.error ? 'error' : 'none'}
              >
                <div className={styles.facilities}>
                  {shown.map((facility) => (
                    <Checkbox
                      key={facility.id}
                      label={facility.isActive ? facility.name : `${facility.name} (not in use)`}
                      checked={chosen.includes(facility.id)}
                      onChange={(_, data) =>
                        field.onChange(
                          data.checked === true ? [...chosen, facility.id] : chosen.filter((id) => id !== facility.id),
                        )
                      }
                    />
                  ))}
                </div>
              </CheckboxGroup>
            );
          }}
        />
      )}
    </>
  );
}
