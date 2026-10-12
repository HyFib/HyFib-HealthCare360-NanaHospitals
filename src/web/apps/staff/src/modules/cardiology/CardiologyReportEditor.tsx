import {
  Button,
  Caption1,
  makeStyles,
  MessageBar,
  MessageBarActions,
  MessageBarBody,
  MessageBarTitle,
  Spinner,
  Subtitle2,
  Switch,
  Text,
  tokens,
} from '@fluentui/react-components';
import {
  CalculatorRegular,
  CheckmarkCircleRegular,
  DocumentArrowLeftRegular,
  SaveRegular,
  SendRegular,
  SignatureRegular,
  WarningRegular,
} from '@fluentui/react-icons';
import { zodResolver } from '@hookform/resolvers/zod';
import { formatDateTime, formatRelative, isApiError, useHotkey } from '@hc360/core';
import {
  applyServerErrors,
  EmptyState,
  FormGrid,
  FullRow,
  LEAVE_KEEPING_DRAFT,
  NumberField,
  Section,
  SelectField,
  SwitchField,
  TextAreaField,
  TextField,
  useLeaveGuard,
  useNotify,
} from '@hc360/ui';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { Fragment, useState } from 'react';
import { useForm, useWatch, type UseFormReturn } from 'react-hook-form';
import { z } from 'zod';
import { DocumentButtons } from '@/lib/DocumentButtons';
import { usePermission } from '@/lib/permissions';
import { useFormDraft } from '@/lib/useFormDraft';
import { parseAgeYears } from '@/modules/lab/common/banner';
import { CriticalCallDialog } from '@/modules/lab/common/CriticalCallDialog';
import { hasErrorCode } from '@/modules/lab/common/errors';
import type { CriticalCallDetails } from '@/modules/lab/common/types';
import {
  CARDIOLOGY_PERMISSIONS,
  cardiologyApi,
  cardiologyKeys,
  cardiologyReportDocument,
  TMT_RESULTS,
  type CardiologyReportDto,
  type CardiologyStudyDetail,
  type SaveCardiologyReportRequest,
} from './api';
import { reportStatusLabel, studyTypeLongLabel, tmtResultLabel } from './labels';
import {
  bazettQtc,
  dippingPattern,
  formatDuration,
  heartRateHint,
  lvefCategory,
  parseDuration,
  qtcHint,
  targetHeartRatePercent,
} from './measurements';
import { measurementRows } from './reportBody';
import { isCardiologyReportable, isCardiologyReportEditable } from './workflow';

const int = (min: number, max: number) =>
  z.number().int('Whole numbers only.').min(min, `${min} to ${max}`).max(max, `${min} to ${max}`).nullable();
const dec = (min: number, max: number) =>
  z.number().min(min, `${min} to ${max}`).max(max, `${min} to ${max}`).nullable();
const text = (max: number) => z.string().max(max);

const schema = z
  .object({
    ecg: z.object({
      heartRate: int(10, 350),
      rhythm: text(100),
      prInterval: int(40, 600),
      qrsDuration: int(20, 400),
      qtInterval: int(100, 900),
      qtcInterval: int(100, 900),
      axis: text(64),
      stTChanges: text(500),
      interpretation: text(2000),
    }),
    echo: z.object({
      lvef: dec(1, 95),
      leftVentricle: text(500),
      rightVentricle: text(500),
      leftAtrium: text(300),
      rightAtrium: text(300),
      valves: text(1000),
      rwma: text(500),
      diastolicFunction: text(300),
      pericardium: text(300),
      conclusion: text(2000),
    }),
    tmt: z.object({
      protocol: text(64),
      duration: z.string().refine((v) => !v.trim() || parseDuration(v) !== null, 'Use minutes:seconds, e.g. 9:30'),
      mets: dec(0, 30),
      maxHeartRate: int(30, 250),
      targetHeartRatePercent: int(0, 150),
      bpResponse: text(300),
      symptoms: text(300),
      stChanges: text(500),
      reasonForTermination: text(300),
      result: z.enum(TMT_RESULTS).nullable(),
    }),
    holter: z.object({
      durationHours: int(1, 336),
      minHeartRate: int(10, 350),
      meanHeartRate: int(10, 350),
      maxHeartRate: int(10, 350),
      ventricularEctopics: int(0, 1_000_000),
      supraventricularEctopics: int(0, 1_000_000),
      pauses: int(0, 100_000),
      longestPause: text(64),
      arrhythmias: text(1000),
      stChanges: text(500),
    }),
    abpm: z.object({
      daytimeMeanSystolic: int(40, 300),
      daytimeMeanDiastolic: int(20, 200),
      nighttimeMeanSystolic: int(40, 300),
      nighttimeMeanDiastolic: int(20, 200),
      dippingPattern: text(64),
    }),
    conclusion: text(2000),
    recommendations: text(1000),
    isCriticalFinding: z.boolean(),
    criticalFindingNote: text(500),
  })
  .superRefine((values, ctx) => {
    if (values.isCriticalFinding && !values.criticalFindingNote.trim()) {
      ctx.addIssue({
        code: 'custom',
        path: ['criticalFindingNote'],
        message: 'Describe the critical finding in one line.',
      });
    }
  });

