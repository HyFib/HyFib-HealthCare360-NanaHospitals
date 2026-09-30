import { makeStyles, mergeClasses, tokens, useId } from '@fluentui/react-components';
import type { ReactNode } from 'react';

const useStyles = makeStyles({
  group: { display: 'flex', flexDirection: 'column', gap: '8px', border: 'none', margin: 0, padding: 0, minWidth: 0 },
  legend: { padding: 0, marginBottom: '8px', fontWeight: tokens.fontWeightSemibold, fontSize: tokens.fontSizeBase300 },
  row: { display: 'grid', gridTemplateColumns: 'minmax(0, 1fr)', gap: '8px' },
  twoUp: { '@media (min-width: 520px)': { gridTemplateColumns: 'repeat(2, minmax(0, 1fr))' } },
  option: {
    position: 'relative',
    display: 'flex',
    alignItems: 'flex-start',
    gap: '12px',
    minHeight: '56px',
    padding: '12px 14px',
    boxSizing: 'border-box',
    borderRadius: tokens.borderRadiusLarge,
    border: `1px solid ${tokens.colorNeutralStroke1}`,
    backgroundColor: tokens.colorNeutralBackground1,
    cursor: 'pointer',
    ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover },
    ':focus-within': { outline: `3px solid ${tokens.colorStrokeFocus2}`, outlineOffset: '2px' },
  },
  checked: {
    border: `2px solid ${tokens.colorBrandStroke1}`,
    backgroundColor: tokens.colorBrandBackground2,
    padding: '11px 13px',
  },
  disabled: { cursor: 'not-allowed', opacity: 0.6 },
  input: { margin: '3px 0 0', width: '20px', height: '20px', accentColor: tokens.colorBrandBackground, flexShrink: 0 },
  icon: { fontSize: '22px', color: tokens.colorBrandForeground1, display: 'flex', flexShrink: 0, marginTop: '1px' },
  text: { display: 'flex', flexDirection: 'column', gap: '2px', minWidth: 0 },
  title: { fontWeight: tokens.fontWeightSemibold },
  hint: {
    color: tokens.colorNeutralForeground2,
    fontSize: tokens.fontSizeBase200,
    lineHeight: tokens.lineHeightBase200,
  },
});

export interface ChoiceOption<T extends string> {
  value: T;
  title: string;
  hint?: string;
  icon?: ReactNode;
  disabled?: boolean;
}

/** A radio group drawn as large cards (title + one line of help): easy to tap, clear which one is chosen. */
export function ChoiceCards<T extends string>({
  label,
  value,
  options,
  onChange,
  twoUp,
  name,
}: {
  label: string;
  value: T | null;
  options: ChoiceOption<T>[];
  onChange: (value: T) => void;
  twoUp?: boolean;
  name?: string;
}) {
  const styles = useStyles();
  const generated = useId('choice');
  const groupName = name ?? generated;
  return (
    <fieldset className={styles.group}>
      <legend className={styles.legend}>{label}</legend>
      <div className={mergeClasses(styles.row, twoUp && styles.twoUp)}>
        {options.map((option) => {
          const checked = option.value === value;
          return (
            <label
              key={option.value}
              className={mergeClasses(styles.option, checked && styles.checked, option.disabled && styles.disabled)}
            >
              <input
                type="radio"
                className={styles.input}
                name={groupName}
                value={option.value}
                checked={checked}
                disabled={option.disabled}
                onChange={() => onChange(option.value)}
              />
              {option.icon && (
                <span className={styles.icon} aria-hidden>
                  {option.icon}
                </span>
              )}
              <span className={styles.text}>
                <span className={styles.title}>{option.title}</span>
                {option.hint && <span className={styles.hint}>{option.hint}</span>}
              </span>
            </label>
          );
        })}
      </div>
    </fieldset>
  );
}
