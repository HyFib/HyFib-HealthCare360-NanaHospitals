import { zodResolver } from '@hookform/resolvers/zod';
import {
  Button,
  Checkbox,
  Field,
  Input,
  makeStyles,
  mergeClasses,
  Select,
  Spinner,
  Textarea,
  tokens,
  useId,
} from '@fluentui/react-components';
import { CheckmarkCircleRegular, ErrorCircleRegular, LockClosedRegular } from '@fluentui/react-icons';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute } from '@tanstack/react-router';
import { useEffect, useMemo, useRef, useState, type ReactElement } from 'react';
import { Controller, useForm, useWatch, type Control, type FieldPath } from 'react-hook-form';
import { z } from 'zod';
import { ChoiceCards } from '@/components/ChoiceCards';
import { BackLink, ButtonLink, TextLink } from '@/components/links';
import { PortalPage } from '@/components/PortalPage';
import { Notice } from '@/components/States';
import { useT, type MessageKey, type Translate } from '@/i18n';
import { istDateOf } from '@/i18n/format';
import { useFormat } from '@/i18n/useFormat';
import { describeError } from '@/lib/errors';
import { sanitizePhoneInput } from '@/lib/otp';
import { GENDER_KEY } from '@/lib/people';
import { CORRECTABLE_FIELDS, PORTAL_REQUESTERS, type CorrectableField, type PortalRequestCreated } from '@/lib/types';
import { privacyApi, privacyQueries, publicPrivacyQueries } from '@/modules/privacy/api';
import {
  FIELD_FORM,
  FIELD_LABEL,
  KIND_LABEL,
  REQUEST_KINDS,
  REQUESTER_CHOICE,
  type RequestKind,
} from '@/modules/privacy/labels';
import {
  buildRequest,
  checkRequest,
  DETAILS_MAX,
  EMAIL_MAX,
  emptyRequestForm,
  GENDER_CHOICES,
  isTooManyOpen,
  mapServerErrors,
  NAME_MAX,
  parseRequestKind,
  requestFormPath,
  sentFields,
  VALUE_MAX,
  type RequestFormValues,
} from '@/modules/privacy/model';
import { ReportCard } from '@/modules/reports/ReportParts';
import { useActiveProfile } from '@/shell/profileContext';

/**
 * A privacy request from the portal: correct the patient's details, erase what the law allows, or raise a concern
 * (DPDP Act s12 and s13). `?type=` chooses which; without one the page takes a concern.
 */
export const Route = createFileRoute('/_auth/my-privacy/requests/new')({
  validateSearch: (search: Record<string, unknown>): { type?: RequestKind } => ({
    type: parseRequestKind(search.type),
  }),
  component: NewPrivacyRequestPage,
});

const LEAD: Record<RequestKind, MessageKey> = {
  correction: 'myPrivacy.new.correction.lead',
  erasure: 'myPrivacy.new.erasure.lead',
  grievance: 'myPrivacy.new.grievance.lead',
};

const DETAILS: Record<RequestKind, { label: MessageKey; hint: MessageKey }> = {
  correction: { label: 'myPrivacy.new.details.correction', hint: 'myPrivacy.new.details.correction.hint' },
  erasure: { label: 'myPrivacy.new.details.erasure', hint: 'myPrivacy.new.details.erasure.hint' },
  grievance: { label: 'myPrivacy.new.details.grievance', hint: 'myPrivacy.new.details.grievance.hint' },
};

const ERASABLE: MessageKey[] = [
  'myPrivacy.new.erasure.can.messages',
  'myPrivacy.new.erasure.can.feedback',
  'myPrivacy.new.erasure.can.nominee',
  'myPrivacy.new.erasure.can.portal',
];

const KEPT: MessageKey[] = [
  'myPrivacy.new.erasure.keep.records',
  'myPrivacy.new.erasure.keep.bills',
  'myPrivacy.new.erasure.keep.log',
];

