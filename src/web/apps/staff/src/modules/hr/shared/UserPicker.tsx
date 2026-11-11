import { Combobox, Field, Option, Spinner, type FieldProps } from '@fluentui/react-components';
import { useDebouncedValue, useSession } from '@hc360/core';
import { keepPreviousData, useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { searchUsers, USER_DIRECTORY_PERMISSION, useUserName, type PickedUser } from './users';

/**
 * Pick a staff member (sign-in account). Type two letters to search the staff list; the signed-in user is always
 * offered first so "assign to me" is one click.
 */
export function UserPicker({
  label,
  value,
  onChange,
  required,
  hint,
  validationMessage,
  disabled,
  clearable = true,
}: {
  label: string;
  value: PickedUser | null;
  onChange: (user: PickedUser | null) => void;
  required?: boolean;
  hint?: FieldProps['hint'];
  validationMessage?: string;
  disabled?: boolean;
  clearable?: boolean;
}) {
  const me = useSession((s) => s.user);
  const canSearch = usePermission(USER_DIRECTORY_PERMISSION);
  const [term, setTerm] = useState('');
  const debounced = useDebouncedValue(term.trim(), 300);
  const results = useQuery({
    queryKey: ['users', 'directory', debounced],
    queryFn: ({ signal }) => searchUsers(debounced, signal),
    enabled: canSearch && debounced.length >= 2,
    staleTime: 60_000,
    // The list keeps its options while the next search runs, so an option is never taken away under the pointer.
    placeholderData: keepPreviousData,
  });

  const options: { id: string; name: string; text: string }[] = [];
  if (me) {
    options.push({ id: me.id, name: me.displayName, text: `${me.displayName} (you)` });
  }
  // Below two characters there is no search, and the last one's results are not offered either.
  const found = canSearch && debounced.length >= 2 ? (results.data?.items ?? []) : [];
  for (const user of found) {
    if (user.id !== me?.id) {
      options.push({ id: user.id, name: user.displayName, text: `${user.displayName} · ${user.userName}` });
    }
  }
  if (value && !options.some((option) => option.id === value.id)) {
    options.push({ id: value.id, name: value.name, text: value.name });
  }

  return (
    <Field
      label={label}
      required={required}
      validationMessage={validationMessage}
      validationState={validationMessage ? 'error' : 'none'}
      hint={
        hint ??
        (canSearch
          ? 'Type at least two letters of the name.'
          : 'You can choose yourself. Choosing other staff needs access to the staff list.')
      }
    >
      <Combobox
        placeholder={canSearch ? 'Search staff by name' : 'Choose'}
        disabled={disabled}
        value={term || value?.name || ''}
        selectedOptions={value ? [value.id] : []}
        onChange={(event) => {
          setTerm(event.target.value);
          if (!event.target.value) {
            onChange(null);
          }
        }}
        onOptionSelect={(_, data) => {
          // The user comes from the option chosen, not from the results as they are by the time it is chosen.
          onChange(data.optionValue ? { id: data.optionValue, name: data.optionText ?? '' } : null);
          setTerm('');
        }}
        expandIcon={results.isFetching ? <Spinner size="extra-tiny" aria-hidden /> : undefined}
        clearable={clearable}
      >
        {options.map((option) => (
          <Option key={option.id} value={option.id} text={option.name}>
            {option.text}
          </Option>
        ))}
      </Combobox>
    </Field>
  );
}

/** Inline display name of a staff user id (see `useUserName`). */
export function UserName({ id, fallback = '—' }: { id: string | null | undefined; fallback?: string }) {
  const name = useUserName(id);
  return <>{name ?? fallback}</>;
}
