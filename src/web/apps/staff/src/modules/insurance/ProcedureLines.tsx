import {
  Button,
  Caption1,
  Combobox,
  Field,
  Input,
  makeStyles,
  Option,
  Select,
  Spinner,
  Text,
  tokens,
} from '@fluentui/react-components';
import { DeleteRegular } from '@fluentui/react-icons';
import { formatInr, useDebouncedValue } from '@hc360/core';
import { keepPreviousData, useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { useShownItems } from '@/lib/useShownItems';
import { amountInputText, parseRupees } from '@/modules/billing/money';
import { insuranceQueries, type PreAuthProcedureSystem } from './api';

/** The most planned procedures a pre-authorisation lists, and the most times one is planned (as the server allows). */
export const MAX_PROCEDURE_LINES = 20;
const MAX_QUANTITY = 99;

const SYSTEM_LABEL: Record<PreAuthProcedureSystem, string> = {
  Hbp: 'PM-JAY HBP',
  Icd10Pcs: 'ICD-10-PCS',
};

/** A planned procedure while the desk edits it: the amount and quantity as typed. */
export interface ProcedureDraft {
  system: PreAuthProcedureSystem;
  code: string;
  name: string;
  quantity: string;
  amount: string;
}

export function toProcedureDrafts(
  lines: { system: PreAuthProcedureSystem; code: string; name: string; quantity: number; amount: number }[],
): ProcedureDraft[] {
  return lines.map((l) => ({
    system: l.system,
    code: l.code,
    name: l.name,
    quantity: String(l.quantity),
    amount: amountInputText(l.amount),
  }));
}

/** Whether a line's quantity and amount can be sent, and the amount in rupees. */
export function checkProcedureDraft(line: ProcedureDraft) {
  const quantity = Number(line.quantity);
  const quantityOk = Number.isInteger(quantity) && quantity >= 1 && quantity <= MAX_QUANTITY;
  const amount = line.amount.trim() ? parseRupees(line.amount, { allowZero: true }) : null;
  return {
    quantityOk,
    quantity,
    amountError: amount && !amount.ok ? amount.error : null,
    rupees: amount?.ok ? amount.rupees : 0,
  };
}

const useStyles = makeStyles({
  root: { display: 'flex', flexDirection: 'column', gap: '8px' },
  picker: {
    display: 'grid',
    gridTemplateColumns: '160px minmax(0, 1fr)',
    gap: '8px',
    alignItems: 'end',
    '@media (max-width: 640px)': { gridTemplateColumns: '1fr' },
  },
  line: {
    display: 'grid',
    gridTemplateColumns: 'minmax(0, 1fr) 80px 140px auto',
    gap: '8px',
    alignItems: 'end',
    padding: '8px',
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground2,
    '@media (max-width: 640px)': { gridTemplateColumns: '1fr 1fr' },
  },
  muted: { color: tokens.colorNeutralForeground3 },
});

/**
 * The planned procedures of a pre-authorisation, each picked from PM-JAY's HBP master or the hospital's ICD-10-PCS
 * catalogue (INS-04): the code and the master's name, how many times it is planned, and the amount asked for it.
 */
export function ProcedureLines({
  lines,
  onChange,
  legacyText,
}: {
  lines: ProcedureDraft[];
  onChange: (lines: ProcedureDraft[]) => void;
  legacyText?: string | null;
}) {
  const styles = useStyles();
  const [system, setSystem] = useState<PreAuthProcedureSystem>('Hbp');
  const [term, setTerm] = useState('');
  const debounced = useDebouncedValue(term.trim(), 250);
  const options = useQuery({
    ...insuranceQueries.procedureOptions(system, debounced),
    enabled: debounced.length > 0,
    // The list keeps its options while the next search runs, so an option is never taken away under the pointer.
    placeholderData: keepPreviousData,
  });
  // With nothing typed there is no search, and the last one's results are not offered either.
  const items = debounced
    ? (options.data ?? []).filter((o) => !lines.some((l) => l.system === o.system && l.code === o.code))
    : [];
  // The procedure comes from the options shown, not from the results as they are by the time one is chosen.
  const shown = useShownItems(items, (o) => `${o.system}:${o.code}`);
  const full = lines.length >= MAX_PROCEDURE_LINES;
  const total = lines.reduce((sum, l) => sum + checkProcedureDraft(l).rupees, 0);

  const update = (index: number, patch: Partial<ProcedureDraft>) =>
    onChange(lines.map((l, i) => (i === index ? { ...l, ...patch } : l)));

  return (
    <div className={styles.root}>
      {legacyText && (
        <Text block size={200}>
          <span className={styles.muted}>Typed before procedures were coded: </span>
          {legacyText}
        </Text>
      )}
      {lines.map((line, index) => {
        const check = checkProcedureDraft(line);
        return (
          <div key={`${line.system}:${line.code}`} className={styles.line}>
            <div>
              <Text weight="semibold">{line.code}</Text>{' '}
              <Caption1 className={styles.muted}>{SYSTEM_LABEL[line.system]}</Caption1>
              <Text block size={200}>
                {line.name}
              </Text>
            </div>
            <Field
              label="Quantity"
              validationState={check.quantityOk ? 'none' : 'error'}
              validationMessage={check.quantityOk ? undefined : `1 to ${MAX_QUANTITY}`}
            >
              <Input
                value={line.quantity}
                inputMode="numeric"
                onChange={(_, d) => update(index, { quantity: d.value.replace(/\D/g, '') })}
              />
            </Field>
            <Field
              label="Amount asked"
              validationState={check.amountError ? 'error' : 'none'}
              validationMessage={check.amountError ?? undefined}
            >
              <Input
                value={line.amount}
                inputMode="decimal"
                contentBefore="₹"
                onChange={(_, d) => update(index, { amount: d.value })}
              />
            </Field>
            <Button
              appearance="subtle"
              icon={<DeleteRegular />}
              aria-label={`Remove ${line.code}`}
              onClick={() => onChange(lines.filter((_, i) => i !== index))}
            />
          </div>
        );
      })}
      {lines.length > 0 && (
        <Caption1 className={styles.muted}>Amounts asked for the procedures: {formatInr(total)}</Caption1>
      )}
      <div className={styles.picker}>
        <Field label="Code from">
          <Select
            value={system}
            onChange={(_, d) => {
              setSystem(d.value as PreAuthProcedureSystem);
              setTerm('');
            }}
          >
            <option value="Hbp">{SYSTEM_LABEL.Hbp}</option>
            <option value="Icd10Pcs">{SYSTEM_LABEL.Icd10Pcs}</option>
          </Select>
        </Field>
        <Field
          label="Add a planned procedure"
          hint={full ? `At most ${MAX_PROCEDURE_LINES} procedures.` : 'Type a code or words of its name'}
        >
          <Combobox
            placeholder={system === 'Hbp' ? 'e.g. SG039A or cholecystectomy' : 'e.g. 0DTJ4ZZ or appendicectomy'}
            value={term}
            disabled={full}
            onChange={(event) => setTerm(event.target.value)}
            onOptionSelect={(_, data) => {
              const option = shown(data.optionValue);
              if (option) {
                onChange([
                  ...lines,
                  {
                    system: option.system,
                    code: option.code,
                    name: option.name,
                    quantity: '1',
                    amount: option.rate !== null ? amountInputText(option.rate) : '',
                  },
                ]);
              }
              setTerm('');
            }}
            expandIcon={options.isFetching ? <Spinner size="extra-tiny" aria-hidden /> : undefined}
          >
            {items.map((o) => (
              <Option key={`${o.system}:${o.code}`} value={`${o.system}:${o.code}`} text={`${o.code} ${o.name}`}>
                {o.code} · {o.name}
                {o.rate !== null ? ` · ${formatInr(o.rate)}` : ''}
              </Option>
            ))}
            {debounced && !options.isFetching && items.length === 0 && (
              <Option disabled value="" text="">
                {system === 'Hbp' ? 'No procedure of the imported HBP master matches' : 'No ICD-10-PCS code matches'}
              </Option>
            )}
          </Combobox>
        </Field>
      </div>
    </div>
  );
}
