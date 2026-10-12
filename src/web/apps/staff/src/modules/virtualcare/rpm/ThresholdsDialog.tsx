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
  MessageBar,
  MessageBarBody,
  Select,
  Spinner,
  Text,
  tokens,
} from '@fluentui/react-components';
import { DeleteRegular } from '@fluentui/react-icons';
import { isApiError } from '@hc360/core';
import { CloseRequestScope, useCloseRequests, useNotify, useUnsavedChanges, useValuesChanged } from '@hc360/ui';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { READING_TYPES, rpmApi, rpmKeys, type RpmEnrollmentDetail, type RpmReadingType } from './api';
import { readingTypeInfo } from './labels';
import { describeLimits, validateLimits, type Limits } from './thresholds';

const useStyles = makeStyles({
  form: { display: 'flex', flexDirection: 'column', gap: '14px' },
  row: {
    display: 'flex',
    flexDirection: 'column',
    gap: '6px',
    padding: '10px 12px',
    borderRadius: tokens.borderRadiusLarge,
    backgroundColor: tokens.colorNeutralBackground2,
  },
  head: { display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: '8px' },
  inputs: {
    display: 'grid',
    gridTemplateColumns: 'repeat(4, minmax(0, 1fr))',
    gap: '8px',
    '@media (max-width: 600px)': { gridTemplateColumns: 'repeat(2, minmax(0, 1fr))' },
  },
  muted: { color: tokens.colorNeutralForeground3 },
  error: { color: tokens.colorPaletteRedForeground1 },
});

interface LimitRow {
  type: RpmReadingType;
  min: string;
  max: string;
  criticalMin: string;
  criticalMax: string;
}

const FIELDS: { key: keyof Limits; label: string }[] = [
  { key: 'criticalMin', label: 'Critical below' },
  { key: 'min', label: 'Normal from' },
  { key: 'max', label: 'Normal up to' },
  { key: 'criticalMax', label: 'Critical above' },
];

const toText = (value: number | null) => (value === null ? '' : String(value));
const toNumber = (value: string): number | null => (value.trim() === '' ? null : Number(value));

function limitsOf(row: LimitRow): Limits {
  return {
    min: toNumber(row.min),
    max: toNumber(row.max),
    criticalMin: toNumber(row.criticalMin),
    criticalMax: toNumber(row.criticalMax),
  };
}

/**
 * The patient's own limits (RPM-03). Readings outside the normal range raise a warning, beyond the critical limits a
 * critical alert. The programme's defaults are a starting point that the doctor should review for every patient.
 */
export function ThresholdsDialog({
  enrollment,
  onClose,
}: {
  enrollment: RpmEnrollmentDetail | null;
  onClose: () => void;
}) {
  const closing = useCloseRequests({ onClose });
  return (
    <Dialog open={enrollment !== null} onOpenChange={closing.onOpenChange}>
      <DialogSurface style={{ maxWidth: 720 }}>
        {enrollment && (
          <CloseRequestScope value={closing}>
            <ThresholdsContent
              key={`${enrollment.id}-${enrollment.version}`}
              enrollment={enrollment}
              onClose={onClose}
            />
          </CloseRequestScope>
        )}
      </DialogSurface>
    </Dialog>
  );
}

