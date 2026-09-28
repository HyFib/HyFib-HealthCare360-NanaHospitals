import {
  Badge,
  Button,
  Caption1,
  Field,
  makeStyles,
  MessageBar,
  MessageBarActions,
  MessageBarBody,
  MessageBarTitle,
  Select,
  Spinner,
  Subtitle2,
  Switch,
  Text,
  tokens,
} from '@fluentui/react-components';
import {
  AddRegular,
  CheckmarkCircleRegular,
  DocumentArrowLeftRegular,
  PrintRegular,
  SaveRegular,
  SendRegular,
  SignatureRegular,
  WarningRegular,
} from '@fluentui/react-icons';
import { zodResolver } from '@hookform/resolvers/zod';
import { formatDateTime, formatRelative, isApiError, useHotkey } from '@hc360/core';
import { applyServerErrors, EmptyState, ErrorState, LoadingState, Section, SwitchField, TextAreaField, TextField, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState, type ReactNode } from 'react';
import { useForm, useWatch } from 'react-hook-form';
import { z } from 'zod';
import { usePermission } from '@/lib/permissions';
import { useFormDraft } from '@/lib/useFormDraft';
import { CriticalCallDialog } from '@/modules/lab/common/CriticalCallDialog';
import { hasErrorCode } from '@/modules/lab/common/errors';
import type { CriticalCallDetails } from '@/modules/lab/common/types';
import { AddendumDialog } from './AddendumDialog';
import { AiFindingsPanel } from './AiFindingsPanel';
import {
  RADIOLOGY_PERMISSIONS,
  radiologyApi,
  radiologyKeys,
  radiologyQueries,
  type ImagingStudyDetail,
  type RadiologyReportDto,
  type SaveRadiologyReportRequest,
} from './api';
import { reportStatusLabel } from './labels';
import { printRadiologyReport } from './print';
import { appendFinding, applyTemplate, isReportable, isReportEditable } from './workflow';

const useStyles = makeStyles({
  layout: {
    display: 'grid',
    gridTemplateColumns: 'minmax(0, 1fr) minmax(280px, 360px)',
    gap: '16px',
    alignItems: 'start',
    '@media (max-width: 1180px)': { gridTemplateColumns: 'minmax(0, 1fr)' },
  },
  form: { display: 'flex', flexDirection: 'column', gap: '12px' },
  templateRow: { display: 'flex', gap: '8px', alignItems: 'flex-end', flexWrap: 'wrap' },
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
  signed: { display: 'flex', flexDirection: 'column', gap: '12px' },
  text: { whiteSpace: 'pre-wrap' },
  impression: { whiteSpace: 'pre-wrap', fontWeight: tokens.fontWeightSemibold },
  muted: { color: tokens.colorNeutralForeground3 },
  addendum: { borderLeft: `3px solid ${tokens.colorNeutralStroke1}`, paddingLeft: '10px', display: 'flex', flexDirection: 'column', gap: '4px' },
});

const schema = z
  .object({
    templateId: z.string().nullable(),
    technique: z.string().max(2000),
    clinicalHistory: z.string().max(1000),
    comparison: z.string().max(500),
    findings: z.string().max(8000),
    impression: z.string().max(4000),
    isCriticalFinding: z.boolean(),
    criticalFindingNote: z.string().max(500),
  })
  .superRefine((values, ctx) => {
    if (values.isCriticalFinding && !values.criticalFindingNote.trim()) {
      ctx.addIssue({ code: 'custom', path: ['criticalFindingNote'], message: 'Describe the critical finding in one line.' });
    }
  });

type ReportValues = z.infer<typeof schema>;

function toValues(report: RadiologyReportDto | null, study: ImagingStudyDetail): ReportValues {
  return {
    templateId: report?.templateId ?? null,
    technique: report?.technique ?? '',
    clinicalHistory: report?.clinicalHistory ?? study.clinicalNotes ?? '',
    comparison: report?.comparison ?? '',
    findings: report?.findings ?? '',
    impression: report?.impression ?? '',
    isCriticalFinding: report?.isCriticalFinding ?? false,
    criticalFindingNote: report?.criticalFindingNote ?? '',
  };
}

