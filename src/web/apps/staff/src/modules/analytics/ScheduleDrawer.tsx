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
  tokens,
} from '@fluentui/react-components';
import { DismissRegular, MailClockRegular } from '@fluentui/react-icons';
import { zodResolver } from '@hookform/resolvers/zod';
import { isApiError, useSession } from '@hc360/core';
import { ChoiceField, SelectField, SwitchField, TextField, toFormPath, useNotify } from '@hc360/ui';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { Controller, useForm, useWatch } from 'react-hook-form';
import { z } from 'zod';
import {
  analyticsApi,
  analyticsKeys,
  type ReportCatalogue,
  type ReportInputs,
  type SaveScheduleRequest,
  type ScheduleDto,
} from './api';
import {
  CATEGORY_ORDER,
  CUSTOM_PERIOD,
  FORMAT_LABELS,
  FREQUENCIES,
  FREQUENCY_LABELS,
  LAST_DAY_OF_MONTH,
  WEEKDAYS,
} from './labels';
import { defaultPeriodFor, describeSchedule, parseEmails, periodLabel, scheduleInputs, withDefaults } from './params';
import { ReportParameterForm } from './ReportInputs';

const useStyles = makeStyles({
  form: { display: 'flex', flexDirection: 'column', gap: '14px' },
  row: { display: 'grid', gap: '12px', gridTemplateColumns: 'repeat(auto-fill, minmax(160px, 1fr))' },
  recipients: { display: 'flex', flexDirection: 'column', gap: '6px' },
  muted: { color: tokens.colorNeutralForeground3 },
  title: { display: 'flex', alignItems: 'center', gap: '8px' },
});

const TIME = /^([01]\d|2[0-3]):[0-5]\d$/;
const MAX_RECIPIENTS = 20;

const scheduleSchema = z
  .object({
    name: z.string().trim().min(1, 'Give the schedule a name.').max(100, 'Use at most 100 characters.'),
    reportId: z.string().min(1, 'Choose a report.'),
    format: z.enum(['Xlsx', 'Csv', 'Pdf']),
    frequency: z.enum(['Daily', 'Weekly', 'Monthly']),
    time: z.string().regex(TIME, 'Enter the time as HH:mm (24-hour clock, IST).'),
    dayOfWeek: z.enum(['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday']).nullable(),
    dayOfMonth: z.string().nullable(),
    recipients: z
      .array(z.string())
      .min(1, 'Add at least one e-mail address.')
      .max(MAX_RECIPIENTS, `Send to at most ${MAX_RECIPIENTS} people.`),
    isActive: z.boolean(),
  })
  .superRefine((value, ctx) => {
    if (value.frequency === 'Weekly' && !value.dayOfWeek) {
      ctx.addIssue({ code: 'custom', path: ['dayOfWeek'], message: 'Choose the day of the week.' });
    }
    if (value.frequency === 'Monthly' && !value.dayOfMonth) {
      ctx.addIssue({ code: 'custom', path: ['dayOfMonth'], message: 'Choose the day of the month.' });
    }
  });

type ScheduleForm = z.infer<typeof scheduleSchema>;
const FORM_FIELDS = new Set([
  'name',
  'reportId',
  'format',
  'frequency',
  'time',
  'dayOfWeek',
  'dayOfMonth',
  'recipients',
  'isActive',
]);

const DAY_OF_MONTH_OPTIONS = [
  ...Array.from({ length: 28 }, (_, i) => ({ value: String(i + 1), label: String(i + 1) })),
  { value: String(LAST_DAY_OF_MONTH), label: 'Last day of the month' },
];

export interface ScheduleDraft {
  reportId: string;
  inputs: ReportInputs;
}

/**
 * Create or change a scheduled report e-mail: which report (patient lists cannot be e-mailed), its inputs with a relative
 * period, when (IST), the file format and who receives it.
 */
