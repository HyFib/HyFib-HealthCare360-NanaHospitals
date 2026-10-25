import { Button, Caption1, makeStyles, Spinner, tokens } from '@fluentui/react-components';
import { SaveRegular } from '@fluentui/react-icons';
import { zodResolver } from '@hookform/resolvers/zod';
import { formatPhone, isApiError, normalizePhone } from '@hc360/core';
import {
  applyServerErrors,
  ErrorState,
  FormGrid,
  LoadingState,
  Section,
  TextAreaField,
  TextField,
  useLeaveGuard,
  useNotify,
} from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useForm } from 'react-hook-form';
import { z } from 'zod';
import { privacyApi, privacyKeys, privacyQueries, type CertInContact } from './api';

const EMAIL = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;

const schema = z
  .object({
    name: z.string().trim().min(1, 'Name the person CERT-In should contact.').max(200),
    designation: z.string().trim().max(100),
    email: z.string().trim().min(1, 'CERT-In writes to this address: enter it.').max(200),
    phone: z.string().trim().min(1, 'Enter a phone number CERT-In can call at any hour.').max(20),
    address: z.string().trim().max(500),
  })
  .superRefine((v, ctx) => {
    if (v.email && !EMAIL.test(v.email)) {
      ctx.addIssue({ code: 'custom', path: ['email'], message: 'Enter a valid e-mail address.' });
    }
    if (v.phone && normalizePhone(v.phone) === null) {
      ctx.addIssue({ code: 'custom', path: ['phone'], message: 'Enter a valid phone number.' });
    }
  });

type Values = z.infer<typeof schema>;

const useStyles = makeStyles({
  footer: { display: 'flex', justifyContent: 'flex-end', gap: '12px', alignItems: 'center', flexWrap: 'wrap' },
  muted: { color: tokens.colorNeutralForeground3 },
});

function toValues(c: CertInContact): Values {
  return {
    name: c.name ?? '',
    designation: c.designation ?? '',
    email: c.email ?? '',
    phone: c.phone ? formatPhone(c.phone) : '',
    address: c.address ?? '',
  };
}

/**
 * The point of contact designated to CERT-In (CERT-In Directions of 28 April 2022, direction (iii)). System health
 * warns until one is named.
 */
export function CertInContactSection() {
  const contact = useQuery({ ...privacyQueries.certInContact(), refetchOnWindowFocus: false });
  return (
    <Section
      title="CERT-In point of contact"
      description="The person CERT-In writes to about a cyber security incident, and who reports one to CERT-In within six hours. The Directions of 28 April 2022 require the hospital to designate one."
    >
      {contact.data ? (
        <CertInContactForm contact={contact.data} />
      ) : contact.error ? (
        <ErrorState error={contact.error} onRetry={() => void contact.refetch()} />
      ) : (
        <LoadingState />
      )}
    </Section>
  );
}

function CertInContactForm({ contact }: { contact: CertInContact }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const form = useForm<Values>({ resolver: zodResolver(schema), defaultValues: toValues(contact) });

  const save = useMutation({
    mutationFn: (v: Values) =>
      privacyApi.saveCertInContact({
        name: v.name.trim(),
        designation: v.designation.trim() || null,
        email: v.email.trim(),
        phone: normalizePhone(v.phone) ?? v.phone.trim(),
        address: v.address.trim() || null,
        version: contact.version,
      }),
    onSuccess: async () => {
      notify.success('CERT-In point of contact saved');
      const fresh = await queryClient.fetchQuery({ ...privacyQueries.certInContact(), staleTime: 0 });
      form.reset(toValues(fresh));
    },
    onError: (error) => {
      if (!applyServerErrors(error, form.setError)) {
        notify.error(error, 'Could not save the CERT-In point of contact');
        if (isApiError(error) && error.code === 'concurrency') {
          void queryClient.invalidateQueries({ queryKey: privacyKeys.certInContact });
        }
      }
    },
  });

  useLeaveGuard({ dirty: form.formState.isDirty, busy: save.isPending });

  return (
    <form onSubmit={form.handleSubmit((v) => save.mutate(v))} noValidate>
      <FormGrid columns={2} minColumnWidth={220}>
        <TextField control={form.control} name="name" label="Name" required maxLength={200} autoComplete="off" />
        <TextField
          control={form.control}
          name="designation"
          label="Designation"
          maxLength={100}
          hint="For example Chief Information Security Officer, or IT Head."
        />
        <TextField control={form.control} name="email" label="E-mail address" type="email" required maxLength={200} />
        <TextField control={form.control} name="phone" label="Phone number" type="tel" required maxLength={20} />
      </FormGrid>
      <TextAreaField control={form.control} name="address" label="Postal address" rows={2} maxLength={500} />
      <div className={styles.footer}>
        {!form.formState.isDirty && <Caption1 className={styles.muted}>No changes to save.</Caption1>}
        <Button
          type="submit"
          appearance="primary"
          icon={save.isPending ? <Spinner size="tiny" aria-hidden /> : <SaveRegular />}
          disabled={save.isPending || !form.formState.isDirty}
        >
          Save
        </Button>
      </div>
    </form>
  );
}
