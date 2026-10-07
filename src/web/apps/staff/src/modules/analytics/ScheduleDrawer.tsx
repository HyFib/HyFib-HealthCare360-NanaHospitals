import {
  Button,
  Caption1,
  DrawerBody,
  DrawerFooter,
  DrawerHeader,
  DrawerHeaderTitle,
  Field,
  Input,
  makeStyles,
  MessageBar,
  MessageBarBody,
  OverlayDrawer,
  Select,
  Spinner,
  Tag,
  TagGroup,
  Text,
  tokens,
} from '@fluentui/react-components';
import { DatabaseArrowRightRegular, DismissRegular, MailClockRegular } from '@fluentui/react-icons';
import { zodResolver } from '@hookform/resolvers/zod';
import { isApiError, useSession } from '@hc360/core';
import { ChoiceField, SelectField, SwitchField, TextField, toFormPath, useNotify } from '@hc360/ui';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { Controller, useForm, useWatch } from 'react-hook-form';
import {
  analyticsApi,
  analyticsKeys,
  type ReportCatalogue,
  type ReportInputs,
  type ScheduleDestination,
  type ScheduleDto,
} from './api';
import {
  CATEGORY_ORDER,
  CUSTOM_PERIOD,
  EXTRACT_FREQUENCIES,
  FORMAT_LABELS,
  FREQUENCIES,
  FREQUENCY_LABELS,
  LAST_DAY_OF_MONTH,
  MAX_EXTRACT_NAME_LENGTH,
  WEEKDAYS,
} from './labels';
import { describeSchedule, parseEmails, periodLabel, scheduleInputs, withDefaults } from './params';
import { ReportParameterForm } from './ReportInputs';
import {
  defaultSchedulePeriod,
  MAX_RECIPIENTS,
  normalizeExtractName,
  SCHEDULE_TIME,
  scheduleFormDefaults,
  scheduleFormSchema,
  toSaveRequest,
  type ScheduleForm,
} from './schedule-form';

const useStyles = makeStyles({
  form: { display: 'flex', flexDirection: 'column', gap: '14px' },
  row: { display: 'grid', gap: '12px', gridTemplateColumns: 'repeat(auto-fill, minmax(160px, 1fr))' },
  recipients: { display: 'flex', flexDirection: 'column', gap: '6px' },
  muted: { color: tokens.colorNeutralForeground3 },
  title: { display: 'flex', alignItems: 'center', gap: '8px' },
});

const FORM_FIELDS = new Set([
  'name',
  'reportId',
  'format',
  'frequency',
  'time',
  'dayOfWeek',
  'dayOfMonth',
  'recipients',
  'extractName',
  'isActive',
]);

const DAY_OF_MONTH_OPTIONS = [
  ...Array.from({ length: 28 }, (_, i) => ({ value: String(i + 1), label: String(i + 1) })),
  { value: String(LAST_DAY_OF_MONTH), label: 'Last day of the month' },
];

/** Minutes past the hour an hourly extract can run at; a minute saved another way is offered too. */
function minuteOptions(time: string): { value: string; label: string }[] {
  const minutes = Array.from({ length: 12 }, (_, i) => i * 5);
  const current = Number(time.slice(3, 5));
  if (!Number.isNaN(current) && !minutes.includes(current)) {
    minutes.push(current);
    minutes.sort((a, b) => a - b);
  }
  return minutes.map((m) => ({
    value: `00:${String(m).padStart(2, '0')}`,
    label: m === 0 ? 'On the hour' : `${m} minutes past`,
  }));
}

export interface ScheduleDraft {
  reportId: string;
  inputs: ReportInputs;
}

/**
 * Create or change a scheduled report e-mail or a BI extract: which report (patient lists can be neither), its inputs
 * with a relative period, and when (IST); for an e-mail, the file format and who receives it; for an extract, the CSV
 * file it writes to the extract folder.
 */
