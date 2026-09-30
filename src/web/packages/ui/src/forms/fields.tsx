import {
  Combobox,
  Field,
  Input,
  Option,
  Radio,
  RadioGroup,
  Select,
  Switch,
  Textarea,
  type FieldProps,
  type InputProps,
} from '@fluentui/react-components';
import type { LookupItem } from '@hc360/core';
import { useEffect, useEffectEvent, useState } from 'react';
import { Controller, type Control, type FieldPath, type FieldValues } from 'react-hook-form';

interface BaseProps<T extends FieldValues> {
  control: Control<T>;
  name: FieldPath<T>;
  label: string;
  hint?: FieldProps['hint'];
  required?: boolean;
  disabled?: boolean;
}

export interface OptionItem {
  value: string;
  label: string;
}

export function TextField<T extends FieldValues>({
  control,
  name,
  label,
  hint,
  required,
  disabled,
  type = 'text',
  placeholder,
  autoFocus,
  maxLength,
  contentBefore,
  autoComplete,
}: BaseProps<T> & {
  type?: InputProps['type'];
  placeholder?: string;
  autoFocus?: boolean;
  maxLength?: number;
  contentBefore?: InputProps['contentBefore'];
  autoComplete?: string;
}) {
  return (
    <Controller
      control={control}
      name={name}
      render={({ field, fieldState }) => (
        <Field
          label={label}
          hint={hint}
          required={required}
          validationMessage={fieldState.error?.message}
          validationState={fieldState.error ? 'error' : 'none'}
        >
          <Input
            {...field}
            value={(field.value as string | undefined) ?? ''}
            type={type}
            placeholder={placeholder}
            autoFocus={autoFocus}
            maxLength={maxLength}
            disabled={disabled}
            contentBefore={contentBefore}
            autoComplete={autoComplete}
          />
        </Field>
      )}
    />
  );
}

export function TextAreaField<T extends FieldValues>({
  control,
  name,
  label,
  hint,
  required,
  disabled,
  rows = 3,
  placeholder,
  maxLength,
}: BaseProps<T> & { rows?: number; placeholder?: string; maxLength?: number }) {
  return (
    <Controller
      control={control}
      name={name}
      render={({ field, fieldState }) => (
        <Field
          label={label}
          hint={hint}
          required={required}
          validationMessage={fieldState.error?.message}
          validationState={fieldState.error ? 'error' : 'none'}
        >
          <Textarea
            {...field}
            value={(field.value as string | undefined) ?? ''}
            rows={rows}
            placeholder={placeholder}
            maxLength={maxLength}
            disabled={disabled}
            resize="vertical"
          />
        </Field>
      )}
    />
  );
}

export function NumberField<T extends FieldValues>({
  control,
  name,
  label,
  hint,
  required,
  disabled,
  min,
  max,
  step,
}: BaseProps<T> & { min?: number; max?: number; step?: number }) {
  return (
    <Controller
      control={control}
      name={name}
      render={({ field, fieldState }) => (
        <Field
          label={label}
          hint={hint}
          required={required}
          validationMessage={fieldState.error?.message}
          validationState={fieldState.error ? 'error' : 'none'}
        >
          <Input
            type="number"
            name={field.name}
            ref={field.ref}
            onBlur={field.onBlur}
            value={
              field.value === null || field.value === undefined || Number.isNaN(field.value) ? '' : String(field.value)
            }
            onChange={(_, data) => field.onChange(data.value === '' ? null : Number(data.value))}
            min={min}
            max={max}
            step={step}
            disabled={disabled}
          />
        </Field>
      )}
    />
  );
}

/** Native select styled by Fluent: fast, keyboard friendly and accessible. */
export function SelectField<T extends FieldValues>({
  control,
  name,
  label,
  hint,
  required,
  disabled,
  options,
  placeholder,
}: BaseProps<T> & { options: OptionItem[]; placeholder?: string }) {
  return (
    <Controller
      control={control}
      name={name}
      render={({ field, fieldState }) => (
        <Field
          label={label}
          hint={hint}
          required={required}
          validationMessage={fieldState.error?.message}
          validationState={fieldState.error ? 'error' : 'none'}
        >
          <Select
            name={field.name}
            ref={field.ref}
            onBlur={field.onBlur}
            value={(field.value as string | undefined) ?? ''}
            onChange={(_, data) => field.onChange(data.value === '' ? null : data.value)}
            disabled={disabled}
          >
            {placeholder !== undefined && <option value="">{placeholder}</option>}
            {options.map((option) => (
              <option key={option.value} value={option.value}>
                {option.label}
              </option>
            ))}
          </Select>
        </Field>
      )}
    />
  );
}

