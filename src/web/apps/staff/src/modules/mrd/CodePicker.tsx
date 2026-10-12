import {
  Badge,
  Caption1,
  Combobox,
  Field,
  makeStyles,
  Option,
  Spinner,
  Text,
  tokens,
  type FieldProps,
} from '@fluentui/react-components';
import { useDebouncedValue } from '@hc360/core';
import { useQuery } from '@tanstack/react-query';
import { useState, type ReactNode } from 'react';
import { mrdQueries, type IcdCodeDto, type ProcedureCodeDto } from './api';
import { usageMark } from './catalogue';

const FREE_TEXT = '__free-text__';

const useStyles = makeStyles({
  option: { display: 'flex', flexDirection: 'column', gap: '2px', minWidth: 0 },
  optionRow: { display: 'flex', alignItems: 'baseline', gap: '8px', flexWrap: 'wrap' },
  code: {
    fontFamily: tokens.fontFamilyMonospace,
    fontWeight: tokens.fontWeightSemibold,
    color: tokens.colorBrandForeground1,
  },
  muted: { color: tokens.colorNeutralForeground3 },
});

interface CommonProps {
  label: string;
  placeholder?: string;
  hint?: FieldProps['hint'];
  autoFocus?: boolean;
  disabled?: boolean;
  required?: boolean;
  validationMessage?: string;
}

/**
 * Ranked ICD search (code prefix "e119" → E11.9, title words, shorthand such as "T2DM", "HTN"). Arrow keys move,
 * Enter picks. `onFreeText` offers a working diagnosis without a code when nothing fits. `release` limits it to one
 * classification (MRD coding for claims uses ICD-10); without it, ICD-11 codes saved from the WHO search appear too.
 */
export function IcdCodePicker({
  onPick,
  onFreeText,
  billableOnly = false,
  release = null,
  ...common
}: CommonProps & {
  onPick: (code: IcdCodeDto) => void;
  onFreeText?: (text: string) => void;
  billableOnly?: boolean;
  release?: string | null;
}) {
  const styles = useStyles();
  const [term, setTerm] = useState('');
  const debounced = useDebouncedValue(term.trim(), 200);
  const results = useQuery({
    ...mrdQueries.icd(debounced, null, billableOnly, 20, release),
    enabled: debounced.length >= 2,
  });
  const items = debounced.length >= 2 ? (results.data ?? []) : [];

  return (
    <PickerShell
      {...common}
      term={term}
      onTermChange={setTerm}
      busy={results.isFetching}
      onSelect={(value) => {
        if (value === FREE_TEXT) {
          onFreeText?.(term.trim());
        } else {
          const picked = items.find((item) => item.id === value);
          if (picked) {
            onPick(picked);
          }
        }
        setTerm('');
      }}
    >
      {items.map((item) => (
        <Option key={item.id} value={item.id} text={`${item.code}${usageMark(item.usage)} ${item.title}`}>
          <span className={styles.option}>
            <span className={styles.optionRow}>
              <span className={styles.code}>
                {item.code}
                {usageMark(item.usage)}
              </span>
              <Text>{item.title}</Text>
              {!item.isBillable && (
                <Badge size="small" appearance="outline" color="warning">
                  Category
                </Badge>
              )}
              {item.usage === 'Asterisk' && (
                <Badge size="small" appearance="outline" color="informative">
                  Not primary
                </Badge>
              )}
            </span>
            {item.blockTitle && <Caption1 className={styles.muted}>{item.blockTitle}</Caption1>}
          </span>
        </Option>
      ))}
      {debounced.length >= 2 && !results.isFetching && items.length === 0 && !onFreeText && (
        <Option value="" disabled text="No match">
          No ICD code matches. Try other words or the code.
        </Option>
      )}
      {onFreeText && term.trim().length >= 2 && (
        <Option value={FREE_TEXT} text={term.trim()}>
          Use “{term.trim()}” as a working diagnosis without a code
        </Option>
      )}
    </PickerShell>
  );
}

/** ICD-10-PCS / local procedure code search for MRD coding. */
export function ProcedureCodePicker({ onPick, ...common }: CommonProps & { onPick: (code: ProcedureCodeDto) => void }) {
  const styles = useStyles();
  const [term, setTerm] = useState('');
  const debounced = useDebouncedValue(term.trim(), 200);
  const results = useQuery({ ...mrdQueries.procedures(debounced), enabled: debounced.length >= 2 });
  const items = debounced.length >= 2 ? (results.data ?? []) : [];

  return (
    <PickerShell
      {...common}
      term={term}
      onTermChange={setTerm}
      busy={results.isFetching}
      onSelect={(value) => {
        const picked = items.find((item) => item.id === value);
        if (picked) {
          onPick(picked);
        }
        setTerm('');
      }}
    >
      {items.map((item) => (
        <Option key={item.id} value={item.id} text={`${item.code} ${item.title}`}>
          <span className={styles.option}>
            <span className={styles.optionRow}>
              <span className={styles.code}>{item.code}</span>
              <Text>{item.title}</Text>
            </span>
            {item.category && <Caption1 className={styles.muted}>{item.category}</Caption1>}
          </span>
        </Option>
      ))}
      {debounced.length >= 2 && !results.isFetching && items.length === 0 && (
        <Option value="" disabled text="No match">
          No procedure code matches.
        </Option>
      )}
    </PickerShell>
  );
}

function PickerShell({
  label,
  placeholder,
  hint,
  autoFocus,
  disabled,
  required,
  validationMessage,
  term,
  onTermChange,
  onSelect,
  busy,
  children,
}: CommonProps & {
  term: string;
  onTermChange: (term: string) => void;
  onSelect: (value: string) => void;
  busy: boolean;
  children: ReactNode;
}) {
  return (
    <Field
      label={label}
      hint={hint}
      required={required}
      validationMessage={validationMessage}
      validationState={validationMessage ? 'error' : 'none'}
    >
      <Combobox
        freeform
        autoFocus={autoFocus}
        disabled={disabled}
        placeholder={placeholder ?? 'Type a code or words, e.g. E11.9, T2DM, dengue'}
        value={term}
        selectedOptions={[]}
        onChange={(event) => onTermChange(event.target.value)}
        onOptionSelect={(_, data) => {
          if (data.optionValue) {
            onSelect(data.optionValue);
          }
        }}
        expandIcon={busy ? <Spinner size="extra-tiny" aria-hidden /> : undefined}
        positioning={{ autoSize: 'width' }}
      >
        {children}
      </Combobox>
    </Field>
  );
}
