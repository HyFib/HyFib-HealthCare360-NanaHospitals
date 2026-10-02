import { Button } from '@fluentui/react-components';
import { DocumentSearchRegular } from '@fluentui/react-icons';
import { zodResolver } from '@hookform/resolvers/zod';
import { FormGrid, FullRow, SwitchField, TextField, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useForm, type Control } from 'react-hook-form';
import {
  mastersApi,
  mastersQueries,
  refreshAfterMasterChange,
  type FacilityDto,
  type LetterheadPreviewRequest,
} from './api';
import {
  facilitySchema,
  PERMANENT_CODE_HINT,
  toFacilityForm,
  toFacilityRequest,
  toLetterheadRequest,
  type FacilityForm,
} from './forms';
import { ImageField } from './ImageField';
import { LetterheadPreviewDialog } from './LetterheadPreviewDialog';
import { LoadingDrawer, MasterDrawer } from './MasterDrawer';
import { EditorForm, EditorSection } from './parts';
import { facilityField, showSaveError } from './saveErrors';
import { useEditorGuard } from './useEditorGuard';

/** Add a facility, or change one: `facilityId` null adds. The record is read afresh, so its version is current. */
export function FacilityEditorDrawer({ facilityId, onClose }: { facilityId: string | null; onClose: () => void }) {
  const existing = useQuery({ ...mastersQueries.facility(facilityId ?? ''), enabled: facilityId !== null });
  if (facilityId !== null && !existing.data) {
    return <LoadingDrawer error={existing.error} what="facility" size="large" onClose={onClose} />;
  }
  return <FacilityEditor facility={existing.data ?? null} onClose={onClose} />;
}

function FacilityEditor({ facility, onClose }: { facility: FacilityDto | null; onClose: () => void }) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [preview, setPreview] = useState<LetterheadPreviewRequest | null>(null);
  const form = useForm<FacilityForm>({
    resolver: zodResolver(facilitySchema),
    defaultValues: toFacilityForm(facility),
    mode: 'onTouched',
  });
  const save = useMutation({
    mutationFn: (values: FacilityForm) =>
      mastersApi.saveFacility(facility?.id ?? null, toFacilityRequest(values, facility)),
    onSuccess: () => {
      notify.success(facility ? 'Facility saved' : 'Facility added', 'Documents print the new details from now on.');
      refreshAfterMasterChange(queryClient);
    },
    onError: (error) =>
      showSaveError(error, {
        form,
        notify,
        what: 'facility',
        onConflict: () => refreshAfterMasterChange(queryClient),
        fieldFor: facilityField,
      }),
  });
  const guard = useEditorGuard({ dirty: form.formState.isDirty, busy: save.isPending, onClose });

  return (
    <MasterDrawer
      title={facility ? facility.name : 'New facility'}
      size="large"
      guard={guard}
      busy={save.isPending}
      submitLabel={facility ? 'Save' : 'Add facility'}
      onSubmit={form.handleSubmit((values) => save.mutate(values, { onSuccess: guard.close }))}
    >
      <FacilityFields
        control={form.control}
        editing={facility !== null}
        onPreview={() => setPreview(toLetterheadRequest(form.getValues()))}
      />
      {preview && <LetterheadPreviewDialog request={preview} onClose={() => setPreview(null)} />}
    </MasterDrawer>
  );
}

/** The facility's details as documents print them, and its letterhead. */
export function FacilityFields({
  control,
  editing,
  onPreview,
}: {
  control: Control<FacilityForm>;
  editing: boolean;
  onPreview: () => void;
}) {
  return (
    <EditorForm>
      <EditorSection title="Name">
        <FormGrid columns={2}>
          <TextField
            control={control}
            name="name"
            label="Name"
            required
            autoFocus={!editing}
            maxLength={200}
            hint="Printed at the top of every document, such as Nana Hospitals."
          />
          <TextField
            control={control}
            name="code"
            label="Code"
            required
            disabled={editing}
            maxLength={6}
            hint={
              editing
                ? `${PERMANENT_CODE_HINT} It starts every UHID and bill number.`
                : "Two to six letters, such as NH. It starts every UHID and bill number, and can't be changed later."
            }
          />
          <FullRow>
            <TextField
              control={control}
              name="legalName"
              label="Legal name"
              maxLength={200}
              hint="The registered company or trust, printed on bills. Leave it empty to print the name."
            />
          </FullRow>
        </FormGrid>
      </EditorSection>

      <EditorSection title="Address">
        <FormGrid columns={2}>
          <FullRow>
            <TextField control={control} name="line1" label="Address line 1" maxLength={200} />
          </FullRow>
          <FullRow>
            <TextField control={control} name="line2" label="Address line 2" maxLength={200} />
          </FullRow>
          <TextField control={control} name="city" label="City" maxLength={100} />
          <TextField control={control} name="district" label="District" maxLength={100} />
          <TextField control={control} name="state" label="State" maxLength={100} />
          <TextField control={control} name="pinCode" label="PIN code" maxLength={6} />
          <TextField control={control} name="country" label="Country" maxLength={100} />
        </FormGrid>
      </EditorSection>

      <EditorSection title="Contact">
        <FormGrid columns={2}>
          <TextField control={control} name="phone" label="Phone" type="tel" maxLength={20} />
          <TextField control={control} name="email" label="E-mail" type="email" maxLength={256} />
          <FullRow>
            <TextField
              control={control}
              name="website"
              label="Website"
              maxLength={256}
              placeholder="www.nanahospitals.in"
            />
          </FullRow>
        </FormGrid>
      </EditorSection>

      <EditorSection
        title="Registrations and tax"
        description="Printed on bills and reports where the law asks for them."
      >
        <FormGrid columns={2}>
          <TextField control={control} name="gstin" label="GSTIN" maxLength={15} />
          <TextField control={control} name="pan" label="PAN" maxLength={10} />
          <TextField
            control={control}
            name="registrationNumber"
            label="Registration number"
            maxLength={64}
            hint="Clinical establishment registration."
          />
          <TextField control={control} name="nabhNumber" label="NABH accreditation number" maxLength={64} />
          <TextField
            control={control}
            name="hfrId"
            label="ABDM facility id (HFR)"
            maxLength={64}
            hint="The Health Facility Registry id, for sharing records through ABDM."
          />
        </FormGrid>
      </EditorSection>

      <EditorSection
        title="Letterhead"
        description="Documents print the name, address and contact details above, with this logo and line."
      >
        <ImageField
          control={control}
          name="logoFileId"
          label="Logo"
          kind="logo"
          hint="A PNG with a transparent background prints best. It is shrunk to fit documents."
        />
        <TextField
          control={control}
          name="letterheadNote"
          label="Letterhead line"
          maxLength={300}
          hint='An extra line under the address, such as "NABH accredited hospital".'
        />
        <div>
          <Button icon={<DocumentSearchRegular />} onClick={onPreview}>
            Preview letterhead
          </Button>
        </div>
      </EditorSection>

      <EditorSection title="Use">
        <SwitchField
          control={control}
          name="isActive"
          label="In use"
          hint="Staff sign in only to a facility in use. The facility you are signed in to stays in use."
        />
      </EditorSection>
    </EditorForm>
  );
}
