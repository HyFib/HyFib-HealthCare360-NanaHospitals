import { Combobox, Field, Input, makeStyles, Option, Select } from '@fluentui/react-components';
import { todayIso, useDebouncedValue, useSession } from '@hc360/core';
import { useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import {
  analyticsQueries,
  type LookupKind,
  type PeriodPreset,
  type ReportInputs,
  type ReportParameter,
  type ReportParameterKind,
} from './api';
import { CUSTOM_PERIOD, DEFAULT_PERIOD, PERIOD_PRESETS } from './labels';
import { daysInRange, isIsoDate, resolvePeriod, setInput } from './params';

const useStyles = makeStyles({
  form: { display: 'flex', flexWrap: 'wrap', gap: '8px 12px', alignItems: 'flex-start' },
  field: { minWidth: '170px', flex: '0 1 220px' },
  wide: { minWidth: '200px', flex: '1 1 240px', maxWidth: '320px' },
  date: { minWidth: '150px', flex: '0 1 170px' },
  number: { minWidth: '120px', flex: '0 1 150px' },
});

const LOOKUPS: Partial<Record<ReportParameterKind, { kind: LookupKind; all: string }>> = {
  Department: { kind: 'departments', all: 'All departments' },
  Practitioner: { kind: 'practitioners', all: 'All doctors' },
  Payer: { kind: 'payers', all: 'All payers' },
  Ward: { kind: 'wards', all: 'All wards' },
  Store: { kind: 'stores', all: 'All stores' },
  Theatre: { kind: 'theatres', all: 'All theatres' },
};

const ALL = '__all';
const EARLIEST = '2000-01-01';

export type ReportFormMode = 'report' | 'widget' | 'schedule';

export interface ReportParameterFormProps {
  parameters: readonly ReportParameter[];
  /** Current inputs with the report defaults filled in (see `withDefaults`). */
  values: ReportInputs;
  onChange: (next: ReportInputs) => void;
  periods?: readonly PeriodPreset[];
  /** Messages by parameter key ("period", "from", "departmentId"…), for example from the server. */
  errors?: Record<string, string | undefined>;
  /** Schedules use relative periods only and always run for their own facility. */
  mode?: ReportFormMode;
  canConsolidate?: boolean;
  /** Dates of the current run, used when switching to custom dates. */
  range?: { from: string | null; to: string | null } | null;
  /** Names of selected items by parameter key (from the report's filters) for lists longer than the first 200. */
  knownNames?: Record<string, string>;
  maxDays?: number;
  disabled?: boolean;
}

/** The report's inputs as compact fields; every change is applied at once (the result keeps showing while it reloads). */
export function ReportParameterForm(props: ReportParameterFormProps) {
  const styles = useStyles();
  const { parameters, values, onChange, errors = {}, mode = 'report' } = props;
  const facilityId = values.facilityId && values.facilityId !== 'all' ? values.facilityId : undefined;

  return (
    <div className={styles.form}>
      {parameters.map((parameter) => {
        switch (parameter.kind) {
          case 'Period':
            return (
              <PeriodFields
                key={parameter.key}
                values={values}
                onChange={onChange}
                periods={props.periods ?? PERIOD_PRESETS}
                errors={errors}
                relativeOnly={mode === 'schedule'}
                range={props.range ?? null}
                maxDays={props.maxDays ?? 366}
                disabled={props.disabled}
              />
            );
          case 'Facility':
            return mode === 'schedule' ? null : (
              <FacilityField
                key={parameter.key}
                label={parameter.label}
                value={values[parameter.key] ?? null}
                onChange={(value) => onChange(setInput(values, parameter.key, value))}
                canConsolidate={props.canConsolidate ?? false}
                error={errors[parameter.key]}
                disabled={props.disabled}
              />
            );
          case 'Choice':
            return (
              <Field
                key={parameter.key}
                className={styles.field}
                label={parameter.label}
                hint={parameter.hint ?? undefined}
                required={parameter.required}
                validationMessage={errors[parameter.key]}
                validationState={errors[parameter.key] ? 'error' : 'none'}
              >
                <Select
                  value={values[parameter.key] ?? ''}
                  disabled={props.disabled}
                  onChange={(_, data) => onChange(setInput(values, parameter.key, data.value || null))}
                >
                  {!parameter.required && parameter.defaultValue === null && <option value="">All</option>}
                  {(parameter.choices ?? []).map((choice) => (
                    <option key={choice.value} value={choice.value}>
                      {choice.label}
                    </option>
                  ))}
                </Select>
              </Field>
            );
          case 'Number':
            return (
              <NumberInput
                key={`${parameter.key}:${values[parameter.key] ?? ''}`}
                parameter={parameter}
                value={values[parameter.key] ?? ''}
                onCommit={(value) => onChange(setInput(values, parameter.key, value))}
                error={errors[parameter.key]}
                disabled={props.disabled}
              />
            );
          default: {
            const lookup = LOOKUPS[parameter.kind];
            return lookup ? (
              <LookupField
                key={parameter.key}
                kind={lookup.kind}
                label={parameter.label}
                allLabel={lookup.all}
                facilityId={facilityId}
                value={values[parameter.key] ?? null}
                knownName={props.knownNames?.[parameter.key]}
                onChange={(value) => onChange(setInput(values, parameter.key, value))}
                error={errors[parameter.key]}
                required={parameter.required}
                disabled={props.disabled}
              />
            ) : null;
          }
        }
      })}
    </div>
  );
}

// ------------------------------------------------------------------------------------------------ period

function PeriodFields({
  values,
  onChange,
  periods,
  errors,
  relativeOnly,
  range,
  maxDays,
  disabled,
}: {
  values: ReportInputs;
  onChange: (next: ReportInputs) => void;
  periods: readonly PeriodPreset[];
  errors: Record<string, string | undefined>;
  relativeOnly: boolean;
  range: { from: string | null; to: string | null } | null;
  maxDays: number;
  disabled?: boolean;
}) {
  const styles = useStyles();
  const current = values.period ?? DEFAULT_PERIOD;
  const options = relativeOnly ? periods.filter((p) => p.value !== CUSTOM_PERIOD) : periods;

  const choose = (preset: string) => {
    if (preset !== CUSTOM_PERIOD) {
      onChange(setInput(values, 'period', preset));
      return;
    }
    // Start the custom range from the dates on screen, so switching loses nothing.
    const today = todayIso();
    const start =
      range?.from && range.to
        ? { from: range.from, to: range.to }
        : (resolvePeriod(current, today) ?? { from: today, to: today });
    onChange({ ...setInput(values, 'period', CUSTOM_PERIOD), from: start.from, to: start.to });
  };

  return (
    <>
      <Field
        className={styles.field}
        label="Period"
        validationMessage={errors.period}
        validationState={errors.period ? 'error' : 'none'}
      >
        <Select value={current} disabled={disabled} onChange={(_, data) => choose(data.value)}>
          {options.map((preset) => (
            <option key={preset.value} value={preset.value}>
              {preset.label}
            </option>
          ))}
        </Select>
      </Field>
      {current === CUSTOM_PERIOD && !relativeOnly && (
        <DateRangeFields
          key={`${values.from ?? ''}|${values.to ?? ''}`}
          from={values.from ?? ''}
          to={values.to ?? ''}
          maxDays={maxDays}
          errors={errors}
          disabled={disabled}
          onCommit={(from, to) => onChange({ ...values, period: CUSTOM_PERIOD, from, to })}
        />
      )}
    </>
  );
}

/** From/to dates, applied once both are valid (a half-typed range never runs the report). */
function DateRangeFields({
  from: initialFrom,
  to: initialTo,
  maxDays,
  errors,
  disabled,
  onCommit,
}: {
  from: string;
  to: string;
  maxDays: number;
  errors: Record<string, string | undefined>;
  disabled?: boolean;
  onCommit: (from: string, to: string) => void;
}) {
  const styles = useStyles();
  const [from, setFrom] = useState(initialFrom);
  const [to, setTo] = useState(initialTo);
  const today = todayIso();

  const problem = (nextFrom: string, nextTo: string): { field: 'from' | 'to'; message: string } | null => {
    if (!isIsoDate(nextFrom)) {
      return { field: 'from', message: 'Enter the start date.' };
    }
    if (!isIsoDate(nextTo)) {
      return { field: 'to', message: 'Enter the end date.' };
    }
    if (nextFrom < EARLIEST) {
      return { field: 'from', message: 'Choose a date from the year 2000 onwards.' };
    }
    if (nextTo > today) {
      return { field: 'to', message: 'The end date cannot be in the future.' };
    }
    if (nextFrom > nextTo) {
      return { field: 'from', message: 'The start date is after the end date.' };
    }
    if (daysInRange(nextFrom, nextTo) > maxDays) {
      return { field: 'to', message: `Choose at most ${maxDays} days.` };
    }
    return null;
  };

  const local = problem(from, to);
  const update = (nextFrom: string, nextTo: string) => {
    setFrom(nextFrom);
    setTo(nextTo);
    if (!problem(nextFrom, nextTo) && (nextFrom !== initialFrom || nextTo !== initialTo)) {
      onCommit(nextFrom, nextTo);
    }
  };
  const message = (field: 'from' | 'to') => (local?.field === field ? local.message : errors[field]);

  return (
    <>
      <Field
        className={styles.date}
        label="From"
        validationMessage={message('from')}
        validationState={message('from') ? 'error' : 'none'}
      >
        <Input
          type="date"
          value={from}
          min={EARLIEST}
          max={to || today}
          disabled={disabled}
          onChange={(_, data) => update(data.value, to)}
        />
      </Field>
      <Field
        className={styles.date}
        label="To"
        validationMessage={message('to')}
        validationState={message('to') ? 'error' : 'none'}
      >
        <Input
          type="date"
          value={to}
          min={from || EARLIEST}
          max={today}
          disabled={disabled}
          onChange={(_, data) => update(from, data.value)}
        />
      </Field>
    </>
  );
}

// ------------------------------------------------------------------------------------------------ number

function NumberInput({
  parameter,
  value,
  onCommit,
  error,
  disabled,
}: {
  parameter: ReportParameter;
  value: string;
  onCommit: (value: string | null) => void;
  error?: string;
  disabled?: boolean;
}) {
  const styles = useStyles();
  const [text, setText] = useState(value);
  const number = Number(text);
  const outOfRange =
    text !== '' &&
    (!Number.isInteger(number) ||
      (parameter.min !== null && number < parameter.min) ||
      (parameter.max !== null && number > parameter.max));
  const localError = outOfRange
    ? `Enter a whole number from ${parameter.min ?? 0} to ${parameter.max ?? '∞'}.`
    : undefined;
  const commit = () => {
    if (!outOfRange && text !== value) {
      onCommit(text === '' ? null : String(number));
    }
  };

  return (
    <Field
      className={styles.number}
      label={parameter.label}
      hint={parameter.hint ?? undefined}
      validationMessage={localError ?? error}
      validationState={localError || error ? 'error' : 'none'}
    >
      <Input
        type="number"
        inputMode="numeric"
        value={text}
        min={parameter.min ?? undefined}
        max={parameter.max ?? undefined}
        step={1}
        disabled={disabled}
        onChange={(_, data) => setText(data.value)}
        onBlur={commit}
        onKeyDown={(event) => {
          if (event.key === 'Enter') {
            commit();
          }
        }}
      />
    </Field>
  );
}

// ------------------------------------------------------------------------------------------------ facility and look-ups

/** The facility a report covers: the signed-in facility by default, another allowed one, or all of them combined. */
export function FacilityField({
  label = 'Facility',
  value,
  onChange,
  canConsolidate,
  error,
  disabled,
}: {
  label?: string;
  value: string | null;
  onChange: (value: string | null) => void;
  canConsolidate: boolean;
  error?: string;
  disabled?: boolean;
}) {
  const styles = useStyles();
  const user = useSession((s) => s.user);
  const facilities = useQuery(analyticsQueries.lookup('facilities'));
  const list = facilities.data ?? [];
  if (list.length <= 1 && !canConsolidate) {
    return null;
  }
  const current = user?.facilityId ?? '';
  const selected = value ?? current;
  const hasCurrent = list.some((f) => f.id === current);

  return (
    <Field className={styles.wide} label={label} validationMessage={error} validationState={error ? 'error' : 'none'}>
      <Select
        value={selected}
        disabled={disabled}
        onChange={(_, data) => onChange(data.value === current || data.value === '' ? null : data.value)}
      >
        {!hasCurrent && <option value={current}>{user?.facilityName ?? 'My facility'}</option>}
        {list.map((facility) => (
          <option key={facility.id} value={facility.id}>
            {facility.name}
            {facility.id === current ? ' (signed in)' : ''}
          </option>
        ))}
        {canConsolidate && <option value="all">All facilities combined</option>}
      </Select>
    </Field>
  );
}

function LookupField({
  kind,
  label,
  allLabel,
  facilityId,
  value,
  knownName,
  onChange,
  error,
  required,
  disabled,
}: {
  kind: LookupKind;
  label: string;
  allLabel: string;
  facilityId?: string;
  value: string | null;
  knownName?: string;
  onChange: (value: string | null) => void;
  error?: string;
  required?: boolean;
  disabled?: boolean;
}) {
  const styles = useStyles();
  // null = not typing: the box shows the selected name.
  const [text, setText] = useState<string | null>(null);
  const term = useDebouncedValue((text ?? '').trim(), 250);
  const base = useQuery(analyticsQueries.lookup(kind, facilityId));
  const all = base.data ?? [];
  // The first 200 cover almost every list; longer ones (doctors in a big hospital) are searched on the server.
  const remoteSearch = term.length >= 2 && all.length >= 200;
  const remote = useQuery({ ...analyticsQueries.lookup(kind, facilityId, term), enabled: remoteSearch });

  const filter = (text ?? '').trim().toLowerCase();
  const items = remoteSearch
    ? (remote.data ?? [])
    : filter
      ? all.filter((item) => item.name.toLowerCase().includes(filter) || item.code.toLowerCase().includes(filter))
      : all;
  const selectedName = value
    ? (all.find((i) => i.id === value)?.name ??
      remote.data?.find((i) => i.id === value)?.name ??
      knownName ??
      'Selected')
    : '';

  return (
    <Field
      className={styles.wide}
      label={label}
      required={required}
      validationMessage={error}
      validationState={error ? 'error' : 'none'}
    >
      <Combobox
        placeholder={allLabel}
        disabled={disabled}
        value={text ?? selectedName}
        selectedOptions={value ? [value] : []}
        onChange={(event) => setText(event.target.value)}
        onOptionSelect={(_, data) => {
          setText(null);
          onChange(data.optionValue && data.optionValue !== ALL ? data.optionValue : null);
        }}
        onOpenChange={(_, data) => {
          if (!data.open) {
            setText(null);
          }
        }}
        clearable
      >
        {!required && (
          <Option value={ALL} text={allLabel}>
            {allLabel}
          </Option>
        )}
        {items.slice(0, 200).map((item) => (
          <Option key={item.id} value={item.id} text={item.name}>
            {item.name}
          </Option>
        ))}
        {items.length === 0 && (
          <Option value="__none" text="" disabled>
            {base.isLoading || remote.isFetching ? 'Loading…' : 'No matches'}
          </Option>
        )}
      </Combobox>
    </Field>
  );
}