type ReportValues = z.infer<typeof schema>;

const s = (value: string | null | undefined) => value ?? '';
const n = (value: string) => (value.trim() ? value.trim() : null);

function toValues(report: CardiologyReportDto | null): ReportValues {
  return {
    ecg: {
      heartRate: report?.ecg.heartRate ?? null,
      rhythm: s(report?.ecg.rhythm),
      prInterval: report?.ecg.prInterval ?? null,
      qrsDuration: report?.ecg.qrsDuration ?? null,
      qtInterval: report?.ecg.qtInterval ?? null,
      qtcInterval: report?.ecg.qtcInterval ?? null,
      axis: s(report?.ecg.axis),
      stTChanges: s(report?.ecg.stTChanges),
      interpretation: s(report?.ecg.interpretation),
    },
    echo: {
      lvef: report?.echo.lvef ?? null,
      leftVentricle: s(report?.echo.leftVentricle),
      rightVentricle: s(report?.echo.rightVentricle),
      leftAtrium: s(report?.echo.leftAtrium),
      rightAtrium: s(report?.echo.rightAtrium),
      valves: s(report?.echo.valves),
      rwma: s(report?.echo.rwma),
      diastolicFunction: s(report?.echo.diastolicFunction),
      pericardium: s(report?.echo.pericardium),
      conclusion: s(report?.echo.conclusion),
    },
    tmt: {
      protocol: s(report?.tmt.protocol),
      duration: formatDuration(report?.tmt.durationSeconds),
      mets: report?.tmt.mets ?? null,
      maxHeartRate: report?.tmt.maxHeartRate ?? null,
      targetHeartRatePercent: report?.tmt.targetHeartRatePercent ?? null,
      bpResponse: s(report?.tmt.bpResponse),
      symptoms: s(report?.tmt.symptoms),
      stChanges: s(report?.tmt.stChanges),
      reasonForTermination: s(report?.tmt.reasonForTermination),
      result: report?.tmt.result ?? null,
    },
    holter: {
      durationHours: report?.holter.durationHours ?? null,
      minHeartRate: report?.holter.minHeartRate ?? null,
      meanHeartRate: report?.holter.meanHeartRate ?? null,
      maxHeartRate: report?.holter.maxHeartRate ?? null,
      ventricularEctopics: report?.holter.ventricularEctopics ?? null,
      supraventricularEctopics: report?.holter.supraventricularEctopics ?? null,
      pauses: report?.holter.pauses ?? null,
      longestPause: s(report?.holter.longestPause),
      arrhythmias: s(report?.holter.arrhythmias),
      stChanges: s(report?.holter.stChanges),
    },
    abpm: {
      daytimeMeanSystolic: report?.abpm.daytimeMeanSystolic ?? null,
      daytimeMeanDiastolic: report?.abpm.daytimeMeanDiastolic ?? null,
      nighttimeMeanSystolic: report?.abpm.nighttimeMeanSystolic ?? null,
      nighttimeMeanDiastolic: report?.abpm.nighttimeMeanDiastolic ?? null,
      dippingPattern: s(report?.abpm.dippingPattern),
    },
    conclusion: s(report?.conclusion),
    recommendations: s(report?.recommendations),
    isCriticalFinding: report?.isCriticalFinding ?? false,
    criticalFindingNote: s(report?.criticalFindingNote),
  };
}

