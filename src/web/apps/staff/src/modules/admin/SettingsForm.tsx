import {
  Badge,
  Button,
  Caption1,
  Field,
  makeStyles,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  Radio,
  RadioGroup,
  Spinner,
  Subtitle2,
  Textarea,
  tokens,
} from '@fluentui/react-components';
import { LockClosedRegular, SaveRegular } from '@fluentui/react-icons';
import { zodResolver } from '@hookform/resolvers/zod';
import { useHotkey, useSession } from '@hc360/core';
import {
  applyServerErrors,
  ErrorState,
  FormGrid,
  FullRow,
  LoadingState,
  NumberField,
  SelectField,
  SwitchField,
  TextAreaField,
  TextField,
  useLeaveGuard,
  useNotify,
} from '@hc360/ui';
import { queryOptions, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useMemo, useState } from 'react';
import { Controller, useForm, useWatch, type Control } from 'react-hook-form';
import { z } from 'zod';
import { api } from '@/lib/api';
import { usePermission } from '@/lib/permissions';
import { settingsApi, type SaveSettingBody, type SettingChangeCheck, type SettingDto } from './api';
import { PendingGrantNotice } from './PendingGrantNotice';
import {
  buildSecretChanges,
  buildSettingChanges,
  defaultScope,
  fieldId,
  isFieldVisible,
  replaceFlagId,
  scopedSettings,
  secretFieldId,
  specSecretKeys,
  toFormValues,
  validateSettings,
  type FormValues,
  type Scope,
  type SecretStatus,
  type SettingField,
  type SettingsSpec,
} from './settingsForms';

const useStyles = makeStyles({
  form: { display: 'flex', flexDirection: 'column', gap: '16px' },
  group: { display: 'flex', flexDirection: 'column', gap: '10px' },
  groupTitle: { marginTop: '4px' },
  credentials: {
    display: 'flex',
    flexDirection: 'column',
    gap: '10px',
    padding: '12px',
    borderRadius: tokens.borderRadiusMedium,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    backgroundColor: tokens.colorNeutralBackground2,
  },
  credentialsHeader: { display: 'flex', alignItems: 'center', gap: '8px', flexWrap: 'wrap' },
  muted: { color: tokens.colorNeutralForeground3 },
  code: { fontFamily: tokens.fontFamilyMonospace },
  actions: { display: 'flex', gap: '8px', alignItems: 'center', flexWrap: 'wrap' },
});

const settingsByPrefix = (prefix: string) =>
  queryOptions({
    queryKey: ['admin', 'settings', 'prefix', prefix],
    queryFn: ({ signal }) => api.get<SettingDto[]>('/api/v1/admin/settings', { query: { prefix }, signal }),
    // The form restarts from the server after every save; background refetches must not wipe what is being typed.
    staleTime: Infinity,
    refetchOnWindowFocus: false,
  });

const WIDE_KINDS = new Set(['textarea', 'map', 'list', 'json', 'switch']);

const formSchema = (spec: SettingsSpec) =>
  z.record(z.string(), z.union([z.string(), z.number(), z.boolean(), z.null()])).superRefine((values, ctx) => {
    for (const [path, message] of Object.entries(validateSettings(spec, values))) {
      ctx.addIssue({ code: 'custom', path: [path], message });
    }
  });

/**
 * Edits one group of platform settings (an integration, or AI) as a form. Needs `admin.settings.manage`, which the
 * settings API requires. Credentials are write-only: the form shows whether they are set and can replace them. A change
 * that waits for a second administrator (an integration's provider, address or credentials; AI turned on) is asked for
 * with a reason, which the form asks for only once the server says so; the changes that wait show above the form.
 */
