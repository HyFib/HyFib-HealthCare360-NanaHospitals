import {
  Button,
  Caption1,
  DrawerBody,
  DrawerFooter,
  DrawerHeader,
  DrawerHeaderTitle,
  Field,
  makeStyles,
  MessageBar,
  MessageBarActions,
  MessageBarBody,
  MessageBarTitle,
  OverlayDrawer,
  Select,
  Spinner,
  Subtitle2,
  tokens,
} from '@fluentui/react-components';
import { AddRegular, ArrowClockwiseRegular, DeleteRegular, DismissRegular } from '@fluentui/react-icons';
import { zodResolver } from '@hookform/resolvers/zod';
import { useSession } from '@hc360/core';
import {
  applyServerErrors,
  CloseRequestScope,
  ErrorState,
  FormGrid,
  FullRow,
  LoadingState,
  NumberField,
  SelectField,
  SwitchField,
  TextAreaField,
  TextField,
  useCloseRequests,
  useNotify,
  useUnsavedChanges,
} from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { Controller, useFieldArray, useForm, useWatch, type Control, type UseFormSetValue } from 'react-hook-form';
import { emrApi, emrKeys, emrQueries, type TemplateDto } from './api';
import {
  CODE_HINT,
  emptyMedicine,
  isCodeInUse,
  isStale,
  templateSchema,
  templateValues,
  toTemplateRequest,
  useSpecialtyOptions,
  type TemplateValues,
} from './catalogue';
import { PickedServices } from './catalogueParts';
import { DOSE_FREQUENCIES, frequencyOptionLabel, ROUTES, type DoseFrequency } from './dosing';
import { DrugPicker } from './DrugPicker';

