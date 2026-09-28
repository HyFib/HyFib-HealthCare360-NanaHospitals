import { Button, Field, MessageBar, MessageBarActions, MessageBarBody, MessageBarTitle, Select, type FieldProps } from '@fluentui/react-components';
import { ArrowClockwiseRegular } from '@fluentui/react-icons';
import { isApiError } from '@hc360/core';
import type { StoreDto } from './api';
import { storeTypeLabel } from './labels';
import type { WorkingStore } from './stores';

/** A store drop-down (the store type is shown when the list mixes types). */
export function StorePicker({
  stores,
  value,
  onChange,
  label = 'Store',
  allowAll,
  allLabel = 'All stores',
  placeholder = 'Choose a store',
  loading,
  disabled,
  hint,
  required,
  excludeId,
  validationMessage,
}: {
  stores: readonly StoreDto[];
  value: string | null | undefined;
  onChange: (storeId: string | null) => void;
  label?: string;
  allowAll?: boolean;
  allLabel?: string;
  placeholder?: string;
  loading?: boolean;
  disabled?: boolean;
  hint?: FieldProps['hint'];
  required?: boolean;
  excludeId?: string | null;
  validationMessage?: string;
}) {
  const types = new Set(stores.map((s) => s.type));
  const options = stores.filter((s) => s.id !== excludeId);
  return (
    <Field label={label} hint={hint} required={required} validationMessage={validationMessage} validationState={validationMessage ? 'error' : 'none'}>
      <Select value={value ?? ''} onChange={(_, d) => onChange(d.value || null)} disabled={disabled || loading}>
        {allowAll ? <option value="">{allLabel}</option> : !value && <option value="">{loading ? 'Loading stores…' : placeholder}</option>}
        {options.map((s) => (
          <option key={s.id} value={s.id}>
            {s.name}
            {types.size > 1 ? ` (${storeTypeLabel[s.type]})` : ''}
            {s.isActive ? '' : ' — inactive'}
          </option>
        ))}
      </Select>
    </Field>
  );
}

/** Working-store picker bound to {@link useWorkingStore}. */
export function WorkingStorePicker({ working, label = 'Working store', hint }: { working: WorkingStore; label?: string; hint?: FieldProps['hint'] }) {
  return <StorePicker stores={working.stores} value={working.storeId} onChange={working.setStoreId} label={label} loading={working.loading} hint={hint} />;
}

/** Why no store can be chosen (no permission, failed to load, or none set up), with the next step. */
export function StoreProblem({ working, kind = 'store' }: { working: WorkingStore; kind?: string }) {
  if (working.noAccess) {
    return (
      <MessageBar intent="warning" layout="multiline">
        <MessageBarBody>
          <MessageBarTitle>Your account cannot see the {kind} list</MessageBarTitle>
          Ask the administrator to add the permission to view stock or stores to your role. Screens that sell or issue stock need to know which
          {` ${kind}`} you work in.
        </MessageBarBody>
      </MessageBar>
    );
  }
  if (working.error) {
    return (
      <MessageBar intent="error" layout="multiline">
        <MessageBarBody>
          <MessageBarTitle>Could not load the stores</MessageBarTitle>
          {isApiError(working.error) ? working.error.userMessage : 'Check the connection and try again.'}
        </MessageBarBody>
        <MessageBarActions>
          <Button icon={<ArrowClockwiseRegular />} onClick={working.refetch}>
            Retry
          </Button>
        </MessageBarActions>
      </MessageBar>
    );
  }
  if (!working.loading && working.stores.length === 0) {
    return (
      <MessageBar intent="warning" layout="multiline">
        <MessageBarBody>
          <MessageBarTitle>No {kind} is set up</MessageBarTitle>
          An administrator can add one under Stores and inventory, then Stores.
        </MessageBarBody>
      </MessageBar>
    );
  }
  return null;
}