export function SettingsForm({ spec }: { spec: SettingsSpec }) {
  const canEdit = usePermission('admin.settings.manage');
  const facilityId = useSession((s) => s.user?.facilityId ?? null);
  const facilityName = useSession((s) => s.user?.facilityName ?? null);
  const rows = useQuery({ ...settingsByPrefix(spec.prefix), enabled: canEdit });
  // The scope the administrator picked stays picked after a save (the form below restarts from the server).
  const [chosenScope, setChosenScope] = useState<Scope | null>(null);

  if (!canEdit) {
    return (
      <MessageBar intent="info" layout="multiline">
        <MessageBarBody>
          Changing these settings needs the “Change system settings” permission (admin.settings.manage). Ask an
          administrator who has it.
        </MessageBarBody>
      </MessageBar>
    );
  }
  if (rows.isLoading) {
    return <LoadingState label="Loading settings…" />;
  }
  if (rows.error || !rows.data) {
    return <ErrorState error={rows.error} onRetry={() => void rows.refetch()} title="Could not load the settings" />;
  }
  const scope = spec.globalOnly || !facilityId ? 'global' : (chosenScope ?? defaultScope(rows.data, spec, facilityId));
  return (
    <ScopedSettings
      spec={spec}
      rows={rows.data}
      version={rows.dataUpdatedAt}
      scope={scope}
      onScopeChange={setChosenScope}
      facilityId={facilityId}
      facilityName={facilityName}
    />
  );
}

function ScopedSettings({
  spec,
  rows,
  version,
  scope,
  onScopeChange,
  facilityId,
  facilityName,
}: {
  spec: SettingsSpec;
  rows: SettingDto[];
  version: number;
  scope: Scope;
  onScopeChange: (scope: Scope) => void;
  facilityId: string | null;
  facilityName: string | null;
}) {
  return (
    <>
      {spec.globalOnly || !facilityId ? (
        <Caption1>These settings apply to the whole hospital.</Caption1>
      ) : (
        <Field
          label="Applies to"
          hint={
            spec.backgroundUsesGlobal
              ? 'Reminders, reports and other messages sent in the background always use the settings for all facilities.'
              : undefined
          }
        >
          <RadioGroup
            layout="horizontal"
            value={scope}
            onChange={(_, data) => onScopeChange(data.value === 'facility' ? 'facility' : 'global')}
          >
            <Radio value="global" label="All facilities" />
            <Radio
              value="facility"
              label={facilityName ? `This facility only (${facilityName})` : 'This facility only'}
            />
          </RadioGroup>
        </Field>
      )}
      {/* Restart the form for another scope, and from the server's values after every save. */}
      <SettingsFormBody key={`${scope}:${version}`} spec={spec} rows={rows} scope={scope} facilityId={facilityId} />
    </>
  );
}

/** The changes a form's values make, credentials only by which of them are replaced: what a check was made for. */
function standingOf(spec: SettingsSpec, values: FormValues, stored: Record<string, unknown>): string {
  return JSON.stringify([
    buildSettingChanges(spec, values, stored),
    buildSecretChanges(spec, values).map((secret) => [secret.key, Object.keys(secret.value).sort()]),
  ]);
}

/** Splits the fields into titled groups (a field with `group` starts a new one). */
function groupsOf(fields: readonly SettingField[]): { title?: string; fields: SettingField[] }[] {
  const groups: { title?: string; fields: SettingField[] }[] = [];
  for (const field of fields) {
    const current = groups.at(-1);
    if (!current || field.group) {
      groups.push({ title: field.group, fields: [field] });
    } else {
      current.fields.push(field);
    }
  }
  return groups;
}

