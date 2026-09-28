import {
  Button,
  Caption1,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Field,
  Input,
  makeStyles,
  Select,
  Spinner,
  Text,
  Textarea,
  tokens,
} from '@fluentui/react-components';
import { StatusBadge, useNotify } from '@hc360/ui';
import { isApiError } from '@hc360/core';
import { useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { currentMonth, MONTH_NAMES, yearOptions } from '@/modules/hr/shared/dates';
import { qualityApi, qualityKeys, type IndicatorDto, type IndicatorValueDto } from './api';
import { formatIndicatorValue, indicatorValue, meetsTarget } from './rules';

const useStyles = makeStyles({
  row: { display: 'flex', gap: '12px', flexWrap: 'wrap' },
  preview: { display: 'flex', gap: '12px', alignItems: 'center', flexWrap: 'wrap', padding: '10px 12px', borderRadius: tokens.borderRadiusMedium, backgroundColor: tokens.colorNeutralBackground3 },
  big: { fontSize: tokens.fontSizeBase600, fontWeight: tokens.fontWeightSemibold },
  muted: { color: tokens.colorNeutralForeground3 },
});

/** The period an indicator value belongs to, chosen the way the indicator is measured. */
function defaultPeriod(frequency: IndicatorDto['frequency']): { year: number; month: number } {
  const { year, month } = currentMonth();
  if (frequency === 'Monthly') {
    return month === 1 ? { year: year - 1, month: 12 } : { year, month: month - 1 };
  }
  if (frequency === 'Quarterly') {
    const quarterStart = Math.floor((month - 1) / 3) * 3 + 1;
    return quarterStart === 1 ? { year: year - 1, month: 10 } : { year, month: quarterStart - 3 };
  }
  // Financial year starting 1 April: the last completed one.
  return { year: month >= 4 ? year - 1 : year - 2, month: 4 };
}

function periodFromValue(value: IndicatorValueDto): { year: number; month: number } {
  const [year = 2000, month = 1] = value.periodStart.split('-').map(Number);
  return { year, month };
}

/** Enter (or correct) one period's numerator and denominator; the value and target status are worked out live. */
export function IndicatorValueDialog({ open, onClose, indicator, value }: { open: boolean; onClose: () => void; indicator: IndicatorDto; value?: IndicatorValueDto | null }) {
  return (
    <Dialog open={open} onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface style={{ maxWidth: 620 }}>{open && <Body onClose={onClose} indicator={indicator} value={value ?? null} />}</DialogSurface>
    </Dialog>
  );
}

function Body({ onClose, indicator, value }: { onClose: () => void; indicator: IndicatorDto; value: IndicatorValueDto | null }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const initial = value ? periodFromValue(value) : defaultPeriod(indicator.frequency);
  const [year, setYear] = useState(initial.year);
  const [month, setMonth] = useState(initial.month);
  const [numerator, setNumerator] = useState(value ? String(value.numerator) : '');
  const [denominator, setDenominator] = useState(value ? String(value.denominator) : '');
  const [notes, setNotes] = useState(value?.notes ?? '');
  const [busy, setBusy] = useState(false);
  const [errors, setErrors] = useState<Record<string, string>>({});
  const num = numerator === '' ? null : Number(numerator);
  const den = denominator === '' ? null : Number(denominator);
  const computed = indicatorValue(num, den, indicator.unit, indicator.multiplier);
  const met = computed === null ? null : meetsTarget(computed, indicator.target, indicator.direction);
  const periodStart = `${year}-${String(month).padStart(2, '0')}-01`;

  const save = async () => {
    const next: Record<string, string> = {};
    if (num === null || Number.isNaN(num) || num < 0) next.numerator = 'Enter the numerator (0 or more).';
    if (den === null || Number.isNaN(den) || den <= 0) next.denominator = 'The denominator must be more than zero.';
    setErrors(next);
    if (Object.keys(next).length > 0 || num === null || den === null) {
      return;
    }
    setBusy(true);
    try {
      const saved = await qualityApi.recordValue(indicator.id, { periodStart, numerator: num, denominator: den, notes: notes.trim() || null });
      notify.success(`${saved.periodLabel} recorded`, `${formatIndicatorValue(saved.value, indicator.unit)}${saved.meetsTarget === false ? ' · misses the target' : ''}`);
      void queryClient.invalidateQueries({ queryKey: qualityKeys.indicators });
      void queryClient.invalidateQueries({ queryKey: qualityKeys.dashboard });
      onClose();
    } catch (error) {
      if (isApiError(error) && error.isValidation) {
        setErrors(Object.fromEntries(Object.entries(error.fieldErrors).map(([k, v]) => [k, v[0] ?? ''])));
      } else {
        notify.error(error, 'Could not record the value');
      }
    } finally {
      setBusy(false);
    }
  };

  return (
    <form
      onSubmit={(event) => {
        event.preventDefault();
        void save();
      }}
      noValidate
    >
      <DialogBody>
        <DialogTitle>
          {value ? 'Correct value' : 'Record value'}
          <Caption1 block>
            {indicator.name} ({indicator.code})
          </Caption1>
        </DialogTitle>
        <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
          <div className={styles.row}>
            {indicator.frequency === 'Monthly' && (
              <Field label="Month">
                <Select value={String(month)} onChange={(_, d) => setMonth(Number(d.value))} disabled={Boolean(value)}>
                  {MONTH_NAMES.map((name, index) => (
                    <option key={name} value={index + 1}>
                      {name}
                    </option>
                  ))}
                </Select>
              </Field>
            )}
            {indicator.frequency === 'Quarterly' && (
              <Field label="Quarter">
                <Select value={String(month)} onChange={(_, d) => setMonth(Number(d.value))} disabled={Boolean(value)}>
                  <option value="1">January to March</option>
                  <option value="4">April to June</option>
                  <option value="7">July to September</option>
                  <option value="10">October to December</option>
                </Select>
              </Field>
            )}
            <Field label={indicator.frequency === 'Annually' ? 'Financial year starting April' : 'Year'}>
              <Select value={String(year)} onChange={(_, d) => setYear(Number(d.value))} disabled={Boolean(value)}>
                {yearOptions(5, 0).map((y) => (
                  <option key={y} value={y}>
                    {indicator.frequency === 'Annually' ? `${y}–${String(y + 1).slice(2)}` : y}
                  </option>
                ))}
              </Select>
            </Field>
          </div>
          <Field label={`Numerator: ${indicator.numeratorDefinition}`} required validationMessage={errors.numerator} validationState={errors.numerator ? 'error' : 'none'}>
            <Input type="number" min={0} value={numerator} onChange={(_, d) => setNumerator(d.value)} autoFocus />
          </Field>
          <Field label={`Denominator: ${indicator.denominatorDefinition}`} required validationMessage={errors.denominator} validationState={errors.denominator ? 'error' : 'none'}>
            <Input type="number" min={0} value={denominator} onChange={(_, d) => setDenominator(d.value)} />
          </Field>
          <div className={styles.preview} role="status">
            <span className={styles.big}>{formatIndicatorValue(computed, indicator.unit)}</span>
            {met !== null && <StatusBadge status={met ? 'Met' : 'Missed'} tone={met ? 'success' : 'danger'} label={met ? 'Meets the target' : 'Misses the target'} />}
            {indicator.target !== null && (
              <Text className={styles.muted}>
                Target {formatIndicatorValue(indicator.target, indicator.unit)} ({indicator.direction === 'HigherIsBetter' ? 'higher is better' : 'lower is better'})
              </Text>
            )}
          </div>
          <Field label="Notes">
            <Textarea value={notes} onChange={(_, d) => setNotes(d.value)} rows={2} maxLength={1000} placeholder="Data source or anything unusual this period" />
          </Field>
          {!value && <Caption1 className={styles.muted}>If this period already has a value, it is replaced.</Caption1>}
        </DialogContent>
        <DialogActions>
          <Button onClick={onClose} disabled={busy}>
            Cancel
          </Button>
          <Button type="submit" appearance="primary" disabled={busy} icon={busy ? <Spinner size="tiny" /> : undefined}>
            Save value
          </Button>
        </DialogActions>
      </DialogBody>
    </form>
  );
}