function toRequest(values: ReportValues, version: number | null): SaveRadiologyReportRequest {
  return {
    templateId: values.templateId,
    technique: values.technique.trim() || null,
    clinicalHistory: values.clinicalHistory.trim() || null,
    comparison: values.comparison.trim() || null,
    findings: values.findings,
    impression: values.impression,
    isCriticalFinding: values.isCriticalFinding,
    criticalFindingNote: values.isCriticalFinding ? values.criticalFindingNote.trim() || null : null,
    version,
  };
}

/**
 * Radiology report for a study (SRS IMG-03/05): the editor while the report is a draft or waiting for sign-off, the
 * signed report with addenda afterwards. Readers without reporting rights see the report once it is released.
 */
export function RadiologyReportPanel({ study }: { study: ImagingStudyDetail }) {
  const canWrite = usePermission(RADIOLOGY_PERMISSIONS.write);
  const canVerify = usePermission(RADIOLOGY_PERMISSIONS.verify);
  const report = useQuery({ ...radiologyQueries.report(study.id), enabled: isReportable(study.status) });

  if (!isReportable(study.status)) {
    return (
      <Section title="Report">
        <EmptyState title="No report yet" description={study.status === 'Cancelled' ? 'The study was cancelled.' : 'The report is written after the scan is completed.'} />
      </Section>
    );
  }
  if (report.isLoading) {
    return (
      <Section title="Report">
        <LoadingState label="Opening the report…" />
      </Section>
    );
  }
  if (report.error) {
    return (
      <Section title="Report">
        <ErrorState error={report.error} onRetry={() => void report.refetch()} />
      </Section>
    );
  }

  const data = report.data ?? null;
  const reporter = canWrite || canVerify;
  if (canWrite && isReportEditable(data?.status)) {
    return <ReportForm key={data?.id ?? 'new'} study={study} report={data} canVerify={canVerify} />;
  }
  const main = data ? (
    <SignedReport study={study} report={data} canVerify={canVerify} />
  ) : (
    <Section title="Report">
      <EmptyState
        title="Report not available yet"
        description={reporter ? 'A radiologist has not signed the report yet.' : 'The report appears here once it is released.'}
      />
    </Section>
  );
  return reporter ? <ReportLayout main={main} side={<AiFindingsPanel studyId={study.id} canReview />} /> : main;
}

function ReportLayout({ main, side }: { main: ReactNode; side: ReactNode }) {
  const styles = useStyles();
  return (
    <div className={styles.layout}>
      <div style={{ minWidth: 0 }}>{main}</div>
      <aside>{side}</aside>
    </div>
  );
}