function SettingsFormBody({
  spec,
  rows,
  scope,
  facilityId,
}: {
  spec: SettingsSpec;
  rows: SettingDto[];
  scope: Scope;
  facilityId: string | null;
}) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const scoped = useMemo(() => scopedSettings(rows, spec, scope, facilityId), [rows, spec, scope, facilityId]);
  const defaults = useMemo(() => toFormValues(spec, scoped.stored), [spec, scoped]);
  const schema = useMemo(() => formSchema(spec), [spec]);
  const form = useForm<FormValues>({ resolver: zodResolver(schema), defaultValues: defaults });
  const watched = useWatch({ control: form.control });
  const values: FormValues = { ...defaults, ...(watched as FormValues) };
  const target = scope === 'facility' ? facilityId : null;

  const [reason, setReason] = useState('');
  // The changes the server said wait for a second administrator, for the changes as they stand: any edit asks again.
  const [checked, setChecked] = useState<{ standing: string; waiting: SettingChangeCheck[] } | null>(null);
  const waiting = checked?.standing === standingOf(spec, values, scoped.stored) ? checked.waiting : null;
  const pending = rows.flatMap((row) =>
    row.pending && (row.facilityId ?? null) === target ? [{ key: row.key, change: row.pending }] : [],
  );

  const save = useMutation({
    mutationFn: async (submitted: FormValues) => {
      const changes = buildSettingChanges(spec, submitted, scoped.stored);
      const secrets = buildSecretChanges(spec, submitted);
      const stored = (key: string) => rows.find((row) => row.key === key && (row.facilityId ?? null) === target);
      const description = (key: string) => (stored(key) ? null : `${spec.title} (set in Administration)`);
      const body = (key: string, value: unknown, secret: boolean): SaveSettingBody => ({
        value,
        facilityId: target,
        secret,
        description: description(key),
        version: stored(key)?.version,
      });
      const saves = [
        ...changes.map((change) => ({ key: change.key, body: body(change.key, change.value, false) })),
        ...secrets.map((secret) => ({ key: secret.key, body: body(secret.key, secret.value, true) })),
      ];

      // First, whether any of it waits for a second administrator: then a reason is asked for before anything is sent.
      if (!waiting) {
        const checks: SettingChangeCheck[] = [];
        for (const { key, body: asked } of saves) {
          const check = await settingsApi.check(key, asked);
          if (check.waits) {
            checks.push(check);
          }
        }
        if (checks.length > 0) {
          return { kind: 'check' as const, waiting: checks };
        }
      }

      let asked = 0;
      for (const { key, body: change } of saves) {
        if (await settingsApi.save(key, { ...change, reason: waiting ? reason.trim() : null })) {
          asked += 1;
        }
      }
      return { kind: 'sent' as const, saved: saves.length - asked, asked };
    },
    onSuccess: (outcome, submitted) => {
      if (outcome.kind === 'check') {
        setChecked({ standing: standingOf(spec, submitted, scoped.stored), waiting: outcome.waiting });
        return;
      }
      // What the form shows is saved, or asked for, now: leaving does not ask (SRS UX-08).
      form.reset(submitted);
      setChecked(null);
      setReason('');
      if (outcome.saved + outcome.asked === 0) {
        notify.info('Nothing to save', 'The settings are already like this.');
        return;
      }
      if (outcome.asked > 0) {
        notify.success(
          'Asked for approval',
          outcome.saved > 0
            ? `${outcome.saved} saved; ${outcome.asked} wait for another administrator who may change settings, and stay as they are until then.`
            : 'Another administrator who may change settings decides; until then the settings stay as they are.',
        );
      } else {
        notify.success(`${spec.title} settings saved`, 'Changes apply within a minute.');
      }
      void queryClient.invalidateQueries({ queryKey: ['admin', 'settings'] });
      void queryClient.invalidateQueries({ queryKey: ['admin', 'integrations'] });
      void queryClient.invalidateQueries({ queryKey: ['ai'] });
    },
    onError: (error) => {
      if (!applyServerErrors(error, form.setError)) {
        notify.error(error, 'Could not save the settings');
      }
    },
  });

  const submit = form.handleSubmit((submitted) => save.mutate(submitted));
  useHotkey('mod+s', () => void submit(), { allowInInputs: true });

  const secretKeys = specSecretKeys(spec);
  const replacingAny = secretKeys.some((key) => values[replaceFlagId(key)] === true);
  const noCredentials = secretKeys.length > 0 && secretKeys.every((key) => scoped.secretStatus[key] === 'not-set');

  // Leaving the screen or closing the window with unsaved changes asks first (SRS UX-08).
  useLeaveGuard({ dirty: form.formState.isDirty, busy: save.isPending });

  return (
    <form className={styles.form} onSubmit={(event) => void submit(event)} noValidate>
      {pending.map(({ key, change }) => (
        <PendingGrantNotice
          key={key}
          approvalId={change.approvalId}
          what={change.summary}
          requestedBy={change.requestedBy}
          requestedAt={change.requestedAt}
          decides="Another administrator who may change settings decides it; until then the setting stays as it is."
        />
      ))}
      {scoped.inherited.length > 0 && (
        <MessageBar intent="info">
          <MessageBarBody>
            This facility uses the settings for all facilities. Saving here gives it its own settings.
          </MessageBarBody>
        </MessageBar>
      )}

      {groupsOf(spec.fields).map((group, index) => {
        const visible = group.fields.filter((field) => isFieldVisible(field, values));
        if (visible.length === 0) {
          return null;
        }
        return (
          <div key={group.title ?? `group-${index}`} className={styles.group}>
            {group.title && (
              <Subtitle2 as="h3" className={styles.groupTitle}>
                {group.title}
              </Subtitle2>
            )}
            <FormGrid columns={2}>
              {visible.map((field) =>
                WIDE_KINDS.has(field.kind) ? (
                  <FullRow key={fieldId(field)}>
                    <SettingInput field={field} control={form.control} />
                  </FullRow>
                ) : (
                  <SettingInput key={fieldId(field)} field={field} control={form.control} />
                ),
              )}
            </FormGrid>
          </div>
        );
      })}

      {secretKeys.map((key) => (
        <CredentialsGroup
          key={key}
          title={
            secretKeys.length > 1
              ? (spec.secrets.find((secret) => secret.key === key)?.label ?? 'Credentials')
              : 'Credentials'
          }
          spec={spec}
          secretKey={key}
          status={scoped.secretStatus[key] ?? 'not-set'}
          replacing={values[replaceFlagId(key)] === true}
          control={form.control}
        />
      ))}
      {noCredentials && !replacingAny && (
        <Caption1 className={styles.muted}>
          No credentials are stored yet. Most providers need them; the simulator does not.
        </Caption1>
      )}

      {waiting && (
        <>
          <MessageBar intent="warning" layout="multiline">
            <MessageBarBody>
              <MessageBarTitle>Waits for a second administrator</MessageBarTitle>
              {waiting.map((check) => `${check.setting}: ${check.from} → ${check.to}`).join('; ')}. Another
              administrator who may change settings decides; until then the settings stay as they are.
            </MessageBarBody>
          </MessageBar>
          <Field label="Reason, for the administrator who decides" required>
            <Textarea value={reason} onChange={(_, data) => setReason(data.value)} rows={2} maxLength={500} />
          </Field>
        </>
      )}

      <div className={styles.actions}>
        <Button
          type="submit"
          appearance="primary"
          icon={save.isPending ? <Spinner size="tiny" aria-hidden /> : <SaveRegular />}
          disabled={save.isPending || !form.formState.isDirty || (waiting !== null && !reason.trim())}
        >
          {waiting ? 'Ask for approval' : 'Save'}
        </Button>
        <Button onClick={() => form.reset(defaults)} disabled={save.isPending || !form.formState.isDirty}>
          Undo changes
        </Button>
        <Caption1 className={styles.muted}>Ctrl+S saves. Changes apply within a minute.</Caption1>
      </div>
    </form>
  );
}

