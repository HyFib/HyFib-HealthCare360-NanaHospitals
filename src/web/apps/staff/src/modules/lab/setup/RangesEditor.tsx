import {
  Button,
  Caption1,
  Field,
  Input,
  makeStyles,
  MessageBar,
  MessageBarBody,
  Select,
  Spinner,
  Text,
  tokens,
} from '@fluentui/react-components';
import { AddRegular, CopyRegular, DeleteRegular, SaveRegular } from '@fluentui/react-icons';
import type { Gender } from '@hc360/core';
import { useNotify } from '@hc360/ui';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { errorMessage, indexedFieldErrors } from '../common/errors';
import { labApi, labKeys, type LabTestDetail, type ReferenceRangeDto } from '../api';
import {
  emptyRange,
  fromDays,
  parseList,
  rangeProblem,
  rangeSummary,
  sortRanges,
  toDays,
  type AgeUnit,
} from './ranges';

const useStyles = makeStyles({
  root: { display: 'flex', flexDirection: 'column', gap: '12px' },
  card: {
    display: 'flex',
    flexDirection: 'column',
    gap: '8px',
    padding: '12px',
    borderRadius: tokens.borderRadiusMedium,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  cardHead: { display: 'flex', justifyContent: 'space-between', alignItems: 'center', gap: '8px', flexWrap: 'wrap' },
  grid: { display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(140px, 1fr))', gap: '8px 12px' },
  age: { display: 'flex', gap: '4px' },
  wide: { gridColumn: '1 / -1' },
  problem: { color: tokens.colorPaletteRedForeground1 },
  muted: { color: tokens.colorNeutralForeground3 },
  footer: { display: 'flex', gap: '8px', justifyContent: 'flex-end', flexWrap: 'wrap' },
});

interface RangeRow {
  key: string;
  gender: Gender | null;
  fromValue: string;
  fromUnit: AgeUnit;
  toValue: string;
  toUnit: AgeUnit;
  low: string;
  high: string;
  criticalLow: string;
  criticalHigh: string;
  text: string;
  normalValues: string;
  criticalValues: string;
}

function toRow(range: ReferenceRangeDto, index: number): RangeRow {
  const from = fromDays(range.ageFromDays);
  const to = range.ageToDays === null ? null : fromDays(range.ageToDays);
  const number = (value: number | null) => (value === null ? '' : String(value));
  return {
    key: range.id ?? `new-${index}`,
    gender: range.gender === 'Male' || range.gender === 'Female' ? range.gender : null,
    fromValue: String(from.value),
    fromUnit: from.unit,
    toValue: to ? String(to.value) : '',
    toUnit: to?.unit ?? 'years',
    low: number(range.low),
    high: number(range.high),
    criticalLow: number(range.criticalLow),
    criticalHigh: number(range.criticalHigh),
    text: range.text ?? '',
    normalValues: (range.normalValues ?? []).join(', '),
    criticalValues: (range.criticalValues ?? []).join(', '),
  };
}

function parseNumber(value: string): number | null | 'invalid' {
  const text = value.trim().replace(/,/g, '');
  if (!text) {
    return null;
  }
  const number = Number(text);
  return Number.isFinite(number) ? number : 'invalid';
}

function toDto(row: RangeRow, id: string | null): ReferenceRangeDto | string {
  const numbers = [
    parseNumber(row.low),
    parseNumber(row.high),
    parseNumber(row.criticalLow),
    parseNumber(row.criticalHigh),
  ];
  if (numbers.includes('invalid')) {
    return 'Limits must be numbers.';
  }
  const from = Number(row.fromValue || '0');
  const to = row.toValue.trim() ? Number(row.toValue) : null;
  if (!Number.isFinite(from) || from < 0 || (to !== null && (!Number.isFinite(to) || to <= 0))) {
    return 'Ages must be positive numbers.';
  }
  const [low, high, criticalLow, criticalHigh] = numbers as (number | null)[];
  const normal = parseList(row.normalValues);
  const critical = parseList(row.criticalValues);
  return {
    id,
    gender: row.gender,
    ageFromDays: toDays(from, row.fromUnit),
    ageToDays: to === null ? null : toDays(to, row.toUnit),
    low: low ?? null,
    high: high ?? null,
    criticalLow: criticalLow ?? null,
    criticalHigh: criticalHigh ?? null,
    text: row.text.trim() || null,
    normalValues: normal.length ? normal : null,
    criticalValues: critical.length ? critical : null,
  };
}

const UNITS: { value: AgeUnit; label: string }[] = [
  { value: 'years', label: 'years' },
  { value: 'months', label: 'months' },
  { value: 'days', label: 'days' },
];

/**
 * Reference intervals of an analyte: by sex and age band, with critical limits (numeric tests) or normal/critical
 * answers (answer tests). The server picks the most specific interval for each patient, so a sex-specific adult interval
 * and an all-ages fallback can live side by side.
 */
export function RangesEditor({ test }: { test: LabTestDetail }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const numeric = test.resultType === 'Numeric';
  const [rows, setRows] = useState<RangeRow[]>(() => sortRanges(test.ranges).map(toRow));
  const [dirty, setDirty] = useState(false);

  const ids = new Map(test.ranges.map((range) => [range.id ?? '', range.id]));
  const converted = rows.map((row) => toDto(row, ids.get(row.key) ?? null));
  const options = test.resultType === 'Options' ? test.options.map((option) => option.toLowerCase()) : [];
  const problems = converted.map((dto) => {
    if (typeof dto === 'string') {
      return dto;
    }
    const answers = [...(dto.normalValues ?? []), ...(dto.criticalValues ?? [])];
    if (options.length > 0 && answers.some((answer) => !options.includes(answer.toLowerCase()))) {
      return `Normal and critical answers must be among the test's options: ${test.options.join(', ')}.`;
    }
    return rangeProblem(dto);
  });
  const valid = problems.every((problem) => problem === null);

  const save = useMutation({
    mutationFn: () => labApi.setRanges(test.id, converted as ReferenceRangeDto[], test.version),
    onSuccess: () => {
      setDirty(false);
      notify.success(
        'Reference ranges saved',
        'New results use them straight away; results already entered keep their flags.',
      );
      void queryClient.invalidateQueries({ queryKey: labKeys.masters });
    },
    onError: (error) => notify.error(error, 'Could not save the reference ranges'),
  });
  const serverProblems = indexedFieldErrors(save.error, 'ranges');

  const change = (key: string, patch: Partial<RangeRow>) => {
    setRows((current) => current.map((row) => (row.key === key ? { ...row, ...patch } : row)));
    setDirty(true);
    if (save.error) {
      save.reset();
    }
  };

  const add = (base?: RangeRow) => {
    const template = base ? { ...base } : toRow(emptyRange(), 0);
    setRows((current) => [...current, { ...template, key: `new-${crypto.randomUUID()}` }]);
    setDirty(true);
  };

  return (
    <div className={styles.root}>
      <Caption1 className={styles.muted}>
        {numeric
          ? 'Values outside the range are flagged H or L; beyond the critical limits they are critical and need a documented call before verification.'
          : 'List the normal answers and the critical answers. Any other answer is flagged abnormal.'}
      </Caption1>
      {save.error && serverProblems.size === 0 && (
        <MessageBar intent="error">
          <MessageBarBody>{errorMessage(save.error, 'The reference ranges could not be saved.')}</MessageBarBody>
        </MessageBar>
      )}

      {rows.length === 0 && (
        <Text>No reference range yet. Results of this test cannot be flagged until one is added.</Text>
      )}

      {rows.map((row, index) => {
        const dto = converted[index];
        const problem = problems[index] ?? serverProblems.get(index) ?? null;
        return (
          <section key={row.key} className={styles.card} aria-label={`Reference range ${index + 1}`}>
            <div className={styles.cardHead}>
              <Text weight="semibold">
                {typeof dto === 'string' || !dto ? `Range ${index + 1}` : rangeSummary(dto)}
              </Text>
              <span>
                <Button
                  size="small"
                  appearance="subtle"
                  icon={<CopyRegular />}
                  onClick={() => add(row)}
                  aria-label={`Copy range ${index + 1}`}
                >
                  Copy
                </Button>
                <Button
                  size="small"
                  appearance="subtle"
                  icon={<DeleteRegular />}
                  aria-label={`Remove range ${index + 1}`}
                  onClick={() => {
                    setRows((current) => current.filter((r) => r.key !== row.key));
                    setDirty(true);
                  }}
                />
              </span>
            </div>
            <div className={styles.grid}>
              <Field label="Sex">
                <Select
                  value={row.gender ?? ''}
                  onChange={(_, data) => change(row.key, { gender: (data.value || null) as Gender | null })}
                >
                  <option value="">Any</option>
                  <option value="Male">Male</option>
                  <option value="Female">Female</option>
                </Select>
              </Field>
              <Field label="From age">
                <div className={styles.age}>
                  <Input
                    value={row.fromValue}
                    onChange={(_, data) => change(row.key, { fromValue: data.value })}
                    inputMode="numeric"
                    style={{ width: 70 }}
                    aria-label="From age"
                  />
                  <Select
                    value={row.fromUnit}
                    onChange={(_, data) => change(row.key, { fromUnit: data.value as AgeUnit })}
                    aria-label="From age unit"
                  >
                    {UNITS.map((unit) => (
                      <option key={unit.value} value={unit.value}>
                        {unit.label}
                      </option>
                    ))}
                  </Select>
                </div>
              </Field>
              <Field label="Below age" hint="Empty: no upper limit">
                <div className={styles.age}>
                  <Input
                    value={row.toValue}
                    onChange={(_, data) => change(row.key, { toValue: data.value })}
                    inputMode="numeric"
                    style={{ width: 70 }}
                    aria-label="Below age"
                  />
                  <Select
                    value={row.toUnit}
                    onChange={(_, data) => change(row.key, { toUnit: data.value as AgeUnit })}
                    aria-label="Below age unit"
                  >
                    {UNITS.map((unit) => (
                      <option key={unit.value} value={unit.value}>
                        {unit.label}
                      </option>
                    ))}
                  </Select>
                </div>
              </Field>
              {numeric && (
                <>
                  <Field label={`Low${test.unit ? ` (${test.unit})` : ''}`}>
                    <Input
                      value={row.low}
                      onChange={(_, data) => change(row.key, { low: data.value })}
                      inputMode="decimal"
                    />
                  </Field>
                  <Field label={`High${test.unit ? ` (${test.unit})` : ''}`}>
                    <Input
                      value={row.high}
                      onChange={(_, data) => change(row.key, { high: data.value })}
                      inputMode="decimal"
                    />
                  </Field>
                  <Field label="Critical below">
                    <Input
                      value={row.criticalLow}
                      onChange={(_, data) => change(row.key, { criticalLow: data.value })}
                      inputMode="decimal"
                    />
                  </Field>
                  <Field label="Critical above">
                    <Input
                      value={row.criticalHigh}
                      onChange={(_, data) => change(row.key, { criticalHigh: data.value })}
                      inputMode="decimal"
                    />
                  </Field>
                </>
              )}
              {!numeric && (
                <>
                  <Field label="Normal answers" hint="Comma separated" className={styles.wide}>
                    <Input
                      value={row.normalValues}
                      onChange={(_, data) => change(row.key, { normalValues: data.value })}
                      placeholder="e.g. Negative, Non-reactive"
                    />
                  </Field>
                  <Field label="Critical answers" hint="Comma separated" className={styles.wide}>
                    <Input
                      value={row.criticalValues}
                      onChange={(_, data) => change(row.key, { criticalValues: data.value })}
                      placeholder="e.g. Reactive, P. falciparum seen"
                    />
                  </Field>
                </>
              )}
              <Field
                label="Printed text"
                hint="Optional; replaces the computed range on the report"
                className={styles.wide}
              >
                <Input
                  value={row.text}
                  onChange={(_, data) => change(row.key, { text: data.value.slice(0, 500) })}
                  placeholder="e.g. Desirable: below 200"
                />
              </Field>
            </div>
            {problem && (
              <Caption1 className={styles.problem} role="alert">
                {problem}
              </Caption1>
            )}
          </section>
        );
      })}

      <div className={styles.footer}>
        <Button icon={<AddRegular />} onClick={() => add()}>
          Add range
        </Button>
        <Button
          appearance="primary"
          icon={save.isPending ? <Spinner size="tiny" /> : <SaveRegular />}
          disabled={!dirty || !valid || save.isPending || rows.length > 50}
          onClick={() => save.mutate()}
        >
          Save ranges
        </Button>
      </div>
    </div>
  );
}
