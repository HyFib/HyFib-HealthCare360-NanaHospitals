import { Field, Input } from '@fluentui/react-components';
import { todayIso } from '@hc360/core';

export interface DateRange {
  from?: string;
  to?: string;
}

/**
 * From/To date filters (hospital dates, yyyy-MM-dd). Empty values mean "today" on the server, so the fields show
 * today instead of blanks. Future dates are not offered for work lists.
 */
export function DateRangeFields({
  value,
  onChange,
  allowFuture,
}: {
  value: DateRange;
  onChange: (range: DateRange) => void;
  allowFuture?: boolean;
}) {
  const today = todayIso();
  const from = value.from ?? value.to ?? today;
  const to = value.to ?? value.from ?? today;
  return (
    <>
      <Field label="From">
        <Input
          type="date"
          value={from}
          max={allowFuture ? undefined : today}
          onChange={(_, data) =>
            onChange({
              from: data.value || undefined,
              to: value.to && data.value && data.value > value.to ? data.value : value.to,
            })
          }
        />
      </Field>
      <Field label="To">
        <Input
          type="date"
          value={to}
          min={from}
          max={allowFuture ? undefined : today}
          onChange={(_, data) => onChange({ from: value.from, to: data.value || undefined })}
        />
      </Field>
    </>
  );
}