function SettingInput({ field, control }: { field: SettingField; control: Control<FormValues> }) {
  const styles = useStyles();
  const name = fieldId(field);
  const hint =
    field.kind === 'number' && field.defaultNumber !== undefined
      ? [field.hint, `Leave empty for the default (${field.defaultNumber.toLocaleString('en-IN')}).`]
          .filter(Boolean)
          .join(' ')
      : field.hint;

  switch (field.kind) {
    case 'switch':
      return <SwitchField control={control} name={name} label={field.label} hint={hint} />;
    case 'number':
      return (
        <NumberField control={control} name={name} label={field.label} hint={hint} min={field.min} max={field.max} />
      );
    case 'select':
      return (
        <SelectField
          control={control}
          name={name}
          label={field.label}
          hint={hint}
          options={(field.options ?? []).map((option) => ({ ...option }))}
          placeholder={field.defaultValue === undefined ? 'Choose…' : undefined}
        />
      );
    case 'textarea':
      return (
        <TextAreaField
          control={control}
          name={name}
          label={field.label}
          hint={hint}
          rows={4}
          placeholder={field.placeholder}
        />
      );
    case 'map':
    case 'list':
    case 'json':
      return (
        <Controller
          control={control}
          name={name}
          render={({ field: input, fieldState }) => (
            <Field
              label={field.label}
              hint={hint}
              validationMessage={fieldState.error?.message}
              validationState={fieldState.error ? 'error' : 'none'}
            >
              <Textarea
                name={input.name}
                ref={input.ref}
                onBlur={input.onBlur}
                value={typeof input.value === 'string' ? input.value : ''}
                onChange={(_, data) => input.onChange(data.value)}
                rows={field.kind === 'json' ? 6 : 3}
                resize="vertical"
                spellCheck={false}
                textarea={{ className: styles.code }}
                placeholder={field.kind === 'map' ? 'name = value' : field.kind === 'list' ? 'One per line' : undefined}
              />
            </Field>
          )}
        />
      );
    default:
      return (
        <TextField
          control={control}
          name={name}
          label={field.label}
          hint={hint}
          placeholder={field.placeholder}
          maxLength={field.maxLength}
          type={field.kind === 'email' ? 'email' : field.kind === 'url' ? 'url' : 'text'}
          autoComplete="off"
        />
      );
  }
}