/** Calendar date as "yyyy-MM-dd" using the native date picker (works with keyboard and touch). */
export function DateField<T extends FieldValues>({
  control,
  name,
  label,
  hint,
  required,
  disabled,
  min,
  max,
}: BaseProps<T> & { min?: string; max?: string }) {
  return (
    <Controller
      control={control}
      name={name}
      render={({ field, fieldState }) => (
        <Field
          label={label}
          hint={hint}
          required={required}
          validationMessage={fieldState.error?.message}
          validationState={fieldState.error ? 'error' : 'none'}
        >
          <Input
            type="date"
            name={field.name}
            ref={field.ref}
            onBlur={field.onBlur}
            value={(field.value as string | undefined) ?? ''}
            onChange={(_, data) => field.onChange(data.value === '' ? null : data.value)}
            min={min}
            max={max}
            disabled={disabled}
          />
        </Field>
      )}
    />
  );
}

export function SwitchField<T extends FieldValues>({ control, name, label, hint, disabled }: BaseProps<T>) {
  return (
    <Controller
      control={control}
      name={name}
      render={({ field }) => (
        <Field hint={hint}>
          <Switch
            label={label}
            checked={Boolean(field.value)}
            onChange={(_, data) => field.onChange(data.checked)}
            disabled={disabled}
          />
        </Field>
      )}
    />
  );
}

/** Small set of mutually exclusive choices shown inline (e.g. gender) — one click, no dropdown. */
export function ChoiceField<T extends FieldValues>({
  control,
  name,
  label,
  hint,
  required,
  disabled,
  options,
}: BaseProps<T> & { options: OptionItem[] }) {
  return (
    <Controller
      control={control}
      name={name}
      render={({ field, fieldState }) => (
        <Field
          label={label}
          hint={hint}
          required={required}
          validationMessage={fieldState.error?.message}
          validationState={fieldState.error ? 'error' : 'none'}
        >
          <RadioGroup
            layout="horizontal"
            value={(field.value as string | undefined) ?? ''}
            onChange={(_, data) => field.onChange(data.value)}
            disabled={disabled}
          >
            {options.map((option) => (
              <Radio key={option.value} value={option.value} label={option.label} />
            ))}
          </RadioGroup>
        </Field>
      )}
    />
  );
}

/**
 * Searchable look-up (doctors, departments, services). `load` is called with the typed text (debounced);
 * the stored value is the item id.
 */
export function LookupField<T extends FieldValues>({
  control,
  name,
  label,
  hint,
  required,
  disabled,
  load,
  placeholder,
  initialItems,
}: BaseProps<T> & {
  load: (search: string) => Promise<LookupItem[]>;
  placeholder?: string;
  initialItems?: LookupItem[];
}) {
  const [search, setSearch] = useState('');
  const [items, setItems] = useState<LookupItem[]>(initialItems ?? []);
  // Callers often pass an inline function: reading the latest one keeps a new identity from re-running the search.
  const loadItems = useEffectEvent((term: string) => load(term));

  useEffect(() => {
    let cancelled = false;
    const timer = setTimeout(() => {
      loadItems(search).then(
        (result) => !cancelled && setItems(result),
        () => undefined,
      );
    }, 250);
    return () => {
      cancelled = true;
      clearTimeout(timer);
    };
  }, [search]);

  return (
    <Controller
      control={control}
      name={name}
      render={({ field, fieldState }) => {
        const selected = items.find((item) => item.id === field.value);
        return (
          <Field
            label={label}
            hint={hint}
            required={required}
            validationMessage={fieldState.error?.message}
            validationState={fieldState.error ? 'error' : 'none'}
          >
            <Combobox
              placeholder={placeholder ?? 'Type to search'}
              disabled={disabled}
              value={search || selected?.name || ''}
              selectedOptions={field.value ? [String(field.value)] : []}
              onChange={(event) => setSearch(event.target.value)}
              onOptionSelect={(_, data) => {
                field.onChange(data.optionValue ?? null);
                setSearch('');
              }}
              onBlur={field.onBlur}
              clearable
            >
              {items.map((item) => (
                <Option key={item.id} value={item.id} text={item.name}>
                  {item.name}
                </Option>
              ))}
            </Combobox>
          </Field>
        );
      }}
    />
  );
}
