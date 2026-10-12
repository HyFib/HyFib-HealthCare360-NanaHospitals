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
import { applyServerErrors, ChoiceField, FormGrid, SelectField, SwitchField, TextField, useNotify } from '@hc360/ui';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useForm } from 'react-hook-form';
import { z } from 'zod';
import { LAB_SECTIONS, labApi, labKeys, type LabInstrumentDto } from '../api';
import { sectionLabel } from '../labels';

const schema = z.object({
  code: z
    .string()
    .trim()
    .min(1, 'Enter the code the analyser sends, e.g. XN550.')
    .max(32)
    .regex(/^[A-Za-z0-9_-]+$/, 'Use letters, digits, - or _.'),
  name: z.string().trim().min(1, 'Enter a name.').max(200),
  manufacturer: z.string().trim().max(100),
  model: z.string().trim().max(100),
  section: z.enum(LAB_SECTIONS),
  protocol: z.enum(['Hl7', 'Astm']),
  isBidirectional: z.boolean(),
  autoValidationEnabled: z.boolean(),
  location: z.string().trim().max(100),
  isActive: z.boolean(),
});

type InstrumentValues = z.infer<typeof schema>;

const useStyles = makeStyles({ form: { display: 'flex', flexDirection: 'column', gap: '12px' } });

/** An analyser (HL7 or ASTM). Bidirectional ones ask which tests to run and are sent the orders of received samples. */
export function InstrumentDrawer({
  instrument,
  onClose,
}: {
  instrument: LabInstrumentDto | null;
  onClose: () => void;
}) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const form = useForm<InstrumentValues>({
    resolver: zodResolver(schema),
    defaultValues: {
      code: instrument?.code ?? '',
      name: instrument?.name ?? '',
      manufacturer: instrument?.manufacturer ?? '',
      model: instrument?.model ?? '',
      section: instrument?.section ?? 'Biochemistry',
      protocol: instrument?.protocol ?? 'Astm',
      isBidirectional: instrument?.isBidirectional ?? false,
      autoValidationEnabled: instrument?.autoValidationEnabled ?? false,
      location: instrument?.location ?? '',
      isActive: instrument?.isActive ?? true,
    },
  });

  const save = useMutation({
    mutationFn: (values: InstrumentValues) =>
      labApi.saveInstrument(instrument?.id ?? null, {
        code: values.code.toUpperCase(),
        name: values.name,
        manufacturer: values.manufacturer || null,
        model: values.model || null,
        section: values.section,
        protocol: values.protocol,
        isBidirectional: values.isBidirectional,
        autoValidationEnabled: values.autoValidationEnabled,
        location: values.location || null,
        isActive: values.isActive,
        version: instrument?.version ?? null,
      }),
    onSuccess: () => {
      notify.success(instrument ? 'Analyser saved' : 'Analyser added');
      void queryClient.invalidateQueries({ queryKey: labKeys.masters });
      onClose();
    },
    onError: (error) => {
      if (!applyServerErrors(error, form.setError)) {
        notify.error(error, 'Could not save the analyser');
      }
    },
  });

  return (
    <OverlayDrawer open position="end" size="medium" onOpenChange={(_, data) => !data.open && onClose()}>
      <form onSubmit={form.handleSubmit((values) => save.mutate(values))} noValidate style={{ display: 'contents' }}>
        <DrawerHeader>
          <DrawerHeaderTitle
            action={<Button appearance="subtle" icon={<DismissRegular />} aria-label="Close" onClick={onClose} />}
          >
            {instrument ? instrument.name : 'Add analyser'}
          </DrawerHeaderTitle>
        </DrawerHeader>
        <DrawerBody>
          <div className={styles.form}>
            <FormGrid columns={2}>
              <TextField
                control={form.control}
                name="code"
                label="Code"
                required
                disabled={Boolean(instrument)}
                hint="The code the analyser sends with its results"
              />
              <TextField control={form.control} name="name" label="Name" required />
              <TextField control={form.control} name="manufacturer" label="Manufacturer" />
              <TextField control={form.control} name="model" label="Model" />
              <SelectField
                control={form.control}
                name="section"
                label="Bench"
                options={LAB_SECTIONS.map((value) => ({ value, label: sectionLabel[value] }))}
              />
              <TextField
                control={form.control}
                name="location"
                label="Location"
                placeholder="e.g. Central lab, bench 2"
              />
            </FormGrid>
            <ChoiceField
              control={form.control}
              name="protocol"
              label="Interface"
              options={[
                { value: 'Astm', label: 'ASTM' },
                { value: 'Hl7', label: 'HL7' },
              ]}
            />
            <SwitchField
              control={form.control}
              name="isBidirectional"
              label="Bidirectional"
              hint="The analyser may ask which tests to run, and HC360 sends it the orders of each sample received in the lab. Set the analyser to match."
            />
            <SwitchField
              control={form.control}
              name="autoValidationEnabled"
              label="Normal results may be verified automatically"
              hint="Only for tests that also allow it, with QC accepted and delta check passed"
            />
            <SwitchField control={form.control} name="isActive" label="In use" />
          </div>
        </DrawerBody>
        <DrawerFooter>
          <Button
            type="submit"
            appearance="primary"
            disabled={save.isPending}
            icon={save.isPending ? <Spinner size="tiny" aria-hidden /> : undefined}
          >
            {instrument ? 'Save' : 'Add analyser'}
          </Button>
          <Button onClick={onClose}>Cancel</Button>
        </DrawerFooter>
      </form>
    </OverlayDrawer>
  );
}
