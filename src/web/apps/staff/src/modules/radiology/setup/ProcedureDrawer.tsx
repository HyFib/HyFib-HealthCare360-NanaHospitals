import {
  Button,
  DrawerBody,
  DrawerFooter,
  DrawerHeader,
  DrawerHeaderTitle,
  makeStyles,
  OverlayDrawer,
  Spinner,
} from '@fluentui/react-components';
import { DismissRegular } from '@fluentui/react-icons';
import { zodResolver } from '@hookform/resolvers/zod';
import {
  applyServerErrors,
  FormGrid,
  FullRow,
  NumberField,
  SelectField,
  SwitchField,
  TextAreaField,
  TextField,
  useNotify,
  useUnsavedChanges,
} from '@hc360/ui';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { Controller, useForm } from 'react-hook-form';
import { z } from 'zod';
import { ServiceLookup } from '@/modules/lab/common/ServiceLookup';
import { MODALITIES, radiologyApi, radiologyKeys, type ImagingProcedureDto } from '../api';
import { modalityLabel } from '../labels';

const schema = z.object({
  code: z
    .string()
    .trim()
    .min(1, 'Enter a code, e.g. RAD-CT-HEAD.')
    .max(32)
    .regex(/^[A-Za-z0-9_-]+$/, 'Use letters, digits, - or _.'),
  name: z.string().trim().min(1, 'Enter the procedure name.').max(200),
  modality: z.enum(MODALITIES),
  bodyPart: z.string().trim().min(1, 'Enter the body part, e.g. Chest.').max(100),
  serviceItemId: z.string().nullable(),
  durationMinutes: z.number().int('Whole minutes.').min(5, '5 to 240 minutes').max(240, '5 to 240 minutes').nullable(),
  requiresContrast: z.boolean(),
  patientPreparation: z.string().trim().max(1000),
  isActive: z.boolean(),
});

type ProcedureValues = z.infer<typeof schema>;

const useStyles = makeStyles({ form: { display: 'flex', flexDirection: 'column', gap: '12px' } });

/** Imaging procedure master: modality, body part, slot length, contrast and preparation, linked to its billable service. */
export function ProcedureDrawer({
  procedure,
  onClose,
}: {
  procedure: ImagingProcedureDto | null;
  onClose: () => void;
}) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const form = useForm<ProcedureValues>({
    resolver: zodResolver(schema),
    defaultValues: {
      code: procedure?.code ?? '',
      name: procedure?.name ?? '',
      modality: procedure?.modality ?? 'XR',
      bodyPart: procedure?.bodyPart ?? '',
      serviceItemId: procedure?.serviceItemId ?? null,
      durationMinutes: procedure?.durationMinutes ?? 15,
      requiresContrast: procedure?.requiresContrast ?? false,
      patientPreparation: procedure?.patientPreparation ?? '',
      isActive: procedure?.isActive ?? true,
    },
  });

  const save = useMutation({
    mutationFn: (values: ProcedureValues) =>
      radiologyApi.saveProcedure(procedure?.id ?? null, {
        code: values.code.toUpperCase(),
        name: values.name,
        modality: values.modality,
        bodyPart: values.bodyPart,
        serviceItemId: values.serviceItemId,
        durationMinutes: values.durationMinutes ?? 15,
        requiresContrast: values.requiresContrast,
        patientPreparation: values.patientPreparation || null,
        isActive: values.isActive,
        version: procedure?.version ?? null,
      }),
    onSuccess: () => {
      notify.success(procedure ? 'Procedure saved' : 'Procedure added');
      void queryClient.invalidateQueries({ queryKey: radiologyKeys.masters });
      onClose();
    },
    onError: (error) => {
      if (!applyServerErrors(error, form.setError)) {
        notify.error(error, 'Could not save the procedure');
      }
    },
  });

  const guard = useUnsavedChanges({ dirty: form.formState.isDirty, busy: save.isPending, onClose });

  return (
    <OverlayDrawer open position="end" size="medium" onOpenChange={guard.onOpenChange}>
      <form onSubmit={form.handleSubmit((values) => save.mutate(values))} noValidate style={{ display: 'contents' }}>
        <DrawerHeader>
          <DrawerHeaderTitle
            action={
              <Button appearance="subtle" icon={<DismissRegular />} aria-label="Close" onClick={guard.requestClose} />
            }
          >
            {procedure ? procedure.name : 'Add imaging procedure'}
          </DrawerHeaderTitle>
        </DrawerHeader>
        <DrawerBody>
          <div className={styles.form}>
            <FormGrid columns={2}>
              <TextField control={form.control} name="code" label="Code" required disabled={Boolean(procedure)} />
              <TextField control={form.control} name="name" label="Name" required />
              <SelectField
                control={form.control}
                name="modality"
                label="Modality"
                options={MODALITIES.map((value) => ({ value, label: `${modalityLabel[value]} (${value})` }))}
              />
              <TextField
                control={form.control}
                name="bodyPart"
                label="Body part"
                required
                placeholder="e.g. Chest, Abdomen, Brain"
              />
              <NumberField
                control={form.control}
                name="durationMinutes"
                label="Slot length (minutes)"
                required
                min={5}
                max={240}
              />
              <SwitchField control={form.control} name="requiresContrast" label="Usually needs contrast" />
              <FullRow>
                <Controller
                  control={form.control}
                  name="serviceItemId"
                  render={({ field, fieldState }) => (
                    <ServiceLookup
                      category="Radiology"
                      value={field.value}
                      onChange={field.onChange}
                      validationMessage={fieldState.error?.message}
                    />
                  )}
                />
              </FullRow>
              <FullRow>
                <TextAreaField
                  control={form.control}
                  name="patientPreparation"
                  label="Patient preparation"
                  rows={3}
                  maxLength={1000}
                  placeholder="e.g. Fasting 6 hours; bring previous reports"
                />
              </FullRow>
            </FormGrid>
            <SwitchField control={form.control} name="isActive" label="In use" />
          </div>
        </DrawerBody>
        <DrawerFooter>
          <Button
            type="submit"
            appearance="primary"
            disabled={save.isPending}
            icon={save.isPending ? <Spinner size="tiny" /> : undefined}
          >
            {procedure ? 'Save' : 'Add procedure'}
          </Button>
          <Button onClick={guard.requestClose}>Cancel</Button>
        </DrawerFooter>
      </form>
      {guard.dialogs}
    </OverlayDrawer>
  );
}