const useStyles = makeStyles({
  form: { display: 'flex', flexDirection: 'column', gap: '20px' },
  lead: {
    margin: 0,
    color: tokens.colorNeutralForeground2,
    fontSize: tokens.fontSizeBase400,
    lineHeight: tokens.lineHeightBase400,
  },
  person: { margin: 0, fontWeight: tokens.fontWeightSemibold },
  other: { display: 'flex', flexWrap: 'wrap', alignItems: 'center', columnGap: '16px', margin: 0 },
  otherLink: { display: 'inline-flex', alignItems: 'center', minHeight: '44px' },
  group: { display: 'flex', flexDirection: 'column', gap: '8px', border: 'none', margin: 0, padding: 0, minWidth: 0 },
  legend: { padding: 0, fontWeight: tokens.fontWeightSemibold, fontSize: tokens.fontSizeBase300 },
  legendHint: { margin: 0, color: tokens.colorNeutralForeground2, fontSize: tokens.fontSizeBase200 },
  option: {
    display: 'flex',
    flexDirection: 'column',
    borderRadius: tokens.borderRadiusLarge,
    border: `1px solid ${tokens.colorNeutralStroke1}`,
    backgroundColor: tokens.colorNeutralBackground1,
  },
  optionChecked: { border: `2px solid ${tokens.colorBrandStroke1}`, margin: '-1px' },
  checkbox: { display: 'flex', alignItems: 'center', minHeight: '52px', width: '100%' },
  checkboxLabel: { flexGrow: 1, fontSize: tokens.fontSizeBase400 },
  value: { padding: '0 12px 12px 44px', '@media (max-width: 480px)': { paddingLeft: '12px' } },
  groupError: {
    display: 'flex',
    alignItems: 'center',
    gap: '6px',
    margin: 0,
    color: tokens.colorPaletteRedForeground1,
    fontSize: tokens.fontSizeBase200,
    lineHeight: tokens.lineHeightBase200,
  },
  bullets: { listStyle: 'none', margin: 0, padding: 0, display: 'flex', flexDirection: 'column', gap: '10px' },
  bullet: {
    display: 'flex',
    alignItems: 'flex-start',
    gap: '10px',
    fontSize: tokens.fontSizeBase400,
    lineHeight: tokens.lineHeightBase400,
  },
  canIcon: { flexShrink: 0, fontSize: '20px', marginTop: '2px', color: tokens.colorStatusSuccessForeground1 },
  keepIcon: { flexShrink: 0, fontSize: '20px', marginTop: '2px', color: tokens.colorNeutralForeground3 },
  policy: { margin: 0, color: tokens.colorNeutralForeground2, fontSize: tokens.fontSizeBase300 },
  submit: { alignSelf: 'flex-start', minWidth: '200px' },
  done: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    gap: '12px',
    padding: '24px 16px',
    textAlign: 'center',
    borderRadius: tokens.borderRadiusXLarge,
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  doneIcon: { fontSize: '56px', color: tokens.colorStatusSuccessForeground1 },
  doneTitle: {
    margin: 0,
    fontSize: tokens.fontSizeBase500,
    fontWeight: tokens.fontWeightSemibold,
    ':focus-visible': {
      outline: `3px solid ${tokens.colorStrokeFocus2}`,
      outlineOffset: '4px',
      borderRadius: tokens.borderRadiusMedium,
    },
  },
  doneText: {
    margin: 0,
    color: tokens.colorNeutralForeground2,
    fontSize: tokens.fontSizeBase400,
    lineHeight: tokens.lineHeightBase400,
  },
  doneActions: { display: 'flex', flexWrap: 'wrap', justifyContent: 'center', gap: '10px' },
});

function requestSchema(t: Translate, kind: RequestKind, today: string) {
  const values = Object.fromEntries(CORRECTABLE_FIELDS.map((field) => [field, z.string()])) as Record<
    CorrectableField,
    z.ZodString
  >;
  return z
    .object({
      requestedBy: z.enum(PORTAL_REQUESTERS).nullable(),
      requesterName: z.string(),
      fields: z.array(z.enum(CORRECTABLE_FIELDS)),
      values: z.object(values),
      details: z.string(),
      understood: z.boolean(),
    })
    .superRefine((value, ctx) => {
      for (const problem of checkRequest(value, kind, today)) {
        ctx.addIssue({ code: 'custom', path: problem.path, message: t(problem.key) });
      }
    });
}