export function ScheduleDrawer({
  catalogue,
  schedule,
  draft,
  onClose,
}: {
  catalogue: ReportCatalogue;
  schedule: ScheduleDto | null;
  draft?: ScheduleDraft | null;
  onClose: () => void;
}) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const facilityName = useSession((s) => s.user?.facilityName);
  const schedulable = catalogue.reports.filter((r) => r.canSchedule);
  const initialReport = schedulable.find((r) => r.id === (schedule?.reportId ?? draft?.reportId));

  const form = useForm<ScheduleForm>({
    resolver: zodResolver(scheduleSchema),
    defaultValues: {
      name: schedule?.name ?? initialReport?.title ?? '',
      reportId: initialReport?.id ?? '',
      format: schedule?.format ?? 'Xlsx',
      frequency: schedule?.frequency ?? 'Daily',
      time: schedule?.time ?? '07:00',
      dayOfWeek: schedule?.dayOfWeek ?? null,
      dayOfMonth: schedule?.dayOfMonth ? String(schedule.dayOfMonth) : null,
      recipients: schedule?.recipients ?? [],
      isActive: schedule?.isActive ?? true,
    },
  });
  const [parameters, setParameters] = useState<ReportInputs>(() => {
    const start = scheduleInputs(schedule?.parameters ?? draft?.inputs ?? {});
    // Like the server: without a chosen period, an e-mail covers the complete period that just ended.
    return initialReport?.parameters.some((p) => p.kind === 'Period') && !start.period
      ? { ...start, period: defaultPeriodFor(schedule?.frequency ?? 'Daily') }
      : start;
  });
  const [periodTouched, setPeriodTouched] = useState(
    Boolean(schedule) || Boolean(draft?.inputs.period && draft.inputs.period !== CUSTOM_PERIOD),
  );
  const [parameterErrors, setParameterErrors] = useState<Record<string, string>>({});

  const [reportId, frequency, time, dayOfWeek, dayOfMonth, format, recipients, name] = useWatch({
    control: form.control,
    name: ['reportId', 'frequency', 'time', 'dayOfWeek', 'dayOfMonth', 'format', 'recipients', 'name'],
  });
  const report = schedulable.find((r) => r.id === reportId);
  const values = report ? scheduleInputs(withDefaults(report.parameters, parameters)) : {};
  const hasPeriod = report?.parameters.some((p) => p.kind === 'Period') ?? false;

  const save = useMutation({
    mutationFn: (value: ScheduleForm) => {
      const body: SaveScheduleRequest = {
        name: value.name.trim(),
        reportId: value.reportId,
        parameters: values,
        format: value.format,
        frequency: value.frequency,
        time: value.time,
        dayOfWeek: value.frequency === 'Weekly' ? value.dayOfWeek : null,
        dayOfMonth: value.frequency === 'Monthly' && value.dayOfMonth ? Number(value.dayOfMonth) : null,
        recipients: value.recipients,
        isActive: value.isActive,
        version: schedule?.version ?? null,
      };
      return schedule ? analyticsApi.updateSchedule(schedule.id, body) : analyticsApi.createSchedule(body);
    },
    onSuccess: (saved) => {
      void queryClient.invalidateQueries({ queryKey: analyticsKeys.schedules });
      void queryClient.invalidateQueries({ queryKey: ['analytics', 'status'] });
      notify.success(schedule ? 'Schedule saved' : 'Schedule created', saved.nextRunAt ? `${saved.when}.` : undefined);
      onClose();
    },
    onError: (error) => {
      if (!isApiError(error) || !error.isValidation) {
        notify.error(error, 'Could not save the schedule');
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
    const period: ReportInputs = next?.parameters.some((p) => p.kind === 'Period')
      ? { period: periodTouched && parameters.period ? parameters.period : defaultPeriodFor(frequency) }
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
    // Until the user picks a period, cover the complete period that just ended (yesterday, last week, last month).
    if (hasPeriod && !periodTouched) {
      setParameters((current) => ({ ...current, period: defaultPeriodFor(next) }));
    }
  };

  const summary = report
    ? [
        describeSchedule(
          frequency,
          TIME.test(time) ? time : '--:--',
          dayOfWeek,
          dayOfMonth ? Number(dayOfMonth) : null,
        ),
        hasPeriod ? `covers ${periodLabel(values.period, catalogue.periods).toLowerCase()}` : 'current position',
        FORMAT_LABELS[format],
        recipients.length === 1 ? '1 recipient' : `${recipients.length} recipients`,
      ].join(' · ')
    : null;

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
            <MailClockRegular aria-hidden />
            {schedule ? 'Change scheduled e-mail' : 'New scheduled e-mail'}
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
            placeholder="For example: Daily census for the MD"
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
              hint={`E-mails always cover ${facilityName ?? 'this facility'}, with a period relative to the day they are sent.`}
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
                    {FREQUENCIES.map((f) => (
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
          </div>

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

          <SwitchField
            control={form.control}
            name="isActive"
            label="Send e-mails on this schedule"
            hint="Turn off to pause the e-mails without deleting the schedule."
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
            The file is attached to the e-mail (up to 10 MB) and can be downloaded again from the delivery history.
            Reports that list patients cannot be e-mailed.
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
          {schedule ? 'Save schedule' : 'Create schedule'}
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