export function ScheduleDrawer({
  catalogue,
  schedule,
  draft,
  destination = 'Email',
  onClose,
}: {
  catalogue: ReportCatalogue;
  schedule: ScheduleDto | null;
  draft?: ScheduleDraft | null;
  /** For a new schedule: an e-mail or a BI extract. A saved one keeps its own. */
  destination?: ScheduleDestination;
  onClose: () => void;
}) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const facilityName = useSession((s) => s.user?.facilityName);
  const schedulable = catalogue.reports.filter((r) => r.canSchedule);
  const initialReport = schedulable.find((r) => r.id === (schedule?.reportId ?? draft?.reportId));
  const kind = schedule?.destination ?? destination;
  const extract = kind === 'ExtractFolder';

  const form = useForm<ScheduleForm>({
    resolver: zodResolver(scheduleFormSchema),
    defaultValues: scheduleFormDefaults(schedule, kind, initialReport),
  });
  const [parameters, setParameters] = useState<ReportInputs>(() => {
    const start = scheduleInputs(schedule?.parameters ?? draft?.inputs ?? {});
    // Like the server: without a chosen period, an e-mail covers the complete period that just ended, and an extract
    // the financial year.
    return initialReport?.parameters.some((p) => p.kind === 'Period') && !start.period
      ? { ...start, period: defaultSchedulePeriod(kind, schedule?.frequency ?? 'Daily') }
      : start;
  });
  const [periodTouched, setPeriodTouched] = useState(
    Boolean(schedule) || Boolean(draft?.inputs.period && draft.inputs.period !== CUSTOM_PERIOD),
  );
  const [parameterErrors, setParameterErrors] = useState<Record<string, string>>({});

  const [reportId, frequency, time, dayOfWeek, dayOfMonth, format, recipients, name, extractName] = useWatch({
    control: form.control,
    name: ['reportId', 'frequency', 'time', 'dayOfWeek', 'dayOfMonth', 'format', 'recipients', 'name', 'extractName'],
  });
  const report = schedulable.find((r) => r.id === reportId);
  const values = report ? scheduleInputs(withDefaults(report.parameters, parameters)) : {};
  const hasPeriod = report?.parameters.some((p) => p.kind === 'Period') ?? false;

  const save = useMutation({
    mutationFn: (value: ScheduleForm) => {
      const body = toSaveRequest(value, values, schedule?.version ?? null);
      return schedule ? analyticsApi.updateSchedule(schedule.id, body) : analyticsApi.createSchedule(body);
    },
    onSuccess: (saved) => {
      void queryClient.invalidateQueries({ queryKey: analyticsKeys.schedules });
      void queryClient.invalidateQueries({ queryKey: ['analytics', 'status'] });
      const done = extract
        ? schedule
          ? 'BI extract saved'
          : 'BI extract created'
        : schedule
          ? 'Schedule saved'
          : 'Schedule created';
      notify.success(done, saved.nextRunAt ? `${saved.when}.` : undefined);
      onClose();
    },
    onError: (error) => {
      if (!isApiError(error) || !error.isValidation) {
        notify.error(error, extract ? 'Could not save the BI extract' : 'Could not save the schedule');
        return;
      }
      const inputs: Record<string, string> = {};
      for (const [key, messages] of Object.entries(error.fieldErrors)) {
        const root = toFormPath(key).split('.')[0] ?? key;
        if (FORM_FIELDS.has(root)) {
          form.setError(root as keyof ScheduleForm, { type: 'server', message: messages[0] });
        } else {
          inputs[key] = messages[0] ?? 'Check this input.';
        }
      }
      setParameterErrors(inputs);
    },
  });

  const changeReport = (id: string) => {
    const next = schedulable.find((r) => r.id === id);
    const previous = report;
    form.setValue('reportId', id, { shouldValidate: true, shouldDirty: true });
    if (next && (!name.trim() || name === previous?.title)) {
      form.setValue('name', next.title, { shouldDirty: true });
    }
    // A new extract's file is named after its report until the name is changed.
    if (extract && next && (!extractName.trim() || extractName === previous?.id)) {
      form.setValue('extractName', next.id, { shouldDirty: true, shouldValidate: form.formState.isSubmitted });
    }
    const period: ReportInputs = next?.parameters.some((p) => p.kind === 'Period')
      ? { period: periodTouched && parameters.period ? parameters.period : defaultSchedulePeriod(kind, frequency) }
      : {};
    setParameters(period);
    setParameterErrors({});
  };

  const changeFrequency = (next: ScheduleForm['frequency']) => {
    form.setValue('frequency', next, { shouldValidate: true, shouldDirty: true });
    if (next === 'Weekly' && !dayOfWeek) {
      form.setValue('dayOfWeek', 'Monday');
    }
    if (next === 'Monthly' && !dayOfMonth) {
      form.setValue('dayOfMonth', '1');
    }
    // An hourly extract keeps only the minutes of the time; another frequency starts again at 06:00.
    if (next === 'Hourly' && SCHEDULE_TIME.test(time)) {
      form.setValue('time', `00:${time.slice(3, 5)}`);
    } else if (frequency === 'Hourly' && next !== 'Hourly') {
      form.setValue('time', '06:00');
    }
    // Until the user picks a period, an e-mail covers the complete period that just ended (yesterday, last week, last
    // month); an extract keeps its period whatever the frequency.
    if (hasPeriod && !periodTouched && !extract) {
      setParameters((current) => ({ ...current, period: defaultSchedulePeriod(kind, next) }));
    }
  };

  const when = describeSchedule(
    frequency,
    SCHEDULE_TIME.test(time) ? time : '--:--',
    dayOfWeek,
    dayOfMonth ? Number(dayOfMonth) : null,
  );
  const covers = hasPeriod
    ? `covers ${periodLabel(values.period, catalogue.periods).toLowerCase()}`
    : 'current position';
  const summary = !report
    ? null
    : extract
      ? [when, covers, `writes ${extractName.trim() || 'the file'}.csv`].join(' · ')
      : [
          when,
          covers,
          FORMAT_LABELS[format],
          recipients.length === 1 ? '1 recipient' : `${recipients.length} recipients`,
        ].join(' · ');

  return (
    <OverlayDrawer
      open
      position="end"
      size="medium"
      onOpenChange={(_, data) => !data.open && !save.isPending && onClose()}
    >
      <DrawerHeader>
        <DrawerHeaderTitle
          action={<Button appearance="subtle" aria-label="Close" icon={<DismissRegular />} onClick={onClose} />}
        >
          <span className={styles.title}>
            {extract ? <DatabaseArrowRightRegular aria-hidden /> : <MailClockRegular aria-hidden />}
            {extract
              ? schedule
                ? 'Change BI extract'
                : 'New BI extract'
              : schedule
                ? 'Change scheduled e-mail'
                : 'New scheduled e-mail'}
          </span>
        </DrawerHeaderTitle>
      </DrawerHeader>
      <DrawerBody>
        <form
          id="schedule-form"
          className={styles.form}
          onSubmit={form.handleSubmit((value) => !save.isPending && save.mutate(value))}
          noValidate
        >
          <TextField
            control={form.control}
            name="name"
            label="Name"
            required
            autoFocus
            maxLength={100}
            placeholder={extract ? 'For example: Revenue for Power BI' : 'For example: Daily census for the MD'}
          />

          <Controller
            control={form.control}
            name="reportId"
            render={({ field, fieldState }) => (
              <Field
                label="Report"
                required
                validationMessage={fieldState.error?.message}
                validationState={fieldState.error ? 'error' : 'none'}
              >
                <Select value={field.value} onChange={(_, data) => changeReport(data.value)} onBlur={field.onBlur}>
                  <option value="">Choose a report</option>
                  {CATEGORY_ORDER.map((category) => {
                    const reports = schedulable.filter((r) => r.category === category);
                    return reports.length === 0 ? null : (
                      <optgroup key={category} label={reports[0]?.categoryLabel}>
                        {reports.map((r) => (
                          <option key={r.id} value={r.id}>
                            {r.title}
                          </option>
                        ))}
                      </optgroup>
                    );
                  })}
                </Select>
              </Field>
            )}
          />

          {report && report.parameters.some((p) => p.kind !== 'Facility') && (
            <Field
              label="Report inputs"
              hint={
                extract
                  ? `Extracts always cover ${facilityName ?? 'this facility'}, with a period relative to the day they are refreshed.`
                  : `E-mails always cover ${facilityName ?? 'this facility'}, with a period relative to the day they are sent.`
              }
            >
              <ReportParameterForm
                parameters={report.parameters}
                values={values}
                onChange={(next) => {
                  if (next.period !== values.period) {
                    setPeriodTouched(true);
                  }
                  setParameters(next);
                  setParameterErrors({});
                }}
                periods={catalogue.periods}
                errors={parameterErrors}
                mode="schedule"
                maxDays={report.maxDays}
              />
            </Field>
          )}

          <div className={styles.row}>
            <Controller
              control={form.control}
              name="frequency"
              render={({ field }) => (
                <Field label="How often" required>
                  <Select
                    value={field.value}
                    onChange={(_, data) => changeFrequency(data.value as ScheduleForm['frequency'])}
                  >
                    {(extract ? EXTRACT_FREQUENCIES : FREQUENCIES).map((f) => (
                      <option key={f} value={f}>
                        {FREQUENCY_LABELS[f]}
                      </option>
                    ))}
                  </Select>
                </Field>
              )}
            />
            {frequency === 'Weekly' && (
              <SelectField
                control={form.control}
                name="dayOfWeek"
                label="On"
                required
                options={WEEKDAYS.map((d) => ({ value: d, label: d }))}
              />
            )}
            {frequency === 'Monthly' && (
              <SelectField
                control={form.control}
                name="dayOfMonth"
                label="On day"
                required
                options={DAY_OF_MONTH_OPTIONS}
              />
            )}
            {frequency === 'Hourly' ? (
              <SelectField control={form.control} name="time" label="At" required options={minuteOptions(time)} />
            ) : (
              <Controller
                control={form.control}
                name="time"
                render={({ field, fieldState }) => (
                  <Field
                    label="At (IST)"
                    required
                    validationMessage={fieldState.error?.message}
                    validationState={fieldState.error ? 'error' : 'none'}
                  >
                    <Input
                      type="time"
                      step={60}
                      value={field.value}
                      onChange={(_, data) => field.onChange(data.value)}
                      onBlur={field.onBlur}
                    />
                  </Field>
                )}
              />
            )}
          </div>

          {extract ? (
            <Controller
              control={form.control}
              name="extractName"
              render={({ field, fieldState }) => (
                <Field
                  label="File name"
                  required
                  hint="Lower-case letters, digits and dashes. Each refresh replaces this file in the facility's extract folder."
                  validationMessage={fieldState.error?.message}
                  validationState={fieldState.error ? 'error' : 'none'}
                >
                  <Input
                    value={field.value}
                    maxLength={MAX_EXTRACT_NAME_LENGTH}
                    placeholder="daily-revenue"
                    contentAfter={<Text className={styles.muted}>.csv</Text>}
                    onChange={(_, data) => field.onChange(normalizeExtractName(data.value))}
                    onBlur={field.onBlur}
                  />
                </Field>
              )}
            />
          ) : (
            <>
              <ChoiceField
                control={form.control}
                name="format"
                label="File"
                options={[
                  { value: 'Xlsx', label: 'Excel workbook' },
                  { value: 'Csv', label: 'CSV file' },
                  { value: 'Pdf', label: 'PDF (print-ready)' },
                ]}
              />

              <Controller
                control={form.control}
                name="recipients"
                render={({ field, fieldState }) => (
                  <RecipientsField value={field.value} onChange={field.onChange} error={fieldState.error?.message} />
                )}
              />
            </>
          )}

          <SwitchField
            control={form.control}
            name="isActive"
            label={extract ? 'Refresh the file on this schedule' : 'Send e-mails on this schedule'}
            hint={
              extract
                ? 'Turn off to pause the refreshes. The last file stays in the folder.'
                : 'Turn off to pause the e-mails without deleting the schedule.'
            }
          />

          {summary && (
            <MessageBar intent="info" layout="multiline">
              <MessageBarBody>{summary}</MessageBarBody>
            </MessageBar>
          )}
          {schedule && !schedule.isMine && (
            <MessageBar intent="warning" layout="multiline">
              <MessageBarBody>
                Saving makes you the owner of this schedule. The report then runs with your access to reports and
                facilities.
              </MessageBarBody>
            </MessageBar>
          )}
          <Caption1 className={styles.muted}>
            {extract
              ? 'Each refresh replaces the file, so the BI tool always reads the latest data: one row per row of the report, with the facility code in the first column. Reports that list patients cannot be extracts.'
              : 'The file is attached to the e-mail (up to 10 MB) and can be downloaded again from the delivery history. Reports that list patients cannot be e-mailed.'}
          </Caption1>
        </form>
      </DrawerBody>
      <DrawerFooter>
        <Button
          appearance="primary"
          type="submit"
          form="schedule-form"
          disabled={save.isPending}
          // Keeps focus while saving (a disabled button drops it); Enter can still submit, which the form ignores.
          disabledFocusable={save.isPending}
          icon={save.isPending ? <Spinner size="tiny" /> : undefined}
        >
          {extract ? (schedule ? 'Save extract' : 'Create extract') : schedule ? 'Save schedule' : 'Create schedule'}
        </Button>
        <Button onClick={onClose}>Cancel</Button>
      </DrawerFooter>
    </OverlayDrawer>
  );
}