function ReportForm({ study, report, canVerify }: { study: ImagingStudyDetail; report: RadiologyReportDto | null; canVerify: boolean }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const reportKey = radiologyKeys.report(study.id);
  const form = useForm<ReportValues>({ resolver: zodResolver(schema), defaultValues: toValues(report, study) });
  const draft = useFormDraft(form, `radiology-report:${study.id}`);
  const templates = useQuery(radiologyQueries.templates(study.modality, study.bodyPart));
  const [templateId, setTemplateId] = useState(report?.templateId ?? '');
  const [release, setRelease] = useState(true);
  const [calling, setCalling] = useState(false);
  const isCritical = useWatch({ control: form.control, name: 'isCriticalFinding' });
  const latest = () => queryClient.getQueryData<RadiologyReportDto | null>(reportKey) ?? report;

  const invalidate = () => {
    void queryClient.invalidateQueries({ queryKey: radiologyKeys.all });
  };

  /** Saves the typed report (always, so the server has exactly what is on screen) and returns it. */
  const persist = async (values: ReportValues) => {
    const saved = await radiologyApi.saveReport(study.id, toRequest(values, latest()?.version ?? null));
    queryClient.setQueryData(reportKey, saved);
    form.reset(values);
    draft.clear();
    return saved;
  };

  const handleError = (error: unknown, fallback: string) => {
    if (isApiError(error) && error.isConflict) {
      notify.warning('The report was changed by someone else', 'It has been reloaded. Check it and make your changes again.');
      void queryClient.invalidateQueries({ queryKey: reportKey });
      return;
    }
    if (!applyServerErrors(error, form.setError)) {
      notify.error(error, fallback);
    }
  };

  const save = useMutation({
    mutationFn: persist,
    onSuccess: () => notify.success('Draft saved'),
    onError: (error) => handleError(error, 'Could not save the report'),
  });

  const submit = useMutation({
    mutationFn: async (values: ReportValues) => {
      await persist(values);
      return radiologyApi.submitReport(study.id);
    },
    onSuccess: () => {
      notify.success('Report submitted for sign-off');
      invalidate();
    },
    onError: (error) => handleError(error, 'Could not submit the report'),
  });

  const sign = useMutation({
    mutationFn: async ({ values, call }: { values: ReportValues; call: CriticalCallDetails | null }) => {
      if (form.formState.isDirty || !latest()) {
        await persist(values);
      }
      return radiologyApi.verifyReport(study.id, call, release);
    },
    onSuccess: (signed) => {
      setCalling(false);
      notify.success(signed.status === 'Released' ? 'Report signed and released' : 'Report signed', signed.status === 'Released' ? 'The doctor and the patient are informed.' : undefined);
      invalidate();
    },
    onError: (error) => {
      if (hasErrorCode(error, 'radiology.critical_call_required')) {
        setCalling(true);
        return;
      }
      setCalling(false);
      handleError(error, 'Could not sign the report');
    },
  });

  const requireComplete = (values: ReportValues) => {
    let ok = true;
    if (!values.findings.trim()) {
      form.setError('findings', { type: 'required', message: 'Enter the findings.' });
      ok = false;
    }
    if (!values.impression.trim()) {
      form.setError('impression', { type: 'required', message: 'Enter the impression.' });
      ok = false;
    }
    return ok;
  };

  const busy = save.isPending || submit.isPending || sign.isPending;
  useHotkey('mod+s', () => void form.handleSubmit((values) => save.mutate(values))(), { allowInInputs: true, enabled: !busy });

  const applyChosenTemplate = () => {
    const template = templates.data?.find((t) => t.id === templateId);
    if (!template) {
      return;
    }
    const merged = applyTemplate(form.getValues(), template);
    form.setValue('technique', merged.technique, { shouldDirty: true });
    form.setValue('findings', merged.findings, { shouldDirty: true });
    form.setValue('impression', merged.impression, { shouldDirty: true });
    form.setValue('templateId', template.id, { shouldDirty: true });
    notify.info(`Template "${template.name}" applied`, 'Edit the text to match this study before signing.');
  };

  const status = latest()?.status;

  return (
    <ReportLayout
      main={
        <Section
          title="Report"
          description={status ? `${reportStatusLabel[status]}${latest()?.reportedBy ? ` · reported by ${latest()?.reportedBy}` : ''}` : 'Not started. Choose a template or type the findings.'}
          actions={status && <Badge appearance="tint">{reportStatusLabel[status]}</Badge>}
        >
          <form onSubmit={(event) => event.preventDefault()} noValidate className={styles.form}>
            {draft.saved && (
              <MessageBar intent="info" icon={<DocumentArrowLeftRegular />}>
                <MessageBarBody>Unsaved report text from {formatRelative(new Date(draft.saved.savedAt).toISOString())} was found on this computer.</MessageBarBody>
                <MessageBarActions>
                  <Button onClick={draft.restore}>Restore it</Button>
                  <Button appearance="subtle" onClick={draft.discard}>
                    Discard
                  </Button>
                </MessageBarActions>
              </MessageBar>
            )}

            <div className={styles.templateRow}>
              <Field label="Template" style={{ flex: '1 1 260px' }}>
                <Select value={templateId} onChange={(_, data) => setTemplateId(data.value)}>
                  <option value="">{templates.isLoading ? 'Loading…' : templates.data?.length ? 'Choose a template' : 'No templates for this modality'}</option>
                  {templates.data?.map((template) => (
                    <option key={template.id} value={template.id}>
                      {template.name}
                      {template.bodyPart ? ` (${template.bodyPart})` : ''}
                    </option>
                  ))}
                </Select>
              </Field>
              <Button icon={<AddRegular />} disabled={!templateId} onClick={applyChosenTemplate}>
                Apply template
              </Button>
            </div>

            <TextAreaField control={form.control} name="clinicalHistory" label="Clinical history" rows={2} maxLength={1000} />
            <TextAreaField control={form.control} name="technique" label="Technique" rows={2} maxLength={2000} />
            <TextField control={form.control} name="comparison" label="Comparison" maxLength={500} placeholder="e.g. Compared with CT of 12 Aug 2026" />
            <TextAreaField control={form.control} name="findings" label="Findings" rows={10} maxLength={8000} />
            <TextAreaField control={form.control} name="impression" label="Impression" rows={4} maxLength={4000} />
            <SwitchField control={form.control} name="isCriticalFinding" label="Critical finding (the treating doctor must be called before sign-off)" />
            {isCritical && <TextField control={form.control} name="criticalFindingNote" label="Critical finding in one line" required maxLength={500} placeholder="e.g. Acute left MCA infarct" />}

            <div className={styles.footer}>
              <Caption1 className={styles.muted}>{form.formState.isDirty ? 'Unsaved changes are kept on this computer · Ctrl+S saves' : 'Saved'}</Caption1>
              <div className={styles.actions}>
                <Button icon={save.isPending ? <Spinner size="tiny" /> : <SaveRegular />} disabled={busy} onClick={() => void form.handleSubmit((values) => save.mutate(values))()} aria-keyshortcuts="Control+S">
                  Save draft
                </Button>
                {status !== 'Reported' && (
                  <Button
                    icon={submit.isPending ? <Spinner size="tiny" /> : <SendRegular />}
                    appearance={canVerify ? 'secondary' : 'primary'}
                    disabled={busy}
                    onClick={() => void form.handleSubmit((values) => requireComplete(values) && submit.mutate(values))()}
                  >
                    Submit for sign-off
                  </Button>
                )}
                {canVerify && (
                  <>
                    <Switch checked={release} onChange={(_, data) => setRelease(data.checked)} label="Release now" />
                    <Button
                      appearance="primary"
                      icon={sign.isPending ? <Spinner size="tiny" /> : <SignatureRegular />}
                      disabled={busy}
                      onClick={() => void form.handleSubmit((values) => requireComplete(values) && sign.mutate({ values, call: null }))()}
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
              findings={[{ id: study.id, label: `${study.studyName}: ${form.getValues('criticalFindingNote') || 'critical finding'}` }]}
              submitLabel={release ? 'Save call, sign and release' : 'Save call and sign'}
              busy={sign.isPending}
              onCancel={() => setCalling(false)}
              onSubmit={(call) => sign.mutate({ values: form.getValues(), call })}
            />
          )}
        </Section>
      }
      side={<AiFindingsPanel studyId={study.id} canReview onInsert={(text) => form.setValue('findings', appendFinding(form.getValues('findings'), text), { shouldDirty: true })} />}
    />
  );
}