function NewPrivacyRequestPage() {
  const { type } = Route.useSearch();
  const kind = type ?? 'grievance';
  // A different kind of request is a different form: nothing typed for one carries over to another.
  return <PrivacyRequestForm key={kind} kind={kind} />;
}

function PrivacyRequestForm({ kind }: { kind: RequestKind }) {
  const styles = useStyles();
  const t = useT();
  const f = useFormat();
  const queryClient = useQueryClient();
  const { profile } = useActiveProfile();
  const info = useQuery(publicPrivacyQueries.info());
  const status = useQuery(privacyQueries.status(profile.id));
  const [today] = useState(() => istDateOf(Date.now()));
  const [created, setCreated] = useState<{ result: PortalRequestCreated; forSomeoneElse: boolean } | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [tooMany, setTooMany] = useState(false);
  const schema = useMemo(() => requestSchema(t, kind, today), [t, kind, today]);
  const form = useForm<RequestFormValues>({ resolver: zodResolver(schema), defaultValues: emptyRequestForm() });
  const requestedBy = useWatch({ control: form.control, name: 'requestedBy' });
  const busy = form.formState.isSubmitting;
  const label = KIND_LABEL[kind];
  const back = <BackLink to="/my-privacy">{t('nav.myPrivacy')}</BackLink>;
  // Said before anything is typed when the portal's limit is already reached (the server still decides on sending).
  const atLimit = status.data !== undefined && status.data.openRequests >= status.data.maxOpenRequests;
  const tooManyNotice = (
    <Notice
      intent="warning"
      title={t('myPrivacy.new.tooMany.title')}
      action={
        <ButtonLink to="/my-privacy/requests" appearance="secondary" size="medium">
          {t('myPrivacy.requests.see')}
        </ButtonLink>
      }
    >
      {status.data
        ? t('myPrivacy.new.tooMany', { count: f.number(status.data.maxOpenRequests) })
        : t('myPrivacy.new.tooMany.noCount')}
    </Notice>
  );

  const submit = form.handleSubmit(async (values) => {
    if (!values.requestedBy) {
      return;
    }
    setError(null);
    setTooMany(false);
    const sent = sentFields(values, kind);
    try {
      const result = await privacyApi.createRequest(buildRequest(values, kind, profile.id, values.requestedBy));
      void queryClient.invalidateQueries({ queryKey: privacyQueries.all(profile.id) });
      setCreated({ result, forSomeoneElse: values.requestedBy !== 'Self' });
    } catch (err) {
      if (isTooManyOpen(err)) {
        setTooMany(true);
        void queryClient.invalidateQueries({ queryKey: privacyQueries.status(profile.id).queryKey });
        return;
      }
      const mapped = mapServerErrors(err, (field) => requestFormPath(field, sent));
      if (!mapped) {
        setError(describeError(err, t, 'errors.saveTitle').message);
        return;
      }
      mapped.fields.forEach(({ path, message }, index) =>
        form.setError(path as FieldPath<RequestFormValues>, { type: 'server', message }, { shouldFocus: index === 0 }),
      );
      if (mapped.other.length > 0) {
        setError(mapped.other.join(' '));
      }
    }
  });

  if (created) {
    return (
      <PortalPage title={t(label.title)} back={back} width="narrow">
        <RequestSent result={created.result} forSomeoneElse={created.forSomeoneElse} />
      </PortalPage>
    );
  }

  return (
    <PortalPage title={t(label.title)} back={back} width="narrow">
      <p className={styles.lead}>{t(LEAD[kind])}</p>
      <p className={styles.person}>{t('myPrivacy.new.about', { name: profile.fullName })}</p>
      <p className={styles.other}>
        <span>{t('myPrivacy.new.other')}</span>
        {REQUEST_KINDS.filter((other) => other !== kind).map((other) => (
          <TextLink
            key={other}
            to="/my-privacy/requests/new"
            search={{ type: other }}
            replace
            className={styles.otherLink}
          >
            {t(KIND_LABEL[other].title)}
          </TextLink>
        ))}
      </p>

      {/* After a refusal the same notice sits by the button instead, so it does not jump up the page. */}
      {atLimit && !tooMany && tooManyNotice}

      {kind === 'correction' && <Notice intent="info">{t('myPrivacy.new.correction.how')}</Notice>}
      {kind === 'erasure' && (
        <>
          <ReportCard title={t('myPrivacy.new.erasure.can')}>
            <ul className={styles.bullets}>
              {ERASABLE.map((key) => (
                <li key={key} className={styles.bullet}>
                  <CheckmarkCircleRegular className={styles.canIcon} aria-hidden />
                  <span>{t(key)}</span>
                </li>
              ))}
            </ul>
          </ReportCard>
          <ReportCard title={t('myPrivacy.new.erasure.keep')}>
            <ul className={styles.bullets}>
              {KEPT.map((key) => (
                <li key={key} className={styles.bullet}>
                  <LockClosedRegular className={styles.keepIcon} aria-hidden />
                  <span>{t(key)}</span>
                </li>
              ))}
            </ul>
            <p className={styles.policy}>{t('myPrivacy.new.erasure.keep.backups')}</p>
          </ReportCard>
        </>
      )}

      <form
        className={styles.form}
        noValidate
        onSubmit={(event) => {
          event.preventDefault();
          void submit();
        }}
      >
        <Controller
          control={form.control}
          name="requestedBy"
          render={({ field, fieldState }) => (
            <Field validationState={fieldState.error ? 'error' : 'none'} validationMessage={fieldState.error?.message}>
              {/* When nothing is chosen yet, the first choice takes focus so the question is read out again. */}
              <div
                ref={(element) =>
                  field.ref(
                    element
                      ? { focus: () => element.querySelector<HTMLInputElement>('input:not(:disabled)')?.focus() }
                      : null,
                  )
                }
              >
                <ChoiceCards
                  label={t('myPrivacy.new.who')}
                  name={field.name}
                  value={field.value}
                  onChange={(value) => field.onChange(value)}
                  options={PORTAL_REQUESTERS.map((requester) => ({
                    value: requester,
                    title: t(REQUESTER_CHOICE[requester].title),
                    hint: t(REQUESTER_CHOICE[requester].hint, { name: profile.fullName }),
                    disabled: busy,
                  }))}
                />
              </div>
            </Field>
          )}
        />

        {requestedBy && requestedBy !== 'Self' && (
          <Controller
            control={form.control}
            name="requesterName"
            render={({ field, fieldState }) => (
              <Field
                label={t('myPrivacy.new.yourName')}
                hint={t('myPrivacy.new.yourName.hint', { name: profile.fullName })}
                required
                validationState={fieldState.error ? 'error' : 'none'}
                validationMessage={fieldState.error?.message}
              >
                <Input
                  ref={field.ref}
                  size="large"
                  name={field.name}
                  value={field.value}
                  onChange={(_, data) => field.onChange(data.value)}
                  onBlur={field.onBlur}
                  maxLength={NAME_MAX}
                  autoComplete="name"
                  disabled={busy}
                />
              </Field>
            )}
          />
        )}

        {kind === 'correction' && <CorrectionPicker control={form.control} busy={busy} today={today} />}

        <Controller
          control={form.control}
          name="details"
          render={({ field, fieldState }) => (
            <Field
              label={
                kind === 'correction' ? `${t(DETAILS[kind].label)} ${t('common.optional')}` : t(DETAILS[kind].label)
              }
              hint={t(DETAILS[kind].hint)}
              required={kind !== 'correction'}
              validationState={fieldState.error ? 'error' : 'none'}
              validationMessage={fieldState.error?.message}
            >
              <Textarea
                ref={field.ref}
                name={field.name}
                value={field.value}
                onChange={(_, data) => field.onChange(data.value)}
                onBlur={field.onBlur}
                rows={kind === 'correction' ? 3 : 5}
                maxLength={DETAILS_MAX}
                resize="vertical"
                disabled={busy}
              />
            </Field>
          )}
        />

        {kind === 'erasure' && (
          <>
            <Notice intent="warning">{t('myPrivacy.new.erasure.portal')}</Notice>
            <Controller
              control={form.control}
              name="understood"
              render={({ field, fieldState }) => (
                <Field
                  validationState={fieldState.error ? 'error' : 'none'}
                  validationMessage={fieldState.error?.message}
                >
                  <Checkbox
                    ref={field.ref}
                    size="large"
                    label={t('myPrivacy.new.understood')}
                    checked={field.value}
                    onChange={(_, data) => field.onChange(data.checked === true)}
                    disabled={busy}
                  />
                </Field>
              )}
            />
          </>
        )}

        {kind === 'grievance' && <Notice intent="info">{t('myPrivacy.new.truthful')}</Notice>}

        {tooMany && tooManyNotice}
        {error && <Notice intent="error">{error}</Notice>}
        {info.data && (
          <p className={styles.policy}>{t('myPrivacy.contact.reply', { days: f.number(info.data.responseDays) })}</p>
        )}
        <Button
          type="submit"
          appearance="primary"
          size="large"
          className={styles.submit}
          disabled={busy}
          icon={busy ? <Spinner size="tiny" /> : undefined}
        >
          {busy ? t('common.sending') : t('myPrivacy.new.submit')}
        </Button>
      </form>
    </PortalPage>
  );
}