/** Only the section of the study type is sent; the other measurement sets stay untouched on the server. */
function toRequest(
  study: CardiologyStudyDetail,
  values: ReportValues,
  version: number | null,
): SaveCardiologyReportRequest {
  const v = values;
  const type = study.studyType;
  return {
    ecg:
      type === 'Ecg'
        ? {
            ...v.ecg,
            rhythm: n(v.ecg.rhythm),
            axis: n(v.ecg.axis),
            stTChanges: n(v.ecg.stTChanges),
            interpretation: n(v.ecg.interpretation),
          }
        : null,
    echo:
      type === 'Echo'
        ? {
            lvef: v.echo.lvef,
            leftVentricle: n(v.echo.leftVentricle),
            rightVentricle: n(v.echo.rightVentricle),
            leftAtrium: n(v.echo.leftAtrium),
            rightAtrium: n(v.echo.rightAtrium),
            valves: n(v.echo.valves),
            rwma: n(v.echo.rwma),
            diastolicFunction: n(v.echo.diastolicFunction),
            pericardium: n(v.echo.pericardium),
            conclusion: n(v.echo.conclusion),
          }
        : null,
    tmt:
      type === 'Tmt'
        ? {
            protocol: n(v.tmt.protocol),
            durationSeconds: parseDuration(v.tmt.duration),
            mets: v.tmt.mets,
            maxHeartRate: v.tmt.maxHeartRate,
            targetHeartRatePercent: v.tmt.targetHeartRatePercent,
            bpResponse: n(v.tmt.bpResponse),
            symptoms: n(v.tmt.symptoms),
            stChanges: n(v.tmt.stChanges),
            reasonForTermination: n(v.tmt.reasonForTermination),
            result: v.tmt.result,
          }
        : null,
    holter:
      type === 'Holter'
        ? {
            ...v.holter,
            longestPause: n(v.holter.longestPause),
            arrhythmias: n(v.holter.arrhythmias),
            stChanges: n(v.holter.stChanges),
          }
        : null,
    abpm: type === 'Abpm' ? { ...v.abpm, dippingPattern: n(v.abpm.dippingPattern) } : null,
    conclusion: n(v.conclusion),
    recommendations: n(v.recommendations),
    isCriticalFinding: v.isCriticalFinding,
    criticalFindingNote: v.isCriticalFinding ? n(v.criticalFindingNote) : null,
    version,
  };
}

const useStyles = makeStyles({
  form: { display: 'flex', flexDirection: 'column', gap: '16px' },
  group: { display: 'flex', flexDirection: 'column', gap: '10px' },
  suggestions: { display: 'flex', gap: '6px', flexWrap: 'wrap', marginTop: '6px' },
  calc: { display: 'flex', gap: '8px', alignItems: 'flex-end' },
  footer: {
    position: 'sticky',
    bottom: 0,
    zIndex: 1,
    display: 'flex',
    gap: '8px',
    flexWrap: 'wrap',
    alignItems: 'center',
    justifyContent: 'space-between',
    padding: '10px 12px',
    backgroundColor: tokens.colorNeutralBackground1,
    borderTop: `1px solid ${tokens.colorNeutralStroke2}`,
    boxShadow: tokens.shadow8,
    borderRadius: tokens.borderRadiusLarge,
  },
  actions: { display: 'flex', gap: '8px', flexWrap: 'wrap', alignItems: 'center' },
  muted: { color: tokens.colorNeutralForeground3 },
  rows: { display: 'grid', gridTemplateColumns: 'minmax(160px, 240px) minmax(0, 1fr)', gap: '6px 16px' },
  text: { whiteSpace: 'pre-wrap' },
  strong: { whiteSpace: 'pre-wrap', fontWeight: tokens.fontWeightSemibold },
});

const RHYTHMS = [
  'Sinus rhythm',
  'Sinus tachycardia',
  'Sinus bradycardia',
  'Atrial fibrillation',
  'Atrial flutter',
  'Paced rhythm',
];
const AXES = ['Normal', 'Left axis deviation', 'Right axis deviation', 'Extreme axis deviation'];
const PROTOCOLS = ['Bruce', 'Modified Bruce'];

/** Quick-pick buttons that fill a free-text field (typing stays possible). */
function Suggestions({
  form,
  name,
  options,
}: {
  form: UseFormReturn<ReportValues>;
  name: 'ecg.rhythm' | 'ecg.axis' | 'tmt.protocol';
  options: string[];
}) {
  const styles = useStyles();
  const value = useWatch({ control: form.control, name });
  return (
    <div className={styles.suggestions}>
      {options.map((option) => (
        <Button
          key={option}
          size="small"
          shape="circular"
          appearance={value === option ? 'primary' : 'outline'}
          onClick={() => form.setValue(name, option, { shouldDirty: true })}
        >
          {option}
        </Button>
      ))}
    </div>
  );
}

/**
 * Cardiology report for a study (SRS CAR-03/04): the measurement form of the study type while it is being reported,
 * the signed report afterwards. Readers without cardiology rights see it once it is released.
 */