function SignedReport({ study, report, canVerify }: { study: ImagingStudyDetail; report: RadiologyReportDto; canVerify: boolean }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [addendum, setAddendum] = useState(false);
  const [printing, setPrinting] = useState(false);
  const [calling, setCalling] = useState(false);
  const unsigned = report.status === 'Draft' || report.status === 'Reported';

  const sign = useMutation({
    mutationFn: (call: CriticalCallDetails | null) => radiologyApi.verifyReport(study.id, call, true),
    onSuccess: () => {
      setCalling(false);
      notify.success('Report signed and released', 'The doctor and the patient are informed.');
      void queryClient.invalidateQueries({ queryKey: radiologyKeys.all });
    },
    onError: (error) => {
      if (hasErrorCode(error, 'radiology.critical_call_required')) {
        setCalling(true);
        return;
      }
      setCalling(false);
      notify.error(error, 'Could not sign the report');
    },
  });

  const release = useMutation({
    mutationFn: () => radiologyApi.releaseReport(study.id),
    onSuccess: () => {
      notify.success('Report released', 'The doctor and the patient are informed.');
      void queryClient.invalidateQueries({ queryKey: radiologyKeys.all });
    },
    onError: (error) => notify.error(error, 'Could not release the report'),
  });

  const print = async () => {
    setPrinting(true);
    try {
      await printRadiologyReport(await radiologyApi.printView(study.id));
    } catch (error) {
      notify.error(error, 'The report could not be printed');
    } finally {
      setPrinting(false);
    }
  };

  const block = (title: string, text: string | null, strong = false) =>
    text ? (
      <div>
        <Subtitle2 as="h3">{title}</Subtitle2>
        <Text block className={strong ? styles.impression : styles.text}>
          {text}
        </Text>
      </div>
    ) : null;

  const signedLine = [
    report.reportedBy ? `Reported by ${report.reportedBy}` : null,
    report.verifiedBy ? `signed by ${report.verifiedBy}${report.verifiedAt ? ` on ${formatDateTime(report.verifiedAt)}` : ''}` : null,
    report.releasedAt ? `released ${formatDateTime(report.releasedAt)}` : null,
  ]
    .filter(Boolean)
    .join(', ');

  return (
    <Section
      title="Report"
      description={signedLine || reportStatusLabel[report.status]}
      actions={
        <>
          {canVerify && unsigned && (
            <Button appearance="primary" icon={sign.isPending ? <Spinner size="tiny" /> : <SignatureRegular />} disabled={sign.isPending} onClick={() => sign.mutate(null)}>
              Sign and release
            </Button>
          )}
          {canVerify && report.status === 'Verified' && (
            <Button appearance="primary" icon={release.isPending ? <Spinner size="tiny" /> : <CheckmarkCircleRegular />} disabled={release.isPending} onClick={() => release.mutate()}>
              Release
            </Button>
          )}
          <Button icon={printing ? <Spinner size="tiny" /> : <PrintRegular />} disabled={printing} onClick={() => void print()}>
            Print
          </Button>
          {canVerify && (report.status === 'Verified' || report.status === 'Released') && (
            <Button icon={<AddRegular />} onClick={() => setAddendum(true)}>
              Addendum
            </Button>
          )}
        </>
      }
    >
      <div className={styles.signed}>
        {report.isCriticalFinding && (
          <MessageBar intent="error" icon={<WarningRegular />}>
            <MessageBarBody>
              <MessageBarTitle>Critical finding</MessageBarTitle>
              {report.criticalFindingNote}
            </MessageBarBody>
          </MessageBar>
        )}
        {block('Clinical history', report.clinicalHistory)}
        {block('Technique', report.technique)}
        {block('Comparison', report.comparison)}
        {block('Findings', report.findings)}
        {block('Impression', report.impression, true)}
        {report.addenda.length > 0 && (
          <div>
            <Subtitle2 as="h3">Addenda</Subtitle2>
            {report.addenda.map((item) => (
              <div key={item.id} className={styles.addendum}>
                <Caption1 className={styles.muted}>
                  {formatDateTime(item.addedAt)}
                  {item.addedBy ? ` · ${item.addedBy}` : ''}
                  {item.reason ? ` · ${item.reason}` : ''}
                  {item.isCriticalFinding ? ' · critical finding' : ''}
                </Caption1>
                <Text className={styles.text}>{item.text}</Text>
              </div>
            ))}
          </div>
        )}
      </div>
      {addendum && <AddendumDialog studyId={study.id} studyName={study.studyName} onClose={() => setAddendum(false)} />}
      {calling && (
        <CriticalCallDialog
          open
          title="Inform the doctor about the critical finding"
          findings={[{ id: study.id, label: `${study.studyName}: ${report.criticalFindingNote ?? 'critical finding'}` }]}
          submitLabel="Save call, sign and release"
          busy={sign.isPending}
          onCancel={() => setCalling(false)}
          onSubmit={(call) => sign.mutate(call)}
        />
      )}
    </Section>
  );
}