const STATUS_TEXT: Record<SecretStatus, string> = {
  set: 'Set',
  inherited: 'Set for all facilities',
  'not-set': 'Not set',
};

function CredentialsGroup({
  title,
  spec,
  secretKey,
  status,
  replacing,
  control,
}: {
  title: string;
  spec: SettingsSpec;
  secretKey: string;
  status: SecretStatus;
  replacing: boolean;
  control: Control<FormValues>;
}) {
  const styles = useStyles();
  const secrets = spec.secrets.filter((secret) => secret.key === secretKey);
  return (
    <section className={styles.credentials} aria-label={title}>
      <div className={styles.credentialsHeader}>
        <Subtitle2 as="h3">{title}</Subtitle2>
        <Badge appearance="tint" color={status === 'not-set' ? 'informative' : 'success'} icon={<LockClosedRegular />}>
          {STATUS_TEXT[status]}
        </Badge>
      </div>
      <Caption1 className={styles.muted}>Stored encrypted and never shown again, not even to administrators.</Caption1>
      <SwitchField
        control={control}
        name={replaceFlagId(secretKey)}
        label={status === 'not-set' ? 'Enter credentials' : 'Replace credentials'}
      />
      {replacing && (
        <>
          {status !== 'not-set' && (
            <MessageBar intent="warning" layout="multiline">
              <MessageBarBody>
                {status === 'set'
                  ? 'Saving replaces everything stored here. Enter every credential you use; any you leave empty is removed.'
                  : 'This facility will use what you enter here instead of the credentials for all facilities.'}
              </MessageBarBody>
            </MessageBar>
          )}
          <FormGrid columns={2}>
            {secrets.map((secret) => (
              <TextField
                key={secretFieldId(secret)}
                control={control}
                name={secretFieldId(secret)}
                label={secret.label}
                hint={secret.hint}
                type="password"
                autoComplete="new-password"
              />
            ))}
          </FormGrid>
        </>
      )}
    </section>
  );
}
