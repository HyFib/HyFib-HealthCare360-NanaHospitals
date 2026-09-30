import { Field, Input, Select } from '@fluentui/react-components';
import { PERIOD_PRESETS, periodPresetLabel, type PeriodPreset } from './rules';

export interface PeriodPatch {
  preset?: PeriodPreset;
  from?: string;
  to?: string;
}

/** Period filter: a preset, or "Choose dates" with a from/to pair. */
export function PeriodFields({
  preset,
  from,
  to,
  onChange,
}: {
  preset: PeriodPreset;
  /** The dates in effect (shown when choosing dates). */
  from: string;
  to: string;
  onChange: (patch: PeriodPatch) => void;
}) {
  return (
    <>
      <Field label="Period">
        <Select
          value={preset}
          onChange={(_, d) => {
            const next = d.value as PeriodPreset;
            // Start "Choose dates" from the dates currently shown.
            onChange(next === 'custom' ? { preset: next, from, to } : { preset: next, from: undefined, to: undefined });
          }}
        >
          {PERIOD_PRESETS.map((p) => (
            <option key={p} value={p}>
              {periodPresetLabel[p]}
            </option>
          ))}
        </Select>
      </Field>
      {preset === 'custom' && (
        <>
          <Field label="From">
            <Input
              type="date"
              value={from}
              max={to}
              onChange={(_, d) => d.value && onChange({ preset, from: d.value, to })}
            />
          </Field>
          <Field label="To">
            <Input
              type="date"
              value={to}
              min={from}
              onChange={(_, d) => d.value && onChange({ preset, from, to: d.value })}
            />
          </Field>
        </>
      )}
    </>
  );
}
