import { zodResolver } from '@hookform/resolvers/zod';
import { SwitchField, TextAreaField, TextField, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useForm, type Control } from 'react-hook-form';
import { mastersApi, mastersQueries, refreshAfterMasterChange, type SpecialtyDto } from './api';
import { PERMANENT_CODE_HINT, specialtySchema, toSpecialtyForm, toSpecialtyRequest, type SpecialtyForm } from './forms';
import { LoadingDrawer, MasterDrawer } from './MasterDrawer';
import { EditorForm, EditorSection } from './parts';
import { showSaveError } from './saveErrors';
import { useEditorGuard } from './useEditorGuard';

/** Add a speciality (`specialtyId` null), or change one. Specialities are shared by every facility. */
export function SpecialtyEditorDrawer({ specialtyId, onClose }: { specialtyId: string | null; onClose: () => void }) {
  const existing = useQuery({ ...mastersQueries.specialty(specialtyId ?? ''), enabled: specialtyId !== null });
  // Filled from the speciality as read now, not from an older copy kept from before (REL-04).
  if (specialtyId !== null && (!existing.isFetchedAfterMount || !existing.data)) {
    return <LoadingDrawer error={existing.error} what="speciality" size="small" onClose={onClose} />;
  }
  return <SpecialtyEditor specialty={existing.data ?? null} onClose={onClose} />;
}

function SpecialtyEditor({ specialty, onClose }: { specialty: SpecialtyDto | null; onClose: () => void }) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const form = useForm<SpecialtyForm>({
    resolver: zodResolver(specialtySchema),
    defaultValues: toSpecialtyForm(specialty),
    mode: 'onTouched',
  });
  // The version the form was filled from, not a newer one read since (REL-04).
  const [loadedVersion] = useState(specialty?.version ?? null);
  const save = useMutation({
    mutationFn: (values: SpecialtyForm) =>
      mastersApi.saveSpecialty(specialty?.id ?? null, toSpecialtyRequest(values, specialty, loadedVersion)),
    onSuccess: () => {
      notify.success(specialty ? 'Speciality saved' : 'Speciality added');
      refreshAfterMasterChange(queryClient);
    },
    onError: (error) =>
      showSaveError(error, {
        form,
        notify,
        what: 'speciality',
        onConflict: () => refreshAfterMasterChange(queryClient),
      }),
  });
  const guard = useEditorGuard({ dirty: form.formState.isDirty, busy: save.isPending, onClose });

  return (
    <MasterDrawer
      title={specialty ? specialty.name : 'New speciality'}
      size="small"
      guard={guard}
      busy={save.isPending}
      submitLabel={specialty ? 'Save' : 'Add speciality'}
      onSubmit={form.handleSubmit((values) => save.mutate(values, { onSuccess: guard.close }))}
    >
      <SpecialtyFields control={form.control} editing={specialty !== null} />
    </MasterDrawer>
  );
}

export function SpecialtyFields({ control, editing }: { control: Control<SpecialtyForm>; editing: boolean }) {
  return (
    <EditorForm>
      <EditorSection title="Speciality">
        <TextField
          control={control}
          name="name"
          label="Name"
          required
          autoFocus={!editing}
          maxLength={128}
          hint="As patients look for it when booking, such as Cardiology."
        />
        <TextField
          control={control}
          name="code"
          label="Code"
          required
          disabled={editing}
          maxLength={16}
          hint={editing ? PERMANENT_CODE_HINT : "A short code such as CARDIO. It can't be changed later."}
        />
        <TextAreaField control={control} name="description" label="Description" rows={3} maxLength={500} />
      </EditorSection>
      <EditorSection title="Use">
        <SwitchField
          control={control}
          name="isActive"
          label="In use"
          hint="A speciality not in use isn't offered for new doctors, departments or online booking. Records keep it."
        />
      </EditorSection>
    </EditorForm>
  );
}
