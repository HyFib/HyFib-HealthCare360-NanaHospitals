import {
  Button,
  Caption1,
  Checkbox,
  makeStyles,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  Spinner,
  tokens,
} from '@fluentui/react-components';
import { KeyRegular, PeopleCheckmarkRegular } from '@fluentui/react-icons';
import { zodResolver } from '@hookform/resolvers/zod';
import {
  applyServerErrors,
  EmptyState,
  ErrorState,
  FormGrid,
  LoadingState,
  Page,
  PageHeader,
  Section,
  StatusBadge,
  TextField,
  useLeaveGuard,
  useNotify,
} from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute } from '@tanstack/react-router';
import { useState } from 'react';
import { Controller, useForm } from 'react-hook-form';
import { z } from 'zod';
import { RequirePermission } from '@/modules/hr/shared/NoAccess';
import { Stack } from '@/modules/hr/shared/layout';
import { dpdpApi, dpdpKeys, dpdpQueries, type ConsentManagerCreated } from '@/modules/privacy/dpdpApi';
import { PlainTable } from '@/modules/privacy/PlainTable';

/** Consent managers registered with the Data Protection Board that the hospital accepts consent from (DPDP Act s.6(7)). */
export const CONSENT_MANAGERS_PERMISSION = 'privacy.consent_managers.manage';

export const Route = createFileRoute('/_app/privacy/consent-managers')({
  component: () => (
    <RequirePermission permission={CONSENT_MANAGERS_PERMISSION} what="the consent managers">
      <ConsentManagersPage />
    </RequirePermission>
  ),
});

const useStyles = makeStyles({
  muted: { color: tokens.colorNeutralForeground3 },
  footer: { display: 'flex', justifyContent: 'flex-end', gap: '12px' },
  secret: { fontFamily: tokens.fontFamilyMonospace, overflowWrap: 'anywhere' },
});

const schema = z
  .object({
    code: z
      .string()
      .trim()
      .regex(/^[a-z0-9-]{2,12}$/, 'Use 2 to 12 lower-case letters, digits or dashes.'),
    name: z.string().trim().min(1, 'Enter its name.').max(200),
    registrationNumber: z.string().trim().min(1, 'Enter its registration number with the Board.').max(64),
    baseUrl: z.string().trim().max(500),
    isSimulator: z.boolean(),
  })
  .superRefine((v, ctx) => {
    if (!v.isSimulator && !/^https:\/\/\S+$/.test(v.baseUrl)) {
      ctx.addIssue({
        code: 'custom',
        path: ['baseUrl'],
        message: 'Enter the https:// address withdrawals are sent to.',
      });
    }
  });

type Values = z.infer<typeof schema>;