const useStyles = makeStyles({
  form: { display: 'flex', flexDirection: 'column', gap: '16px' },
  part: { display: 'flex', flexDirection: 'column', gap: '12px' },
  medicine: {
    display: 'flex',
    flexDirection: 'column',
    gap: '8px',
    padding: '12px',
    borderRadius: tokens.borderRadiusMedium,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  medicineHead: { display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: '8px' },
  muted: { color: tokens.colorNeutralForeground3 },
});

/**
 * Create or edit a clinical note template (EMR-02): the text it fills into an encounter's note, the complaints offered
 * as chips, and the orders and medicines it proposes. `templateId` is the template to edit, 'new' or null (closed); an
 * edit loads the template afresh, and after someone else's change is reloaded the form opens again on the new version.
 */
export function TemplateDrawer({ templateId, onClose }: { templateId: string | 'new' | null; onClose: () => void }) {
  const closing = useCloseRequests({ onClose });
  return (
    <OverlayDrawer open={templateId !== null} position="end" size="large" onOpenChange={closing.onOpenChange}>
      {templateId !== null && (
        <CloseRequestScope value={closing}>
          {templateId === 'new' ? (
            <TemplateForm template={null} onClose={onClose} />
          ) : (
            <LoadedTemplate templateId={templateId} onClose={onClose} />
          )}
        </CloseRequestScope>
      )}
    </OverlayDrawer>
  );
}

function LoadedTemplate({ templateId, onClose }: { templateId: string; onClose: () => void }) {
  // Always read afresh: the version an edit is saved on is the one loaded here.
  const template = useQuery({ ...emrQueries.template(templateId), staleTime: 0 });
  if (template.data) {
    return (
      <TemplateForm
        key={template.data.version}
        template={template.data}
        onClose={onClose}
        onReload={() => void template.refetch()}
      />
    );
  }
  return (
    <>
      <DrawerHeader>
        <DrawerHeaderTitle
          action={<Button appearance="subtle" icon={<DismissRegular />} aria-label="Close" onClick={onClose} />}
        >
          Template
        </DrawerHeaderTitle>
      </DrawerHeader>
      <DrawerBody>
        {template.error ? (
          <ErrorState error={template.error} onRetry={() => void template.refetch()} />
        ) : (
          <LoadingState label="Loading the template…" />
        )}
      </DrawerBody>
    </>
  );
}

function TemplateForm({
  template,
  onClose,
  onReload,
}: {
  template: TemplateDto | null;
  onClose: () => void;
  onReload?: () => void;
}) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const practitionerId = useSession((state) => state.user?.practitionerId ?? null);
  const [stale, setStale] = useState(false);
  const form = useForm<TemplateValues>({
    resolver: zodResolver(templateSchema),
    defaultValues: templateValues(template),
  });
  const medicines = useFieldArray({ control: form.control, name: 'medications' });
  const specialties = useSpecialtyOptions(template?.specialtyCode);

  const save = useMutation({
    mutationFn: (values: TemplateValues) =>
      emrApi.saveTemplate(template?.id ?? null, toTemplateRequest(values, template?.version ?? null)),
    onSuccess: (saved) => {
      notify.success(template ? 'Template saved' : 'Template added', saved.name);
      void queryClient.invalidateQueries({ queryKey: ['emr', 'templates'] });
      queryClient.setQueryData(emrKeys.template(saved.id), saved);
      onClose();
    },
    onError: (error) => {
      if (isCodeInUse(error)) {
        form.setError('code', { message: error.userMessage });
      } else if (isStale(error)) {
        setStale(true);
      } else if (!applyServerErrors(error, form.setError)) {
        notify.error(error, 'Could not save the template');
      }
    },
  });

  const guard = useUnsavedChanges({ dirty: form.formState.isDirty, busy: save.isPending, onClose });
  const medicinesError = form.formState.errors.medications?.root?.message ?? form.formState.errors.medications?.message;

  return (
    <form onSubmit={form.handleSubmit((v) => save.mutate(v))} noValidate style={{ display: 'contents' }}>
      <DrawerHeader>
        <DrawerHeaderTitle
          action={
            <Button appearance="subtle" icon={<DismissRegular />} aria-label="Close" onClick={guard.requestClose} />
          }
        >
          {template ? template.name : 'New template'}
        </DrawerHeaderTitle>
      </DrawerHeader>
      <DrawerBody>
        <div className={styles.form}>
          {stale && (
            <MessageBar intent="warning" layout="multiline" politeness="assertive">
              <MessageBarBody>
                <MessageBarTitle>Someone else changed this template</MessageBarTitle>
                Reload to see their change, then make yours again. Nothing was saved.
              </MessageBarBody>
              {onReload && (
                <MessageBarActions>
                  <Button icon={<ArrowClockwiseRegular />} onClick={onReload}>
                    Reload
                  </Button>
                </MessageBarActions>
              )}
            </MessageBar>
          )}

          <FormGrid columns={2}>
            <TextField
              control={form.control}
              name="code"
              label="Code"
              required
              autoFocus={!template}
              maxLength={32}
              hint={CODE_HINT}
            />
            <TextField control={form.control} name="name" label="Name" required maxLength={200} />
            <SelectField
              control={form.control}
              name="specialtyCode"
              label="Speciality"
              placeholder="General (no speciality)"
              options={specialties.options}
            />
            <NumberField
              control={form.control}
              name="displayOrder"
              label="Display order"
              step={1}
              hint="Lower numbers come first in the encounter's list."
            />
            {(practitionerId !== null || template?.ownerPractitionerId) && (
              <SwitchField
                control={form.control}
                name="personal"
                label="Personal (only I see it)"
                hint="Otherwise it is shared with every doctor."
              />
            )}
            <SwitchField control={form.control} name="isActive" label="Active (offered in encounters)" />
          </FormGrid>

          <div className={styles.part}>
            <Subtitle2 as="h3">Note text</Subtitle2>
            <Caption1 className={styles.muted}>Filled into the encounter's empty note fields when chosen.</Caption1>
            <TextAreaField
              control={form.control}
              name="chiefComplaint"
              label="Chief complaint"
              rows={2}
              maxLength={1000}
            />
            <TextAreaField
              control={form.control}
              name="historyOfPresentIllness"
              label="History of present illness"
              rows={3}
              maxLength={4000}
            />
            <TextAreaField control={form.control} name="examination" label="Examination" rows={4} maxLength={4000} />
            <TextAreaField control={form.control} name="assessment" label="Assessment" rows={2} maxLength={4000} />
            <TextAreaField control={form.control} name="plan" label="Plan" rows={2} maxLength={4000} />
            <FormGrid columns={2}>
              <TextAreaField
                control={form.control}
                name="followUpAdvice"
                label="Follow-up advice"
                rows={2}
                maxLength={1000}
              />
              <NumberField control={form.control} name="followUpDays" label="Follow up in (days)" min={1} max={365} />
            </FormGrid>
            <TextAreaField
              control={form.control}
              name="commonComplaints"
              label="Common complaints"
              rows={3}
              hint="One per line, offered as quick picks (at most 30)."
            />
          </div>

          <div className={styles.part}>
            <Subtitle2 as="h3">Default orders</Subtitle2>
            <Controller
              control={form.control}
              name="services"
              render={({ field, fieldState }) => (
                <PickedServices
                  label="Tests and services"
                  services={field.value}
                  onChange={field.onChange}
                  max={30}
                  error={fieldState.error?.message}
                />
              )}
            />
          </div>

          <div className={styles.part}>
            <Subtitle2 as="h3">Default medicines</Subtitle2>
            {medicines.fields.length === 0 && <Caption1 className={styles.muted}>No medicines proposed.</Caption1>}
            {medicines.fields.map((field, index) => (
              <MedicineRow
                key={field.id}
                index={index}
                control={form.control}
                setValue={form.setValue}
                onRemove={() => medicines.remove(index)}
              />
            ))}
            {medicinesError && (
              <Caption1 role="alert" style={{ color: tokens.colorPaletteRedForeground1 }}>
                {medicinesError}
              </Caption1>
            )}
            <div>
              <Button
                icon={<AddRegular />}
                disabled={medicines.fields.length >= 20}
                onClick={() => medicines.append(emptyMedicine())}
              >
                Add medicine
              </Button>
            </div>
          </div>
        </div>
      </DrawerBody>
      <DrawerFooter>
        <Button
          type="submit"
          appearance="primary"
          disabled={save.isPending || stale}
          icon={save.isPending ? <Spinner size="tiny" aria-hidden /> : undefined}
        >
          Save
        </Button>
        <Button onClick={guard.requestClose}>Cancel</Button>
      </DrawerFooter>
      {guard.dialogs}
    </form>
  );
}

