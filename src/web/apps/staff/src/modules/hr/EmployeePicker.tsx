import { Combobox, Field, Option, Spinner, type FieldProps } from '@fluentui/react-components';
import { useDebouncedValue } from '@hc360/core';
import { useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { hrQueries } from './api';

export interface PickedEmployee {
  id: string;
  code: string;
  name: string;
}

/** Find an employee by code or name (current staff; separated staff when `includeSeparated`). */
export function EmployeePicker({
  label = 'Employee',
  value,
  onChange,
  required,
  hint,
  validationMessage,
  disabled,
  includeSeparated,
  departmentId,
  autoFocus,
}: {
  label?: string;
  value: PickedEmployee | null;
  onChange: (employee: PickedEmployee | null) => void;
  required?: boolean;
  hint?: FieldProps['hint'];
  validationMessage?: string;
  disabled?: boolean;
  includeSeparated?: boolean;
  departmentId?: string;
  autoFocus?: boolean;
}) {
  const [term, setTerm] = useState('');
  const search = useDebouncedValue(term.trim(), 250);
  const results = useQuery({
    ...hrQueries.employees({ search: search || undefined, includeSeparated: includeSeparated || undefined, departmentId, sort: 'name' }, 15),
    enabled: search.length >= 2,
  });
  const items = search.length >= 2 ? (results.data?.items ?? []) : [];

  return (
    <Field
      label={label}
      required={required}
      hint={hint ?? 'Type two letters of the name or the employee code.'}
      validationMessage={validationMessage}
      validationState={validationMessage ? 'error' : 'none'}
    >
      <Combobox
        placeholder="Name or employee code"
        value={term || (value ? `${value.name} (${value.code})` : '')}
        selectedOptions={value ? [value.id] : []}
        disabled={disabled}
        autoFocus={autoFocus}
        onChange={(event) => {
          setTerm(event.target.value);
          if (!event.target.value) {
            onChange(null);
          }
        }}
        onOptionSelect={(_, data) => {
          const picked = items.find((employee) => employee.id === data.optionValue);
          onChange(picked ? { id: picked.id, code: picked.employeeCode, name: picked.fullName } : null);
          setTerm('');
        }}
        expandIcon={results.isFetching ? <Spinner size="extra-tiny" /> : undefined}
        clearable
      >
        {search.length >= 2 && items.length === 0 && !results.isFetching && (
          <Option value="" disabled text="No match">
            No employee found
          </Option>
        )}
        {items.map((employee) => (
          <Option key={employee.id} value={employee.id} text={`${employee.fullName} (${employee.employeeCode})`}>
            {employee.fullName} · {employee.employeeCode}
            {employee.designationName ? ` · ${employee.designationName}` : ''}
            {employee.departmentName ? ` · ${employee.departmentName}` : ''}
          </Option>
        ))}
      </Combobox>
    </Field>
  );
}