export function CardiologyReportPanel({ study }: { study: CardiologyStudyDetail }) {
  const canManage = usePermission(CARDIOLOGY_PERMISSIONS.manage);
  const canVerify = usePermission(CARDIOLOGY_PERMISSIONS.verify);
  if (!isCardiologyReportable(study.status)) {
    return (
      <Section title="Report">
        <EmptyState
          title="No report yet"
          description={
            study.status === 'Cancelled'
              ? 'The study was cancelled.'
              : 'The report is written after the tracing is attached and the study is completed.'
          }
        />
      </Section>
    );
  }
  if ((canManage || canVerify) && isCardiologyReportEditable(study.report?.status, study.status)) {
    return <ReportForm key={study.report?.id ?? 'new'} study={study} canVerify={canVerify} />;
  }
  if (study.report) {
    return <SignedReport study={study} report={study.report} canVerify={canVerify} />;
  }
  return (
    <Section title="Report">
      <EmptyState
        title="Report not available yet"
        description="The report appears here once the cardiologist releases it."
      />
    </Section>
  );
}

function ReportForm({ study, canVerify }: { study: CardiologyStudyDetail; canVerify: boolean }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const studyKey = cardiologyKeys.study(study.id);
  const form = useForm<ReportValues>({ resolver: zodResolver(schema), defaultValues: toValues(study.report) });
  const draft = useFormDraft(form, `cardiology-report:${study.id}`);
  const [release, setRelease] = useState(true);
  const [calling, setCalling] = useState(false);
  const watched = useWatch({ control: form.control });
  const type = study.studyType;
  const ageYears = parseAgeYears(study.age);
  const latestVersion = () =>
    queryClient.getQueryData<CardiologyStudyDetail>(studyKey)?.report?.version ?? study.report?.version ?? null;

  const persist = async (values: ReportValues) => {
    const saved = await cardiologyApi.saveReport(study.id, toRequest(study, values, latestVersion()));
    queryClient.setQueryData(studyKey, saved);
    form.reset(values);
    draft.clear();
    return saved;
  };

  const handleError = (error: unknown, fallback: string) => {
    if (isApiError(error) && error.isConflict) {
      notify.warning(
        'The report was changed by someone else',
        'It has been reloaded. Check it and make your changes again.',
      );
      void queryClient.invalidateQueries({ queryKey: studyKey });
      return;
    }
    if (!applyServerErrors(error, form.setError)) {
      notify.error(error, fallback);
    }
  };

  const invalidate = () => void queryClient.invalidateQueries({ queryKey: cardiologyKeys.all });

  const save = useMutation({
    mutationFn: persist,
    onSuccess: () => notify.success('Report saved'),
    onError: (error) => handleError(error, 'Could not save the report'),
  });
  const submit = useMutation({
    mutationFn: async (values: ReportValues) => {
      await persist(values);
      return cardiologyApi.submitReport(study.id);
    },
    onSuccess: () => {
      notify.success('Report submitted for sign-off');
      invalidate();
    },
    onError: (error) => handleError(error, 'Could not submit the report'),
  });
  const sign = useMutation({
    mutationFn: async ({ values, call }: { values: ReportValues; call: CriticalCallDetails | null }) => {
      if (form.formState.isDirty || !study.report) {
        await persist(values);
      }
      return cardiologyApi.verifyReport(study.id, call, release);
    },
    onSuccess: (detail) => {
      setCalling(false);
      notify.success(detail.report?.status === 'Released' ? 'Report signed and released' : 'Report signed');
      invalidate();
    },
    onError: (error) => {
      if (hasErrorCode(error, 'cardiology.critical_call_required')) {
        setCalling(true);
        return;
      }
      setCalling(false);
      handleError(error, 'Could not sign the report');
    },
  });

  const busy = save.isPending || submit.isPending || sign.isPending;
  useHotkey('mod+s', () => void form.handleSubmit((values) => save.mutate(values))(), {
    allowInInputs: true,
    enabled: !busy,
  });

  const status = study.report?.status;
  const ecg = watched.ecg;
  const qtcSuggestion = bazettQtc(ecg?.qtInterval, ecg?.heartRate);
  const targetSuggestion = targetHeartRatePercent(watched.tmt?.maxHeartRate, ageYears);
  const dip = dippingPattern(watched.abpm?.daytimeMeanSystolic, watched.abpm?.nighttimeMeanSystolic);

  // Leaving the screen or closing the window with unsaved changes asks first (SRS UX-08).
  useLeaveGuard({
    dirty: form.formState.isDirty,
    busy: save.isPending || submit.isPending || sign.isPending,
    leaveQuestion: LEAVE_KEEPING_DRAFT,
  });

  return (
    <Section
      title={`${studyTypeLongLabel[type]} report`}
      description={status ? reportStatusLabel[status] : 'Not started. Enter the measurements and your conclusion.'}
    >
      <form onSubmit={(event) => event.preventDefault()} noValidate className={styles.form}>
        {draft.saved && (
          <MessageBar intent="info" icon={<DocumentArrowLeftRegular />}>
            <MessageBarBody>
              Unsaved report values from {formatRelative(new Date(draft.saved.savedAt).toISOString())} were found on
              this computer.
            </MessageBarBody>
            <MessageBarActions>
              <Button onClick={draft.restore}>Restore them</Button>
              <Button appearance="subtle" onClick={draft.discard}>
                Discard
              </Button>
            </MessageBarActions>
          </MessageBar>
        )}

        {type === 'Ecg' && (
          <section className={styles.group}>
            <Subtitle2>ECG measurements</Subtitle2>
            <FormGrid columns={3}>
              <NumberField
                control={form.control}
                name="ecg.heartRate"
                label="Heart rate (/min)"
                hint={heartRateHint(ecg?.heartRate) ?? undefined}
                min={10}
                max={350}
              />
              <NumberField control={form.control} name="ecg.prInterval" label="PR interval (ms)" min={40} max={600} />
              <NumberField control={form.control} name="ecg.qrsDuration" label="QRS duration (ms)" min={20} max={400} />
              <NumberField control={form.control} name="ecg.qtInterval" label="QT interval (ms)" min={100} max={900} />
              <div className={styles.calc}>
                <div style={{ flex: 1 }}>
                  <NumberField
                    control={form.control}
                    name="ecg.qtcInterval"
                    label="QTc (ms)"
                    hint={qtcHint(ecg?.qtcInterval, study.gender) ?? undefined}
                    min={100}
                    max={900}
                  />
                </div>
                <Button
                  icon={<CalculatorRegular />}
                  disabled={qtcSuggestion === null}
                  title="Bazett formula from QT and heart rate"
                  onClick={() =>
                    qtcSuggestion !== null && form.setValue('ecg.qtcInterval', qtcSuggestion, { shouldDirty: true })
                  }
                >
                  {qtcSuggestion !== null ? `Bazett ${qtcSuggestion}` : 'Bazett'}
                </Button>
              </div>
              <FullRow>
                <TextField control={form.control} name="ecg.rhythm" label="Rhythm" maxLength={100} />
                <Suggestions form={form} name="ecg.rhythm" options={RHYTHMS} />
              </FullRow>
              <FullRow>
                <TextField control={form.control} name="ecg.axis" label="Axis" maxLength={64} />
                <Suggestions form={form} name="ecg.axis" options={AXES} />
              </FullRow>
              <FullRow>
                <TextAreaField
                  control={form.control}
                  name="ecg.stTChanges"
                  label="ST-T changes"
                  rows={2}
                  maxLength={500}
                />
              </FullRow>
              <FullRow>
                <TextAreaField
                  control={form.control}
                  name="ecg.interpretation"
                  label="Interpretation"
                  rows={3}
                  maxLength={2000}
                />
              </FullRow>
            </FormGrid>
          </section>
        )}

        {type === 'Echo' && (
          <section className={styles.group}>
            <Subtitle2>Echo findings</Subtitle2>
            <FormGrid columns={3}>
              <NumberField
                control={form.control}
                name="echo.lvef"
                label="LV ejection fraction (%)"
                hint={lvefCategory(watched.echo?.lvef) ?? undefined}
                min={1}
                max={95}
                step={1}
              />
              <TextField
                control={form.control}
                name="echo.diastolicFunction"
                label="Diastolic function"
                maxLength={300}
              />
              <TextField control={form.control} name="echo.pericardium" label="Pericardium" maxLength={300} />
              <FullRow>
                <TextAreaField
                  control={form.control}
                  name="echo.leftVentricle"
                  label="Left ventricle"
                  rows={2}
                  maxLength={500}
                />
              </FullRow>
              <TextAreaField
                control={form.control}
                name="echo.rightVentricle"
                label="Right ventricle"
                rows={2}
                maxLength={500}
              />
              <TextAreaField
                control={form.control}
                name="echo.leftAtrium"
                label="Left atrium"
                rows={2}
                maxLength={300}
              />
              <TextAreaField
                control={form.control}
                name="echo.rightAtrium"
                label="Right atrium"
                rows={2}
                maxLength={300}
              />
              <FullRow>
                <TextAreaField control={form.control} name="echo.valves" label="Valves" rows={3} maxLength={1000} />
              </FullRow>
              <FullRow>
                <TextAreaField
                  control={form.control}
                  name="echo.rwma"
                  label="Regional wall motion"
                  rows={2}
                  maxLength={500}
                />
              </FullRow>
              <FullRow>
                <TextAreaField
                  control={form.control}
                  name="echo.conclusion"
                  label="Echo conclusion"
                  rows={2}
                  maxLength={2000}
                />
              </FullRow>
            </FormGrid>
          </section>
        )}

        {type === 'Tmt' && (
          <section className={styles.group}>
            <Subtitle2>Treadmill test</Subtitle2>
            <FormGrid columns={3}>
              <FullRow>
                <TextField control={form.control} name="tmt.protocol" label="Protocol" maxLength={64} />
                <Suggestions form={form} name="tmt.protocol" options={PROTOCOLS} />
              </FullRow>
              <TextField
                control={form.control}
                name="tmt.duration"
                label="Exercise duration (min:sec)"
                placeholder="e.g. 9:30"
              />
              <NumberField control={form.control} name="tmt.mets" label="Workload (METs)" min={0} max={30} step={0.1} />
              <NumberField
                control={form.control}
                name="tmt.maxHeartRate"
                label="Maximum heart rate (/min)"
                min={30}
                max={250}
              />
              <div className={styles.calc}>
                <div style={{ flex: 1 }}>
                  <NumberField
                    control={form.control}
                    name="tmt.targetHeartRatePercent"
                    label="Target heart rate reached (%)"
                    hint={
                      ageYears !== null
                        ? `Age-predicted maximum ${220 - ageYears}/min; 85% is the usual target`
                        : undefined
                    }
                    min={0}
                    max={150}
                  />
                </div>
                <Button
                  icon={<CalculatorRegular />}
                  disabled={targetSuggestion === null}
                  title="Maximum heart rate ÷ (220 − age)"
                  onClick={() =>
                    targetSuggestion !== null &&
                    form.setValue('tmt.targetHeartRatePercent', targetSuggestion, { shouldDirty: true })
                  }
                >
                  {targetSuggestion !== null ? `${targetSuggestion}%` : 'Calculate'}
                </Button>
              </div>
              <SelectField
                control={form.control}
                name="tmt.result"
                label="Result"
                placeholder="Choose"
                options={TMT_RESULTS.map((value) => ({ value, label: tmtResultLabel[value] }))}
              />
              <TextField
                control={form.control}
                name="tmt.bpResponse"
                label="BP response"
                maxLength={300}
                placeholder="e.g. Normal rise to 170/80"
              />
              <TextField control={form.control} name="tmt.symptoms" label="Symptoms" maxLength={300} />
              <TextField
                control={form.control}
                name="tmt.reasonForTermination"
                label="Reason for stopping"
                maxLength={300}
                placeholder="e.g. Target heart rate reached"
              />
              <FullRow>
                <TextAreaField
                  control={form.control}
                  name="tmt.stChanges"
                  label="ST changes"
                  rows={2}
                  maxLength={500}
                />
              </FullRow>
            </FormGrid>
          </section>
        )}

        {type === 'Holter' && (
          <section className={styles.group}>
            <Subtitle2>Holter monitoring</Subtitle2>
            <FormGrid columns={4}>
              <NumberField
                control={form.control}
                name="holter.durationHours"
                label="Recording (hours)"
                min={1}
                max={336}
              />
              <NumberField
                control={form.control}
                name="holter.minHeartRate"
                label="Minimum heart rate"
                min={10}
                max={350}
              />
              <NumberField
                control={form.control}
                name="holter.meanHeartRate"
                label="Mean heart rate"
                min={10}
                max={350}
              />
              <NumberField
                control={form.control}
                name="holter.maxHeartRate"
                label="Maximum heart rate"
                min={10}
                max={350}
              />
              <NumberField
                control={form.control}
                name="holter.ventricularEctopics"
                label="Ventricular ectopics"
                min={0}
              />
              <NumberField
                control={form.control}
                name="holter.supraventricularEctopics"
                label="Supraventricular ectopics"
                min={0}
              />
              <NumberField control={form.control} name="holter.pauses" label="Pauses" min={0} />
              <TextField
                control={form.control}
                name="holter.longestPause"
                label="Longest pause"
                maxLength={64}
                placeholder="e.g. 2.4 s at 03:10"
              />
              <FullRow>
                <TextAreaField
                  control={form.control}
                  name="holter.arrhythmias"
                  label="Arrhythmias"
                  rows={2}
                  maxLength={1000}
                />
              </FullRow>
              <FullRow>
                <TextAreaField
                  control={form.control}
                  name="holter.stChanges"
                  label="ST changes"
                  rows={2}
                  maxLength={500}
                />
              </FullRow>
            </FormGrid>
          </section>
        )}

        {type === 'Abpm' && (
          <section className={styles.group}>
            <Subtitle2>Ambulatory BP monitoring (mmHg)</Subtitle2>
            <FormGrid columns={4}>
              <NumberField
                control={form.control}
                name="abpm.daytimeMeanSystolic"
                label="Daytime systolic"
                min={40}
                max={300}
              />
              <NumberField
                control={form.control}
                name="abpm.daytimeMeanDiastolic"
                label="Daytime diastolic"
                min={20}
                max={200}
              />
              <NumberField
                control={form.control}
                name="abpm.nighttimeMeanSystolic"
                label="Night-time systolic"
                min={40}
                max={300}
              />
              <NumberField
                control={form.control}
                name="abpm.nighttimeMeanDiastolic"
                label="Night-time diastolic"
                min={20}
                max={200}
              />
              <FullRow>
                <div className={styles.calc}>
                  <div style={{ flex: 1 }}>
                    <TextField
                      control={form.control}
                      name="abpm.dippingPattern"
                      label="Dipping pattern"
                      maxLength={64}
                    />
                  </div>
                  <Button
                    icon={<CalculatorRegular />}
                    disabled={dip === null}
                    onClick={() =>
                      dip &&
                      form.setValue('abpm.dippingPattern', `${dip.pattern} (${dip.percent}% nocturnal fall)`, {
                        shouldDirty: true,
                      })
                    }
                  >
                    {dip ? `${dip.pattern}, ${dip.percent}%` : 'Calculate'}
                  </Button>
                </div>
              </FullRow>
            </FormGrid>
          </section>
        )}

        <section className={styles.group}>
          <Subtitle2>Conclusion</Subtitle2>
          <TextAreaField control={form.control} name="conclusion" label="Conclusion" rows={3} maxLength={2000} />
          <TextAreaField
            control={form.control}
            name="recommendations"
            label="Recommendations"
            rows={2}
            maxLength={1000}
          />
          <SwitchField
            control={form.control}
            name="isCriticalFinding"
            label="Critical finding (the treating doctor must be called before sign-off)"
          />
          {watched.isCriticalFinding && (
            <TextField
              control={form.control}
              name="criticalFindingNote"
              label="Critical finding in one line"
              required
              maxLength={500}
              placeholder="e.g. Acute anterior STEMI pattern"
            />
          )}
        </section>

        <div className={styles.footer}>
          <Caption1 className={styles.muted}>
            {form.formState.isDirty ? 'Unsaved changes are kept on this computer · Ctrl+S saves' : 'Saved'}
          </Caption1>
          <div className={styles.actions}>
            <Button
              icon={save.isPending ? <Spinner size="tiny" aria-hidden /> : <SaveRegular />}
              disabled={busy}
              onClick={() => void form.handleSubmit((values) => save.mutate(values))()}
              aria-keyshortcuts="Control+S"
            >
              Save
            </Button>
            {status !== 'Reported' && (
              <Button
                icon={submit.isPending ? <Spinner size="tiny" aria-hidden /> : <SendRegular />}
                disabled={busy}
                onClick={() => void form.handleSubmit((values) => submit.mutate(values))()}
              >
                Submit for sign-off
              </Button>
            )}
            {canVerify && (
              <>
                <Switch checked={release} onChange={(_, data) => setRelease(data.checked)} label="Release now" />
                <Button
                  appearance="primary"
                  icon={sign.isPending ? <Spinner size="tiny" aria-hidden /> : <SignatureRegular />}
                  disabled={busy}
                  onClick={() => void form.handleSubmit((values) => sign.mutate({ values, call: null }))()}
                >
                  {release ? 'Sign and release' : 'Sign'}
                </Button>
              </>
            )}
          </div>
        </div>
      </form>
      {calling && (
        <CriticalCallDialog
          open
          title="Inform the doctor about the critical finding"
          findings={[
            {
              id: study.id,
              label: `${study.studyName}: ${form.getValues('criticalFindingNote') || 'critical finding'}`,
            },
          ]}
          submitLabel={release ? 'Save call, sign and release' : 'Save call and sign'}
          busy={sign.isPending}
          onCancel={() => setCalling(false)}
          onSubmit={(call) => sign.mutate({ values: form.getValues(), call })}
        />
      )}
    </Section>
  );
}

