import { Button, Caption1, makeStyles, Spinner, Text, tokens } from '@fluentui/react-components';
import { SaveRegular, SettingsRegular } from '@fluentui/react-icons';
import { zodResolver } from '@hookform/resolvers/zod';
import { formatPhone, normalizePhone } from '@hc360/core';
import {
  applyServerErrors,
  ErrorState,
  FormGrid,
  LoadingState,
  NumberField,
  Page,
  PageHeader,
  Section,
  TextAreaField,
  TextField,
  useLeaveGuard,
  useNotify,
} from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute } from '@tanstack/react-router';
import { useForm } from 'react-hook-form';
import { z } from 'zod';
import { Detail, DetailGrid } from '@/modules/hr/shared/DetailGrid';
import { MainAside, Stack } from '@/modules/hr/shared/layout';
import { RequirePermission } from '@/modules/hr/shared/NoAccess';
import { privacyApi, privacyKeys, privacyQueries, type PrivacySettingsView } from '@/modules/privacy/api';
import { DEFAULT_OFFICER_TITLE, MAX_RESPONSE_DAYS, PRIVACY_PERMISSIONS as P } from '@/modules/privacy/labels';

export const Route = createFileRoute('/_app/privacy/settings')({
  component: () => (
    <RequirePermission permission={P.settingsManage} what="the privacy settings">
      <SettingsPage />
    </RequirePermission>
  ),
});

const EMAIL = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;

const schema = z
  .object({
    title: z.string().trim().max(100),
    name: z.string().trim().max(200),
    email: z.string().trim().max(200),
    phone: z.string().trim().max(20),
    address: z.string().trim().max(500),
    responseDays: z.number({ error: 'Enter the number of days.' }).nullable(),
  })
  .superRefine((v, ctx) => {
    if (v.email && !EMAIL.test(v.email)) {
      ctx.addIssue({ code: 'custom', path: ['email'], message: 'Enter a valid e-mail address.' });
    }
    if (v.phone && normalizePhone(v.phone) === null) {
      ctx.addIssue({ code: 'custom', path: ['phone'], message: 'Enter a valid phone number.' });
    }
    if (v.responseDays === null) {
      ctx.addIssue({ code: 'custom', path: ['responseDays'], message: 'Enter the number of days.' });
    } else if (!Number.isInteger(v.responseDays) || v.responseDays < 1 || v.responseDays > MAX_RESPONSE_DAYS) {
      ctx.addIssue({
        code: 'custom',
        path: ['responseDays'],
        message: `Choose 1 to ${MAX_RESPONSE_DAYS} days: the DPDP Rules 2025 allow at most ${MAX_RESPONSE_DAYS}.`,
      });
    }
  });

type Values = z.infer<typeof schema>;

const useStyles = makeStyles({
  muted: { color: tokens.colorNeutralForeground3 },
  footer: { display: 'flex', justifyContent: 'flex-end', gap: '12px', alignItems: 'center', flexWrap: 'wrap' },
  published: { display: 'flex', flexDirection: 'column', gap: '12px' },
});

function toValues(view: PrivacySettingsView): Values {
  const c = view.settings.contact;
  return {
    title: c.title || DEFAULT_OFFICER_TITLE,
    name: c.name ?? '',
    email: c.email ?? '',
    phone: c.phone ? formatPhone(c.phone) : '',
    address: c.address ?? '',
    responseDays: view.settings.responseDays,
  };
}

function SettingsPage() {
  const settings = useQuery({ ...privacyQueries.settings(), refetchOnWindowFocus: false });
  return (
    <Page maxWidth={1100}>
      <PageHeader
        title="Privacy settings"
        subtitle="Whom patients contact about their personal data, and how soon their requests are answered. Shown in the privacy notice and in every reply."
        icon={<SettingsRegular />}
      />
      {settings.data ? (
        <SettingsForm view={settings.data} />
      ) : settings.error ? (
        <ErrorState error={settings.error} onRetry={() => void settings.refetch()} />
      ) : (
        <LoadingState />
      )}
    </Page>
  );
}