/** E-mail addresses as removable tags: type or paste several (comma, semicolon or space) and press Enter. */
function RecipientsField({
  value,
  onChange,
  error,
}: {
  value: string[];
  onChange: (value: string[]) => void;
  error?: string;
}) {
  const styles = useStyles();
  const [text, setText] = useState('');
  const [problem, setProblem] = useState<string | null>(null);

  const add = (input: string) => {
    const { valid, invalid } = parseEmails(input);
    const next = [...value, ...valid.filter((email) => !value.includes(email))].slice(0, MAX_RECIPIENTS);
    if (next.length !== value.length) {
      onChange(next);
    }
    setProblem(invalid.length > 0 ? `“${invalid[0]}” is not a valid e-mail address.` : null);
    setText(invalid.join(' '));
  };

  const message = problem ?? error;
  return (
    <Field
      label="Send to"
      required
      hint={`Up to ${MAX_RECIPIENTS} addresses. Only addresses of the hospital's e-mail domains may be allowed.`}
      validationMessage={message}
      validationState={message ? 'error' : 'none'}
    >
      <div className={styles.recipients}>
        {value.length > 0 && (
          <TagGroup
            aria-label="Recipients"
            onDismiss={(_, data) => onChange(value.filter((email) => email !== data.value))}
          >
            {value.map((email) => (
              <Tag
                key={email}
                value={email}
                dismissible
                dismissIcon={{ 'aria-label': `Remove ${email}` }}
                shape="circular"
                size="small"
              >
                {email}
              </Tag>
            ))}
          </TagGroup>
        )}
        <Input
          type="email"
          value={text}
          placeholder="name@hospital.in — press Enter to add"
          disabled={value.length >= MAX_RECIPIENTS}
          onChange={(_, data) => setText(data.value)}
          onKeyDown={(event) => {
            if (['Enter', ',', ';'].includes(event.key)) {
              event.preventDefault();
              if (text.trim()) {
                add(text);
              }
            } else if (event.key === 'Backspace' && !text && value.length > 0) {
              onChange(value.slice(0, -1));
            }
          }}
          onPaste={(event) => {
            const pasted = event.clipboardData.getData('text');
            if (/[\s,;]/.test(pasted.trim())) {
              event.preventDefault();
              add(`${text} ${pasted}`);
            }
          }}
          onBlur={() => {
            if (text.trim()) {
              add(text);
            }
          }}
        />
      </div>
    </Field>
  );
}
