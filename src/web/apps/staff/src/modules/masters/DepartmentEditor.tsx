import { zodResolver } from '@hookform/resolvers/zod';
import type { LookupItem } from '@hc360/core';
import { FormGrid, FullRow, LookupField, NumberField, SelectField, SwitchField, TextField, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useForm, type Control } from 'react-hook-form';
import { searchPractitioners } from '@/lib/lookups';
import { mastersApi, mastersQueries, refreshAfterMasterChange, type DepartmentDto, type SpecialtyDto } from './api';
import {
  departmentSchema,
  optionsWithCurrent,
  PERMANENT_CODE_HINT,
  toDepartmentForm,
  toDepartmentRequest,
  withCurrentItem,
  type DepartmentForm,
} from './forms';
import { DEPARTMENT_KIND_LABELS, DEPARTMENT_KINDS } from './labels';
import { LoadingDrawer, MasterDrawer } from './MasterDrawer';
import { EditorForm, EditorSection } from './parts';
import { showSaveError } from './saveErrors';
import { useEditorGuard } from './useEditorGuard';

/** Add a department to the signed-in facility (`departmentId` null), or change one. */
export function DepartmentEditorDrawer({
  departmentId,
  nextOrder,
  onClose,
}: {
  departmentId: string | null;
  nextOrder: number;
  onClose: () => void;
}) {
  const existing = useQuery({ ...mastersQueries.department(departmentId ?? ''), enabled: departmentId !== null });
  const headId = existing.data?.headPractitionerId ?? null;
  const head = useQuery({ ...mastersQueries.practitioner(headId ?? ''), enabled: headId !== null });
  if (departmentId !== null && (!existing.data || (headId !== null && head.isLoading))) {
    return <LoadingDrawer error={existing.error} what="department" onClose={onClose} />;
  }
  const currentHead = head.data ? { id: head.data.id, code: head.data.code, name: head.data.displayName } : null;
  return (
    <DepartmentEditor
      department={existing.data ?? null}
      currentHead={currentHead}
      nextOrder={nextOrder}
      onClose={onClose}
    />
  );
}

function DepartmentEditor({
  department,
  currentHead,
  nextOrder,
  onClose,
}: {
  department: DepartmentDto | null;
  currentHead: LookupItem | null;
  nextOrder: number;
  onClose: () => void;
}) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const specialties = useQuery(mastersQueries.specialties());
  const form = useForm<DepartmentForm>({
    resolver: zodResolver(departmentSchema),
    defaultValues: toDepartmentForm(department, nextOrder),
    mode: 'onTouched',
  });
  const save = useMutation({
    mutationFn: (values: DepartmentForm) =>
      mastersApi.saveDepartment(department?.id ?? null, toDepartmentRequest(values, department)),
    onSuccess: () => {
      notify.success(department ? 'Department saved' : 'Department added');
      refreshAfterMasterChange(queryClient);
    },
    onError: (error) =>
      showSaveError(error, {
        form,
        notify,
        what: 'department',
        onConflict: () => refreshAfterMasterChange(queryClient),
      }),
  });
  const guard = useEditorGuard({ dirty: form.formState.isDirty, busy: save.isPending, onClose });

  return (
    <MasterDrawer
      title={department ? department.name : 'New department'}
      guard={guard}
      busy={save.isPending}
      submitLabel={department ? 'Save' : 'Add department'}
      onSubmit={form.handleSubmit((values) => save.mutate(values, { onSuccess: guard.close }))}
    >
      <DepartmentFields
        control={form.control}
        editing={department !== null}
        specialties={specialties.data}
        currentSpecialtyId={department?.specialtyId ?? null}
        currentHead={currentHead}
      />
    </MasterDrawer>
  );
}

/** A department: what it is, who heads it, where it is, and how its queue tokens read. */
export function DepartmentFields({
  control,
  editing,
  specialties,
  currentSpecialtyId,
  currentHead,
}: {
  control: Control<DepartmentForm>;
  editing: boolean;
  specialties: readonly SpecialtyDto[] | undefined;
  currentSpecialtyId: string | null;
  currentHead: LookupItem | null;
}) {
  return (
    <EditorForm>
      <EditorSection title="Department">
        <FormGrid columns={2}>
          <FullRow>
            <TextField
              control={control}
              name="name"
              label="Name"
              required
              autoFocus={!editing}
              maxLength={128}
              hint="As patients and staff know it, such as General medicine."
            />
          </FullRow>
          <TextField
            control={control}
            name="code"
            label="Code"
            required
            disabled={editing}
            maxLength={16}
            hint={editing ? PERMANENT_CODE_HINT : "A short code such as GM. It can't be changed later."}
          />
          <SelectField
            control={control}
            name="kind"
            label="Kind"
            required
            options={DEPARTMENT_KINDS.map((kind) => ({ value: kind, label: DEPARTMENT_KIND_LABELS[kind] }))}
          />
          <SelectField
            control={control}
            name="specialtyId"
            label="Speciality"
            placeholder="None"
            options={optionsWithCurrent(specialties, currentSpecialtyId)}
          />
          <LookupField
            control={control}
            name="headPractitionerId"
            label="Head of department"
            placeholder="Type a doctor's name"
            initialItems={currentHead ? [currentHead] : undefined}
            load={async (search) => withCurrentItem(await searchPractitioners(search), currentHead, search)}
          />
        </FormGrid>
      </EditorSection>

      <EditorSection title="Where and how patients are called">
        <FormGrid columns={2}>
          <TextField
            control={control}
            name="location"
            label="Location"
            maxLength={128}
            hint="Such as Block A, ground floor."
          />
          <TextField control={control} name="phone" label="Phone or extension" type="tel" maxLength={20} />
          <TextField
            control={control}
            name="tokenPrefix"
            label="Token prefix"
            maxLength={4}
            hint="Up to 4 letters before queue numbers on display boards, such as GM for GM-012. Empty: the code is used."
          />
          <NumberField
            control={control}
            name="displayOrder"
            label="Position in lists"
            min={0}
            max={9999}
            step={10}
            hint="Lists show departments from the lowest number."
          />
        </FormGrid>
      </EditorSection>

      <EditorSection title="Use">
        <SwitchField
          control={control}
          name="isActive"
          label="In use"
          hint="A department not in use takes no new visits, and isn't offered in pickers. Its history stays."
        />
      </EditorSection>
    </EditorForm>
  );
}