function SettingsForm({ view }: { view: PrivacySettingsView }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const form = useForm<Values>({ resolver: zodResolver(schema), defaultValues: toValues(view) });
  const save = useMutation({
    mutationFn: (v: Values) =>
      privacyApi.saveSettings({
        title: v.title.trim() || null,
        name: v.name.trim() || null,
        email: v.email.trim() || null,
        phone: v.phone.trim() ? (normalizePhone(v.phone) ?? v.phone.trim()) : null,
        address: v.address.trim() || null,
        responseDays: v.responseDays ?? 30,
      }),
    onSuccess: (_, v) => {
      notify.success('Privacy settings saved', 'New requests are given the new reply-by period.');
      form.reset(v);
      void queryClient.invalidateQueries({ queryKey: privacyKeys.settings });
    },
    onError: (error) => {
      if (!applyServerErrors(error, form.setError)) {
        notify.error(error, 'Could not save the settings');
      }
    },
  });
  const published = view.published;

  // Leaving the screen or closing the window with unsaved changes asks first (SRS UX-08).
  useLeaveGuard({ dirty: form.formState.isDirty, busy: save.isPending });

  return (
    <MainAside>
      <form onSubmit={form.handleSubmit((v) => save.mutate(v))} noValidate>
        <Stack>
          <Section
            title="Grievance officer"
            description="The person patients contact about their personal data: the grievance officer, or the Data Protection Officer where the hospital has one."
          >
            <Stack>
              <FormGrid columns={2} minColumnWidth={220}>
                <TextField
                  control={form.control}
                  name="title"
                  label="Title"
                  autoFocus
                  maxLength={100}
                  hint={`For example ${DEFAULT_OFFICER_TITLE} or Data Protection Officer.`}
                />
                <TextField control={form.control} name="name" label="Name" maxLength={200} autoComplete="off" />
                <TextField
                  control={form.control}
                  name="email"
                  label="E-mail address"
                  type="email"
                  maxLength={200}
                  autoComplete="off"
                />
                <TextField
                  control={form.control}
                  name="phone"
                  label="Phone number"
                  type="tel"
                  maxLength={20}
                  autoComplete="off"
                />
              </FormGrid>
              <TextAreaField control={form.control} name="address" label="Postal address" rows={3} maxLength={500} />
            </Stack>
          </Section>
          <Section title="Response period">
            <FormGrid columns={2} minColumnWidth={220}>
              <NumberField
                control={form.control}
                name="responseDays"
                label="Answer requests within (days)"
                required
                min={1}
                max={MAX_RESPONSE_DAYS}
                hint={`The DPDP Rules 2025 allow at most ${MAX_RESPONSE_DAYS} days; 30 is typical. It sets the reply-by date of requests received from now on.`}
              />
            </FormGrid>
          </Section>
          <div className={styles.footer}>
            {!form.formState.isDirty && <Caption1 className={styles.muted}>No changes to save.</Caption1>}
            <Button
              type="submit"
              appearance="primary"
              icon={save.isPending ? <Spinner size="tiny" /> : <SaveRegular />}
              disabled={save.isPending || !form.formState.isDirty}
            >
              Save
            </Button>
          </div>
        </Stack>
      </form>
      <Section title="What patients see" description="In the privacy notice, the patient portal and every reply.">
        <div className={styles.published}>
          <DetailGrid minColumnWidth={160}>
            <Detail label="Contact" wide>
              {published.name ? `${published.name}, ${published.title}` : published.title}
            </Detail>
            <Detail label="E-mail" wide>
              {published.email}
            </Detail>
            <Detail label="Phone" wide>
              {published.phone ? formatPhone(published.phone) : null}
            </Detail>
            <Detail label="Address" wide>
              {published.address}
            </Detail>
          </DetailGrid>
          <Text>
            Requests are answered within <strong>{view.settings.responseDays} days</strong>.
          </Text>
          <Caption1 className={styles.muted}>
            Where a detail above is left empty, the hospital’s own e-mail, phone number or address is shown instead.
          </Caption1>
        </div>
      </Section>
    </MainAside>
  );
}