/** The details to correct: a checkbox for each, and the correct value under each one chosen. */
function CorrectionPicker({
  control,
  busy,
  today,
}: {
  control: Control<RequestFormValues>;
  busy: boolean;
  today: string;
}) {
  const styles = useStyles();
  const t = useT();
  const errorId = useId('fields-error');
  return (
    <Controller
      control={control}
      name="fields"
      render={({ field, fieldState }) => (
        <fieldset className={styles.group} aria-describedby={fieldState.error ? errorId : undefined}>
          <legend className={styles.legend}>{t('myPrivacy.new.fields')}</legend>
          <p className={styles.legendHint}>{t('myPrivacy.new.fields.hint')}</p>
          {CORRECTABLE_FIELDS.map((name, index) => {
            const checked = field.value.includes(name);
            return (
              <div key={name} className={mergeClasses(styles.option, checked && styles.optionChecked)}>
                <Checkbox
                  ref={index === 0 ? field.ref : undefined}
                  size="large"
                  className={styles.checkbox}
                  label={{ children: t(FIELD_LABEL[name]), className: styles.checkboxLabel }}
                  checked={checked}
                  disabled={busy}
                  onChange={(_, data) =>
                    field.onChange(
                      data.checked === true ? [...field.value, name] : field.value.filter((chosen) => chosen !== name),
                    )
                  }
                />
                {checked && <CorrectionValue name={name} control={control} busy={busy} today={today} />}
              </div>
            );
          })}
          {fieldState.error?.message && (
            <p id={errorId} role="alert" className={styles.groupError}>
              <ErrorCircleRegular aria-hidden />
              {fieldState.error.message}
            </p>
          )}
        </fieldset>
      )}
    />
  );
}

