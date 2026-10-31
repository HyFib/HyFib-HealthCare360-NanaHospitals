import { Checkbox, Field, Input, makeStyles, type FieldProps } from '@fluentui/react-components';
import { CheckboxGroup } from '@hc360/ui';
import type { ReactNode } from 'react';
import { Controller, type Control, type FieldPath, type FieldValues } from 'react-hook-form';

const useStyles = makeStyles({
  group: { display: 'flex', flexWrap: 'wrap', gap: '2px 12px' },
  columns: { display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(190px, 1fr))', gap: '2px 12px' },
});

interface BaseProps<T extends FieldValues> {
  control: Control<T>;
  name: FieldPath<T>;
  label: string;
  hint?: FieldProps['hint'];
  required?: boolean;
  disabled?: boolean;
}

/**
 * Date and time as "yyyy-MM-ddTHH:mm" in hospital time (IST). Convert with `fromIstInput` when submitting.
 * Uses the native picker: keyboard and touch friendly on workstations and tablets.
 */
export function DateTimeField<T extends FieldValues>({
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
            type="datetime-local"
            name={field.name}
            ref={field.ref}
            onBlur={field.onBlur}
            value={(field.value as string | null | undefined) ?? ''}
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

/** Clock time "HH:mm" (24-hour value; the browser shows it in the user's format). */
export function TimeField<T extends FieldValues>({ control, name, label, hint, required, disabled }: BaseProps<T>) {
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
            type="time"
            name={field.name}
            ref={field.ref}
            onBlur={field.onBlur}
            value={(field.value as string | null | undefined) ?? ''}
            onChange={(_, data) => field.onChange(data.value === '' ? null : data.value)}
            step={300}
            disabled={disabled}
          />
        </Field>
      )}
    />
  );
}

/** Several choices from a short list (modalities, complications, symptoms): one tick each, no dropdown. */
export function CheckboxGroupField<T extends FieldValues>({
  control,
  name,
  label,
  hint,
  required,
  disabled,
  options,
  layout = 'wrap',
}: Omit<BaseProps<T>, 'hint'> & {
  hint?: ReactNode;
  options: readonly { value: string; label: string }[];
  layout?: 'wrap' | 'columns';
}) {
  const styles = useStyles();
  return (
    <Controller
      control={control}
      name={name}
      render={({ field, fieldState }) => {
        const selected = (field.value as string[] | null | undefined) ?? [];
        return (
          <CheckboxGroup
            label={label}
            hint={hint}
            required={required}
            validationMessage={fieldState.error?.message}
            validationState={fieldState.error ? 'error' : 'none'}
          >
            <div className={layout === 'columns' ? styles.columns : styles.group}>
              {options.map((option) => (
                <Checkbox
                  key={option.value}
                  label={option.label}
                  disabled={disabled}
                  checked={selected.includes(option.value)}
                  onChange={(_, data) =>
                    field.onChange(
                      data.checked === true
                        ? [...selected, option.value]
                        : selected.filter((value) => value !== option.value),
                    )
                  }
                />
              ))}
            </div>
          </CheckboxGroup>
        );
      }}
    />
  );
}