function ThresholdsContent({ enrollment, onClose }: { enrollment: RpmEnrollmentDetail; onClose: () => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [rows, setRows] = useState<LimitRow[]>(() =>
    enrollment.thresholds.map((t) => ({
      type: t.type,
      min: toText(t.min),
      max: toText(t.max),
      criticalMin: toText(t.criticalMin),
      criticalMax: toText(t.criticalMax),
    })),
  );
  const [problem, setProblem] = useState<string | null>(null);
  const available = READING_TYPES.filter((type) => !rows.some((row) => row.type === type));

  const problems = rows.map((row) => {
    const limits = limitsOf(row);
    if (Object.values(limits).some((v) => v !== null && Number.isNaN(v))) {
      return 'Enter numbers only.';
    }
    return validateLimits(limits)?.message ?? null;
  });

  const save = useMutation({
    mutationFn: () =>
      rpmApi.setThresholds(
        enrollment.id,
        rows.map((row) => ({ type: row.type, ...limitsOf(row) })),
        enrollment.version,
      ),
    onSuccess: () => {
      notify.success('Limits saved', 'New readings are checked against them.');
      void queryClient.invalidateQueries({ queryKey: rpmKeys.all });
      onClose();
    },
    onError: (error) => {
      if (isApiError(error) && error.isConflict) {
        notify.error(error, 'Someone else changed this enrolment');
        void queryClient.invalidateQueries({ queryKey: rpmKeys.enrollment(enrollment.id) });
        onClose();
        return;
      }
      setProblem(isApiError(error) ? error.userMessage : 'The limits could not be saved.');
    },
  });

  const setValue = (type: RpmReadingType, key: keyof Limits, value: string) =>
    setRows((current) => current.map((row) => (row.type === type ? { ...row, [key]: value } : row)));

  const guard = useUnsavedChanges({ dirty: useValuesChanged(rows), busy: save.isPending, onClose });

  return (
    <form
      onSubmit={(event) => {
        event.preventDefault();
        if (problems.every((p) => p === null)) {
          setProblem(null);
          save.mutate();
        }
      }}
    >
      <DialogBody>
        <DialogTitle>
          Alert limits
          <Caption1 block className={styles.muted}>
            {enrollment.patientName} · {enrollment.programName}. Leave a box empty when there is no limit on that side.
          </Caption1>
        </DialogTitle>
        <DialogContent className={styles.form}>
          {rows.length === 0 && (
            <Text className={styles.muted}>
              No limits: readings are stored but never raise alerts. Add limits below.
            </Text>
          )}
          {rows.map((row, index) => {
            const info = readingTypeInfo[row.type];
            return (
              <div key={row.type} className={styles.row}>
                <div className={styles.head}>
                  <Text weight="semibold">
                    {info.label} <Caption1 className={styles.muted}>({info.unit})</Caption1>
                  </Text>
                  <Button
                    size="small"
                    appearance="subtle"
                    icon={<DeleteRegular />}
                    aria-label={`Remove the limits for ${info.label}`}
                    onClick={() => setRows((current) => current.filter((r) => r.type !== row.type))}
                  />
                </div>
                <div className={styles.inputs}>
                  {FIELDS.map((field) => (
                    <Field key={field.key} label={field.label} size="small">
                      <Input
                        type="number"
                        inputMode="decimal"
                        size="small"
                        value={row[field.key]}
                        onChange={(_, d) => setValue(row.type, field.key, d.value)}
                      />
                    </Field>
                  ))}
                </div>
                {problems[index] ? (
                  <Caption1 className={styles.error} role="alert">
                    {problems[index]}
                  </Caption1>
                ) : (
                  <Caption1 className={styles.muted}>Normal: {describeLimits(limitsOf(row), info.unit)}</Caption1>
                )}
              </div>
            );
          })}
          {available.length > 0 && (
            <Field label="Add limits for" style={{ maxWidth: 280 }}>
              <Select
                value=""
                onChange={(_, d) => {
                  if (d.value) {
                    setRows((current) => [
                      ...current,
                      { type: d.value as RpmReadingType, min: '', max: '', criticalMin: '', criticalMax: '' },
                    ]);
                  }
                }}
              >
                <option value="">Choose a reading</option>
                {available.map((type) => (
                  <option key={type} value={type}>
                    {readingTypeInfo[type].label}
                  </option>
                ))}
              </Select>
            </Field>
          )}
          {problem && (
            <MessageBar intent="error">
              <MessageBarBody>{problem}</MessageBarBody>
            </MessageBar>
          )}
        </DialogContent>
        <DialogActions>
          <Button onClick={guard.requestClose} disabled={save.isPending}>
            Cancel
          </Button>
          <Button
            type="submit"
            appearance="primary"
            disabled={save.isPending || problems.some((p) => p !== null)}
            icon={save.isPending ? <Spinner size="tiny" aria-hidden /> : undefined}
          >
            Save limits
          </Button>
        </DialogActions>
      </DialogBody>
      {guard.dialogs}
    </form>
  );
}