function SignedReport({
  study,
  report,
  canVerify,
}: {
  study: CardiologyStudyDetail;
  report: CardiologyReportDto;
  canVerify: boolean;
}) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [calling, setCalling] = useState(false);
  const unsigned = report.status === 'Draft' || report.status === 'Reported';
  const rows = measurementRows(study.studyType, report);
  const invalidate = () => void queryClient.invalidateQueries({ queryKey: cardiologyKeys.all });

  const sign = useMutation({
    mutationFn: (call: CriticalCallDetails | null) => cardiologyApi.verifyReport(study.id, call, true),
    onSuccess: () => {
      setCalling(false);
      notify.success('Report signed and released');
      invalidate();
    },
    onError: (error) => {
      if (hasErrorCode(error, 'cardiology.critical_call_required')) {
        setCalling(true);
        return;
      }
      setCalling(false);
      notify.error(error, 'Could not sign the report');
    },
  });

  const release = useMutation({
    mutationFn: () => cardiologyApi.releaseReport(study.id),
    onSuccess: () => {
      notify.success('Report released', 'The doctor and the patient are informed.');
      invalidate();
    },
    onError: (error) => notify.error(error, 'Could not release the report'),
  });

  const signedLine = [
    report.reportedBy ? `Reported by ${report.reportedBy}` : null,
    report.verifiedBy
      ? `signed by ${report.verifiedBy}${report.verifiedAt ? ` on ${formatDateTime(report.verifiedAt)}` : ''}`
      : null,
    report.releasedAt ? `released ${formatDateTime(report.releasedAt)}` : null,
  ]
    .filter(Boolean)
    .join(', ');

  return (
    <Section
      title={`${studyTypeLongLabel[study.studyType]} report`}
      description={signedLine || reportStatusLabel[report.status]}
      actions={
        <>
          {canVerify && unsigned && (
            <Button
              appearance="primary"
              icon={sign.isPending ? <Spinner size="tiny" aria-hidden /> : <SignatureRegular />}
              disabled={sign.isPending}
              onClick={() => sign.mutate(null)}
            >
              Sign and release
            </Button>
          )}
          {canVerify && report.status === 'Verified' && (
            <Button
              appearance="primary"
              icon={release.isPending ? <Spinner size="tiny" aria-hidden /> : <CheckmarkCircleRegular />}
              disabled={release.isPending}
              onClick={() => release.mutate()}
            >
              Release
            </Button>
          )}
          <DocumentButtons path={cardiologyReportDocument(study.id)} />
        </>
      }
    >
      {report.isCriticalFinding && (
        <MessageBar intent="error" icon={<WarningRegular />} style={{ marginBottom: 12 }}>
          <MessageBarBody>
            <MessageBarTitle>Critical finding</MessageBarTitle>
            {report.criticalFindingNote}
          </MessageBarBody>
        </MessageBar>
      )}
      {rows.length > 0 && (
        <div className={styles.rows}>
          {rows.map(([label, value]) => (
            <Fragment key={label}>
              <Caption1 className={styles.muted}>{label}</Caption1>
              <Text className={styles.text}>{value}</Text>
            </Fragment>
          ))}
        </div>
      )}
      {report.conclusion && (
        <div style={{ marginTop: 12 }}>
          <Subtitle2 as="h3">Conclusion</Subtitle2>
          <Text block className={styles.strong}>
            {report.conclusion}
          </Text>
        </div>
      )}
      {report.recommendations && (
        <div style={{ marginTop: 12 }}>
          <Subtitle2 as="h3">Recommendations</Subtitle2>
          <Text block className={styles.text}>
            {report.recommendations}
          </Text>
        </div>
      )}
      {calling && (
        <CriticalCallDialog
          open
          title="Inform the doctor about the critical finding"
          findings={[
            { id: study.id, label: `${study.studyName}: ${report.criticalFindingNote ?? 'critical finding'}` },
          ]}
          submitLabel="Save call, sign and release"
          busy={sign.isPending}
          onCancel={() => setCalling(false)}
          onSubmit={(call) => sign.mutate(call)}
        />
      )}
    </Section>
  );
}