/** One proposed medicine: picked from favourites or the drug master (or typed), with its usual dose and course. */
function MedicineRow({
  index,
  control,
  setValue,
  onRemove,
}: {
  index: number;
  control: Control<TemplateValues>;
  setValue: UseFormSetValue<TemplateValues>;
  onRemove: () => void;
}) {
  const styles = useStyles();
  const label = `Medicine ${index + 1}`;
  const path = `medications.${index}` as const;
  const set = { shouldDirty: true, shouldValidate: false } as const;
  return (
    <div className={styles.medicine} role="group" aria-label={label}>
      <div className={styles.medicineHead}>
        <Caption1 className={styles.muted}>{label}</Caption1>
        <Button
          size="small"
          appearance="subtle"
          icon={<DeleteRegular />}
          aria-label={`Remove ${label.toLowerCase()}`}
          onClick={onRemove}
        />
      </div>
      <FormGrid columns={3} minColumnWidth={160}>
        <FullRow>
          <Controller
            control={control}
            name={`${path}.drugName`}
            render={({ field, fieldState }) => (
              <Field
                label="Medicine"
                required
                validationMessage={fieldState.error?.message}
                validationState={fieldState.error ? 'error' : 'none'}
              >
                <DrugPicker
                  ariaLabel={`${label} name`}
                  value={field.value}
                  invalid={!!fieldState.error}
                  onChange={(value) => {
                    field.onChange(value);
                    // A name typed over a picked medicine is no longer that drug master item.
                    setValue(`${path}.drugItemId`, null, set);
                  }}
                  onPick={(picked) => {
                    field.onChange(picked.drugName);
                    setValue(`${path}.drugItemId`, picked.drugItemId ?? null, set);
                    setValue(`${path}.genericName`, picked.genericName ?? '', set);
                    setValue(`${path}.strength`, picked.strength ?? '', set);
                    setValue(`${path}.dosageForm`, picked.dosageForm ?? '', set);
                    if (picked.route) setValue(`${path}.route`, picked.route, set);
                    if (picked.dose) setValue(`${path}.dose`, picked.dose, set);
                    if (picked.frequency) setValue(`${path}.frequency`, picked.frequency, set);
                    if (picked.customFrequency) setValue(`${path}.customFrequency`, picked.customFrequency, set);
                    if (picked.durationDays) setValue(`${path}.durationDays`, picked.durationDays, set);
                    if (picked.instructions) setValue(`${path}.instructions`, picked.instructions, set);
                  }}
                />
              </Field>
            )}
          />
        </FullRow>
        <TextField control={control} name={`${path}.strength`} label="Strength" maxLength={64} />
        <Controller
          control={control}
          name={`${path}.route`}
          render={({ field }) => (
            <Field label="Route">
              <Select value={field.value} onChange={(_, d) => field.onChange(d.value)} onBlur={field.onBlur}>
                <option value="">Not stated</option>
                {ROUTES.map((r) => (
                  <option key={r} value={r}>
                    {r}
                  </option>
                ))}
                {field.value && !(ROUTES as readonly string[]).includes(field.value) && (
                  <option value={field.value}>{field.value}</option>
                )}
              </Select>
            </Field>
          )}
        />
        <TextField control={control} name={`${path}.dose`} label="Dose" required maxLength={64} />
        <Controller
          control={control}
          name={`${path}.frequency`}
          render={({ field }) => (
            <Field label="Frequency" required>
              <Select
                value={field.value}
                onChange={(_, d) => field.onChange(d.value as DoseFrequency)}
                onBlur={field.onBlur}
              >
                {DOSE_FREQUENCIES.map((f) => (
                  <option key={f} value={f}>
                    {frequencyOptionLabel(f)}
                  </option>
                ))}
              </Select>
            </Field>
          )}
        />
        <CustomFrequency control={control} index={index} />
        <NumberField control={control} name={`${path}.durationDays`} label="Days" required min={1} max={365} />
        <FullRow>
          <TextField control={control} name={`${path}.instructions`} label="Instructions" maxLength={300} />
        </FullRow>
      </FormGrid>
    </div>
  );
}

/** The frequency in words, asked for only when the frequency is custom. */
function CustomFrequency({ control, index }: { control: Control<TemplateValues>; index: number }) {
  const frequency = useWatch({ control, name: `medications.${index}.frequency` });
  if (frequency !== 'Custom') {
    return null;
  }
  return (
    <TextField
      control={control}
      name={`medications.${index}.customFrequency`}
      label="Custom frequency"
      required
      maxLength={64}
      placeholder="e.g. 1-0-1"
    />
  );
}
