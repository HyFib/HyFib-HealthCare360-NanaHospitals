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
import { normalizePhone } from '@hc360/core';
import {
  applyServerErrors,
  FormGrid,
  FullRow,
  NumberField,
  SwitchField,
  TextAreaField,
  TextField,
  useNotify,
  useUnsavedChanges,
} from '@hc360/ui';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useForm } from 'react-hook-form';
import { z } from 'zod';
import { labApi, labKeys, type ReferralLabDto } from '../api';

const schema = z.object({
  code: z
    .string()
    .trim()
    .min(1, 'Enter a short code, e.g. MRL.')
    .max(32)
    .regex(/^[A-Za-z0-9_-]+$/, 'Use letters, digits, - or _.'),
  name: z.string().trim().min(1, 'Enter the laboratory name.').max(200),
  contactPerson: z.string().trim().max(200),
  phone: z
    .string()
    .trim()
    .refine((v) => !v || normalizePhone(v) !== null, 'Enter a valid phone number.'),
  email: z
    .string()
    .trim()
    .max(256)
    .refine((v) => !v || /^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(v), 'Enter a valid email address.'),
  address: z.string().trim().max(500),
  accreditationNumber: z.string().trim().max(64),
  tatDays: z.number().int('Whole days only.').min(1, '1 to 60 days').max(60, '1 to 60 days').nullable(),
  isActive: z.boolean(),
});

type ReferralValues = z.infer<typeof schema>;

const useStyles = makeStyles({ form: { display: 'flex', flexDirection: 'column', gap: '12px' } });

/** Reference laboratory for outsourced tests; its agreed turnaround sets the due date of every sample sent. */
export function ReferralLabDrawer({ lab, onClose }: { lab: ReferralLabDto | null; onClose: () => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const form = useForm<ReferralValues>({
    resolver: zodResolver(schema),
    defaultValues: {
      code: lab?.code ?? '',
      name: lab?.name ?? '',
      contactPerson: lab?.contactPerson ?? '',
      phone: lab?.phone ?? '',
      email: lab?.email ?? '',
      address: lab?.address ?? '',
      accreditationNumber: lab?.accreditationNumber ?? '',
      tatDays: lab?.tatDays ?? 3,
      isActive: lab?.isActive ?? true,
    },
  });

  const save = useMutation({
    mutationFn: (values: ReferralValues) =>
      labApi.saveReferralLab(lab?.id ?? null, {
        code: values.code.toUpperCase(),
        name: values.name,
        contactPerson: values.contactPerson || null,
        phone: values.phone ? normalizePhone(values.phone) : null,
        email: values.email || null,
        address: values.address || null,
        accreditationNumber: values.accreditationNumber || null,
        tatDays: values.tatDays ?? 3,
        isActive: values.isActive,
        version: lab?.version ?? null,
      }),
    onSuccess: () => {
      notify.success(lab ? 'Reference laboratory saved' : 'Reference laboratory added');
      void queryClient.invalidateQueries({ queryKey: labKeys.masters });
      onClose();
    },
    onError: (error) => {
      if (!applyServerErrors(error, form.setError)) {
        notify.error(error, 'Could not save the reference laboratory');
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
            {lab ? lab.name : 'Add reference laboratory'}
          </DrawerHeaderTitle>
        </DrawerHeader>
        <DrawerBody>
          <div className={styles.form}>
            <FormGrid columns={2}>
              <TextField control={form.control} name="code" label="Code" required disabled={Boolean(lab)} />
              <TextField control={form.control} name="name" label="Name" required />
              <TextField control={form.control} name="accreditationNumber" label="NABL accreditation number" />
              <NumberField
                control={form.control}
                name="tatDays"
                label="Agreed turnaround (days)"
                required
                min={1}
                max={60}
              />
              <TextField control={form.control} name="contactPerson" label="Contact person" />
              <TextField control={form.control} name="phone" label="Phone" type="tel" />
              <FullRow>
                <TextField control={form.control} name="email" label="Email" type="email" />
              </FullRow>
              <FullRow>
                <TextAreaField control={form.control} name="address" label="Address" rows={2} maxLength={500} />
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
            icon={save.isPending ? <Spinner size="tiny" aria-hidden /> : undefined}
          >
            {lab ? 'Save' : 'Add laboratory'}
          </Button>
          <Button onClick={guard.requestClose}>Cancel</Button>
        </DrawerFooter>
      </form>
      {guard.dialogs}
    </OverlayDrawer>
  );
}