/** The correct value for one detail, asked for in the way that suits it (a date, a mobile number, a choice…). */
function CorrectionValue({
  name,
  control,
  busy,
  today,
}: {
  name: CorrectableField;
  control: Control<RequestFormValues>;
  busy: boolean;
  today: string;
}) {
  const styles = useStyles();
  const t = useT();
  const f = useFormat();
  const { profile } = useActiveProfile();
  const spec = FIELD_FORM[name];

  // What the portal already knows, so the patient sees what they are correcting.
  let current: string | null = null;
  if (name === 'name') {
    current = profile.fullName;
  } else if (name === 'dateOfBirth' && profile.dateOfBirth) {
    current = f.calendarDate(profile.dateOfBirth);
  } else if (name === 'gender' && profile.gender !== 'Unknown') {
    current = t(GENDER_KEY[profile.gender]);
  }
  const hint =
    [spec.hint ? t(spec.hint) : null, current ? t('myPrivacy.new.value.now', { value: current }) : null]
      .filter(Boolean)
      .join(' ') || undefined;

  return (
    <Controller
      control={control}
      name={`values.${name}`}
      render={({ field, fieldState }) => {
        const common = {
          name: field.name,
          value: field.value,
          onBlur: field.onBlur,
          disabled: busy,
        };
        let input: ReactElement;
        switch (spec.input) {
          case 'long':
            input = (
              <Textarea
                {...common}
                ref={field.ref}
                onChange={(_, data) => field.onChange(data.value)}
                rows={3}
                maxLength={VALUE_MAX}
                resize="vertical"
              />
            );
            break;
          case 'gender':
            input = (
              <Select {...common} ref={field.ref} size="large" onChange={(_, data) => field.onChange(data.value)}>
                <option value="">{t('myPrivacy.new.value.select')}</option>
                {GENDER_CHOICES.map((gender) => (
                  <option key={gender} value={gender}>
                    {t(GENDER_KEY[gender])}
                  </option>
                ))}
              </Select>
            );
            break;
          case 'date':
            input = (
              <Input
                {...common}
                ref={field.ref}
                type="date"
                size="large"
                min="1900-01-01"
                max={today}
                onChange={(_, data) => field.onChange(data.value)}
              />
            );
            break;
          case 'mobile':
            input = (
              <Input
                {...common}
                ref={field.ref}
                type="tel"
                inputMode="numeric"
                autoComplete="off"
                size="large"
                contentBefore={<span>+91</span>}
                onChange={(_, data) => field.onChange(sanitizePhoneInput(data.value))}
                maxLength={14}
              />
            );
            break;
          case 'phone':
            input = (
              <Input
                {...common}
                ref={field.ref}
                type="tel"
                inputMode="tel"
                autoComplete="off"
                size="large"
                onChange={(_, data) => field.onChange(data.value)}
                maxLength={20}
              />
            );
            break;
          case 'email':
            input = (
              <Input
                {...common}
                ref={field.ref}
                type="email"
                inputMode="email"
                autoComplete="off"
                size="large"
                onChange={(_, data) => field.onChange(data.value)}
                maxLength={EMAIL_MAX}
              />
            );
            break;
          default:
            input = (
              <Input
                {...common}
                ref={field.ref}
                size="large"
                autoComplete="off"
                onChange={(_, data) => field.onChange(data.value)}
                maxLength={VALUE_MAX}
              />
            );
        }
        return (
          <Field
            className={styles.value}
            label={t(spec.label)}
            hint={hint}
            required
            validationState={fieldState.error ? 'error' : 'none'}
            validationMessage={fieldState.error?.message}
          >
            {input}
          </Field>
        );
      }}
    />
  );
}

