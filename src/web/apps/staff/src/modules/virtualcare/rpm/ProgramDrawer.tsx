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
import { isApiError } from '@hc360/core';
import {
  applyServerErrors,
  NumberField,
  SelectField,
  SwitchField,
  TextAreaField,
  TextField,
  useNotify,
} from '@hc360/ui';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useForm } from 'react-hook-form';
import { z } from 'zod';
import { PROGRAM_KINDS, rpmApi, rpmKeys, type RpmProgramDto } from './api';
import { programKindLabel } from './labels';

const schema = z.object({
  code: z
    .string()
    .trim()
    .min(1, 'Enter a short code.')
    .max(16, 'Use at most 16 characters.')
    .regex(/^[A-Za-z0-9_-]+$/, 'Use letters, digits, dashes or underscores.'),
  name: z.string().trim().min(1, 'Enter a name.').max(100, 'Use at most 100 characters.'),
  kind: z.enum(PROGRAM_KINDS),
  description: z.string().trim().max(500, 'Use at most 500 characters.'),
  expectedReadingsPerDay: z
    .number({ message: 'Enter a number from 1 to 12.' })
    .int('Enter a whole number.')
    .min(1, 'At least 1.')
    .max(12, 'At most 12.'),
  isActive: z.boolean(),
});

type ProgramValues = z.infer<typeof schema>;

const useStyles = makeStyles({
  form: { display: 'flex', flexDirection: 'column', gap: '12px' },
});

/** A monitoring programme (for example hypertension) with the number of readings expected each day. */
export function ProgramDrawer({
  open,
  program,
  onClose,
}: {
  open: boolean;
  program: RpmProgramDto | null;
  onClose: () => void;
}) {
  return (
    <OverlayDrawer open={open} position="end" size="small" onOpenChange={(_, d) => !d.open && onClose()}>
      {open && <ProgramForm key={program?.id ?? 'new'} program={program} onClose={onClose} />}
    </OverlayDrawer>
  );
}

function ProgramForm({ program, onClose }: { program: RpmProgramDto | null; onClose: () => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const form = useForm<ProgramValues>({
    resolver: zodResolver(schema),
    defaultValues: {
      code: program?.code ?? '',
      name: program?.name ?? '',
      kind: program?.kind ?? 'Custom',
      description: program?.description ?? '',
      expectedReadingsPerDay: program?.expectedReadingsPerDay ?? 1,
      isActive: program?.isActive ?? true,
    },
  });

  const save = useMutation({
    mutationFn: async (v: ProgramValues): Promise<void> => {
      await rpmApi.saveProgram(program?.id ?? null, {
        code: v.code.toUpperCase(),
        name: v.name,
        kind: v.kind,
        description: v.description || null,
        expectedReadingsPerDay: v.expectedReadingsPerDay,
        isActive: v.isActive,
        version: program?.version ?? null,
      });
    },
    onSuccess: () => {
      notify.success(program ? 'Programme saved' : 'Programme added');
      void queryClient.invalidateQueries({ queryKey: rpmKeys.programs });
      onClose();
    },
    onError: (error) => {
      if (isApiError(error) && error.isConflict && error.code !== 'rpm.program_code_in_use') {
        notify.error(error, 'Someone else changed this programme');
        void queryClient.invalidateQueries({ queryKey: rpmKeys.programs });
        onClose();
        return;
      }
      if (isApiError(error) && error.code === 'rpm.program_code_in_use') {
        form.setError('code', { type: 'server', message: error.userMessage });
        return;
      }
      if (!applyServerErrors(error, form.setError)) {
        notify.error(error, 'Could not save the programme');
      }
    },
  });

  return (
    <form onSubmit={form.handleSubmit((v) => save.mutate(v))} noValidate style={{ display: 'contents' }}>
      <DrawerHeader>
        <DrawerHeaderTitle
          action={<Button appearance="subtle" icon={<DismissRegular />} aria-label="Close" onClick={onClose} />}
        >
          {program ? program.name : 'Add programme'}
        </DrawerHeaderTitle>
      </DrawerHeader>
      <DrawerBody>
        <div className={styles.form}>
          <TextField
            control={form.control}
            name="code"
            label="Code"
            required
            maxLength={16}
            hint="Short and unique, e.g. HTN"
            autoFocus
          />
          <TextField control={form.control} name="name" label="Name" required maxLength={100} />
          <SelectField
            control={form.control}
            name="kind"
            label="Kind"
            hint="Sets the standard limits applied to new enrolments."
            options={PROGRAM_KINDS.map((k) => ({ value: k, label: programKindLabel[k] }))}
          />
          <NumberField
            control={form.control}
            name="expectedReadingsPerDay"
            label="Readings expected per day"
            required
            min={1}
            max={12}
          />
          <TextAreaField
            control={form.control}
            name="description"
            label="Description (optional)"
            rows={3}
            maxLength={500}
          />
          <SwitchField control={form.control} name="isActive" label="Programme is open for new enrolments" />
        </div>
      </DrawerBody>
      <DrawerFooter>
        <Button
          type="submit"
          appearance="primary"
          disabled={save.isPending}
          icon={save.isPending ? <Spinner size="tiny" aria-hidden /> : undefined}
        >
          {program ? 'Save' : 'Add programme'}
        </Button>
        <Button onClick={onClose}>Cancel</Button>
      </DrawerFooter>
    </form>
  );
}
