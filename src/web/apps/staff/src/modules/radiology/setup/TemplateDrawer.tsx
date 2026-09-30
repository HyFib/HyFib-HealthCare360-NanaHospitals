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
import { applyServerErrors, FormGrid, SelectField, SwitchField, TextAreaField, TextField, useNotify } from '@hc360/ui';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useForm } from 'react-hook-form';
import { z } from 'zod';
import { MODALITIES, radiologyApi, radiologyKeys, type ReportTemplateDto } from '../api';
import { modalityLabel } from '../labels';

const schema = z.object({
  code: z
    .string()
    .trim()
    .min(1, 'Enter a code, e.g. TPL-DX-CHEST.')
    .max(32)
    .regex(/^[A-Za-z0-9_-]+$/, 'Use letters, digits, - or _.'),
  name: z.string().trim().min(1, 'Enter the template name.').max(200),
  modality: z.enum(MODALITIES),
  bodyPart: z.string().trim().max(100),
  technique: z.string().max(2000),
  findings: z.string().trim().min(1, 'Enter the normal findings text.').max(8000),
  impression: z.string().trim().min(1, 'Enter the usual impression.').max(4000),
  isActive: z.boolean(),
});

type TemplateValues = z.infer<typeof schema>;

const useStyles = makeStyles({ form: { display: 'flex', flexDirection: 'column', gap: '12px' } });

/**
 * Report template (normal study wording) that radiologists start from and edit. Templates matching the study's body
 * part are offered first.
 */
export function TemplateDrawer({ template, onClose }: { template: ReportTemplateDto | null; onClose: () => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const form = useForm<TemplateValues>({
    resolver: zodResolver(schema),
    defaultValues: {
      code: template?.code ?? '',
      name: template?.name ?? '',
      modality: template?.modality ?? 'XR',
      bodyPart: template?.bodyPart ?? '',
      technique: template?.technique ?? '',
      findings: template?.findings ?? '',
      impression: template?.impression ?? '',
      isActive: template?.isActive ?? true,
    },
  });

  const save = useMutation({
    mutationFn: (values: TemplateValues) =>
      radiologyApi.saveTemplate(template?.id ?? null, {
        code: values.code.toUpperCase(),
        name: values.name,
        modality: values.modality,
        bodyPart: values.bodyPart || null,
        technique: values.technique.trim() || null,
        findings: values.findings,
        impression: values.impression,
        isActive: values.isActive,
        version: template?.version ?? null,
      }),
    onSuccess: () => {
      notify.success(template ? 'Template saved' : 'Template added');
      void queryClient.invalidateQueries({ queryKey: radiologyKeys.masters });
      onClose();
    },
    onError: (error) => {
      if (!applyServerErrors(error, form.setError)) {
        notify.error(error, 'Could not save the template');
      }
    },
  });

  return (
    <OverlayDrawer open position="end" size="large" onOpenChange={(_, data) => !data.open && onClose()}>
      <form onSubmit={form.handleSubmit((values) => save.mutate(values))} noValidate style={{ display: 'contents' }}>
        <DrawerHeader>
          <DrawerHeaderTitle
            action={<Button appearance="subtle" icon={<DismissRegular />} aria-label="Close" onClick={onClose} />}
          >
            {template ? template.name : 'Add report template'}
          </DrawerHeaderTitle>
        </DrawerHeader>
        <DrawerBody>
          <div className={styles.form}>
            <FormGrid columns={2}>
              <TextField control={form.control} name="code" label="Code" required disabled={Boolean(template)} />
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
                hint="Leave empty for a general template"
              />
            </FormGrid>
            <TextAreaField control={form.control} name="technique" label="Technique" rows={2} maxLength={2000} />
            <TextAreaField
              control={form.control}
              name="findings"
              label="Findings"
              required
              rows={10}
              maxLength={8000}
            />
            <TextAreaField
              control={form.control}
              name="impression"
              label="Impression"
              required
              rows={3}
              maxLength={4000}
            />
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
            {template ? 'Save' : 'Add template'}
          </Button>
          <Button onClick={onClose}>Cancel</Button>
        </DrawerFooter>
      </form>
    </OverlayDrawer>
  );
}