/** "Request sent", with the number and the date we reply by. Focus moves here so it is read out at once. */
function RequestSent({ result, forSomeoneElse }: { result: PortalRequestCreated; forSomeoneElse: boolean }) {
  const styles = useStyles();
  const t = useT();
  const f = useFormat();
  const heading = useRef<HTMLHeadingElement>(null);

  useEffect(() => {
    heading.current?.focus();
  }, []);

  return (
    <div className={styles.done} role="status">
      <CheckmarkCircleRegular className={styles.doneIcon} aria-hidden />
      <h2 ref={heading} tabIndex={-1} className={styles.doneTitle}>
        {t('myPrivacy.done.title')}
      </h2>
      <p className={styles.doneText}>
        {t('myPrivacy.done.body', { number: result.requestNumber, date: f.date(result.dueAt) })}
      </p>
      {forSomeoneElse && <p className={styles.doneText}>{t('myPrivacy.done.checking')}</p>}
      <div className={styles.doneActions}>
        <ButtonLink to="/my-privacy/requests/$requestId" params={{ requestId: result.id }} appearance="primary">
          {t('myPrivacy.done.view')}
        </ButtonLink>
        <ButtonLink to="/my-privacy" appearance="secondary">
          {t('myPrivacy.done.back')}
        </ButtonLink>
      </div>
    </div>
  );
}