function ConsentManagersPage() {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const managers = useQuery(dpdpQueries.managers());
  const [created, setCreated] = useState<ConsentManagerCreated | null>(null);
  const form = useForm<Values>({
    resolver: zodResolver(schema),
    defaultValues: { code: '', name: '', registrationNumber: '', baseUrl: '', isSimulator: false },
  });
  const save = useMutation({
    mutationFn: (v: Values) => dpdpApi.createManager({ ...v, baseUrl: v.isSimulator ? null : v.baseUrl || null }),
    onSuccess: async (result) => {
      setCreated(result);
      form.reset();
      await queryClient.invalidateQueries({ queryKey: dpdpKeys.managers });
    },
    onError: (error) => {
      if (!applyServerErrors(error, form.setError)) {
        notify.error(error, 'Could not register the consent manager');
      }
    },
  });
  const deactivate = useMutation({
    mutationFn: (id: string) => dpdpApi.deactivateManager(id),
    onSuccess: async () => {
      notify.success('No longer accepted', 'Its consents stay as they are, and patients can still withdraw them.');
      await queryClient.invalidateQueries({ queryKey: dpdpKeys.managers });
    },
    onError: (error) => notify.error(error, 'Could not change it'),
  });
  useLeaveGuard({ dirty: form.formState.isDirty, busy: save.isPending });

  return (
    <Page maxWidth={1100}>
      <PageHeader
        title="Consent managers"
        subtitle="Platforms registered with the Data Protection Board through which patients give and withdraw consent. Their consents are recorded as the patient's own, and withdrawals go both ways."
        icon={<PeopleCheckmarkRegular />}
      />
      <Stack>
        {created && (
          <MessageBar intent="warning">
            <MessageBarBody>
              <MessageBarTitle>Signing secret for {created.code}, shown once</MessageBarTitle>
              <span className={styles.secret}>{created.secret}</span>
              <br />
              Give it to the consent manager over a channel you both trust. It signs every event it sends to{' '}
              <span className={styles.secret}>/api/v1/consent-managers/{created.code}/events</span>.
            </MessageBarBody>
          </MessageBar>
        )}
        <Section title="Registered">
          {managers.error ? (
            <ErrorState error={managers.error} onRetry={() => void managers.refetch()} />
          ) : !managers.data ? (
            <LoadingState />
          ) : managers.data.length === 0 ? (
            <EmptyState
              title="No consent managers yet"
              description="Register the ones the hospital accepts consent from."
            />
          ) : (
            <PlainTable
              ariaLabel="Consent managers"
              rows={managers.data}
              getRowId={(row) => row.id}
              columns={[
                { key: 'name', header: 'Name', render: (r) => `${r.name}${r.isSimulator ? ' (simulator)' : ''}` },
                { key: 'code', header: 'Code', render: (r) => r.code, width: 110 },
                { key: 'reg', header: 'Board registration', render: (r) => r.registrationNumber, width: 170 },
                {
                  key: 'active',
                  header: 'Consents in force',
                  render: (r) => r.activeArtefacts,
                  width: 140,
                  align: 'end',
                },
                {
                  key: 'status',
                  header: 'Status',
                  width: 120,
                  render: (r) => (
                    <StatusBadge
                      status={r.isActive ? 'Accepted' : 'Stopped'}
                      tone={r.isActive ? 'success' : 'subtle'}
                    />
                  ),
                },
                {
                  key: 'action',
                  header: 'Action',
                  width: 170,
                  render: (r) =>
                    r.isActive ? (
                      <Button size="small" disabled={deactivate.isPending} onClick={() => deactivate.mutate(r.id)}>
                        Stop accepting
                      </Button>
                    ) : null,
                },
              ]}
            />
          )}
        </Section>
        <Section title="Register a consent manager" description="HC360 makes the signing secret and shows it once.">
          <form onSubmit={form.handleSubmit((v) => save.mutate(v))} noValidate aria-label="Register a consent manager">
            <Stack>
              <FormGrid columns={2} minColumnWidth={220}>
                <TextField control={form.control} name="name" label="Name" required maxLength={200} />
                <TextField
                  control={form.control}
                  name="registrationNumber"
                  label="Registration number with the Board"
                  required
                  maxLength={64}
                />
                <TextField
                  control={form.control}
                  name="code"
                  label="Code"
                  required
                  maxLength={12}
                  hint="Short, in the address it calls, for example cm-one."
                />
                <TextField
                  control={form.control}
                  name="baseUrl"
                  label="Address for withdrawals"
                  maxLength={500}
                  hint="Where withdrawals made at the hospital are sent (https://)."
                />
              </FormGrid>
              <Controller
                control={form.control}
                name="isSimulator"
                render={({ field }) => (
                  <Checkbox
                    checked={field.value}
                    onChange={(_, data) => field.onChange(data.checked === true)}
                    label="The built-in simulator (training and demonstration servers only)"
                  />
                )}
              />
              <Caption1 className={styles.muted}>
                Patients see each consent and can withdraw it in the portal, under Your privacy › Consents.
              </Caption1>
              <div className={styles.footer}>
                <Button
                  type="submit"
                  appearance="primary"
                  icon={save.isPending ? <Spinner size="tiny" aria-hidden /> : <KeyRegular />}
                  disabled={save.isPending}
                >
                  Register and make the secret
                </Button>
              </div>
            </Stack>
          </form>
        </Section>
      </Stack>
    </Page>
  );
}
