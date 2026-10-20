import {
  Badge,
  Button,
  Caption1,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Field,
  makeStyles,
  MessageBar,
  MessageBarBody,
  Spinner,
  Table,
  TableBody,
  TableCell,
  TableHeader,
  TableHeaderCell,
  TableRow,
  Text,
  Textarea,
  tokens,
} from '@fluentui/react-components';
import { CheckmarkRegular, OpenRegular, PersonRegular } from '@fluentui/react-icons';
import { formatDateTime, isApiError } from '@hc360/core';
import {
  CloseRequestScope,
  ErrorState,
  LoadingState,
  Section,
  useCloseRequests,
  useNotify,
  useUnsavedChanges,
} from '@hc360/ui';
import { useMutation, useQuery, useQueryClient, type UseQueryResult } from '@tanstack/react-query';
import { useNavigate } from '@tanstack/react-router';
import { useState, type ReactNode } from 'react';
import { DocumentButtons } from '@/lib/DocumentButtons';
import { usePermission } from '@/lib/permissions';
import { cardiologyQueries } from '@/modules/cardiology/api';
import { CardiologyReportPanel } from '@/modules/cardiology/CardiologyReportEditor';
import { pacsState } from '@/modules/cardiology/pacs';
import { PacsButtons } from '@/modules/cardiology/PacsButtons';
import { labReportDocument } from '@/modules/lab/api';
import type { ReportStudyRef } from '@/modules/lab/common/types';
import { RADIOLOGY_PERMISSIONS, radiologyQueries } from '@/modules/radiology/api';
import { RadiologyReportPanel } from '@/modules/radiology/ReportEditor';
import { StudyImages } from '@/modules/radiology/StudyImages';
import { useStudyActions } from '@/modules/radiology/useStudyActions';
import { emrApi, emrKeys, emrQueries, type DiagnosticKind, type LabResultFlag, type WorklistResult } from './api';
import { isRestricted } from './hooks';
import { useResultReaders, type ReleasedResult } from './results';

const useStyles = makeStyles({
  body: { display: 'flex', flexDirection: 'column', gap: '12px' },
  head: { display: 'flex', gap: '8px', alignItems: 'center', flexWrap: 'wrap' },
  muted: { color: tokens.colorNeutralForeground3 },
  abnormal: { fontWeight: tokens.fontWeightSemibold, color: tokens.colorPaletteMarigoldForeground1 },
  critical: { fontWeight: tokens.fontWeightBold, color: tokens.colorPaletteRedForeground1 },
});

const FLAG_TEXT: Record<LabResultFlag, string> = {
  Normal: '',
  Low: 'Low',
  High: 'High',
  CriticalLow: 'Critical low',
  CriticalHigh: 'Critical high',
  Abnormal: 'Abnormal',
  Critical: 'Critical',
};

const DEPARTMENT: Record<DiagnosticKind, string> = {
  Laboratory: 'lab',
  Radiology: 'radiology',
  Cardiology: 'cardiology',
};

/**
 * Why a report cannot be shown: a restricted record; a report the department does not show here (the record lists
 * every facility's results, the departments their own facility's); or an error to retry.
 */
function ReportProblem({ kind, error, onRetry }: { kind: DiagnosticKind; error: unknown; onRetry: () => void }) {
  const styles = useStyles();
  if (isRestricted(error)) {
    return (
      <Caption1 className={styles.muted}>
        This patient's record is restricted. Open the chart and ask for access to read the report.
      </Caption1>
    );
  }
  if (isApiError(error) && error.status === 404) {
    return (
      <Caption1 className={styles.muted}>
        This report cannot be opened here. It may be from another facility, or have been withdrawn; check with the{' '}
        {DEPARTMENT[kind]} department.
      </Caption1>
    );
  }
  return <ErrorState error={error} onRetry={onRetry} title="The report could not be opened" />;
}

/** The lab report's results, with the report the server prints to print or download. */
function LabResults({ reportId }: { reportId: string }) {
  const styles = useStyles();
  const report = useQuery(emrQueries.labReport(reportId));
  if (report.isPending) {
    return <LoadingState label="Loading results…" />;
  }
  if (report.error) {
    return <ReportProblem kind="Laboratory" error={report.error} onRetry={() => void report.refetch()} />;
  }
  const rows = report.data.sections.flatMap((section) =>
    section.tests.flatMap((test) => test.results.map((result) => ({ test: test.name, result }))),
  );
  return (
    <Section title="Results" actions={<DocumentButtons path={labReportDocument(reportId)} />}>
      <Table size="small" aria-label="Results">
        <TableHeader>
          <TableRow>
            <TableHeaderCell>Test</TableHeaderCell>
            <TableHeaderCell>Result</TableHeaderCell>
            <TableHeaderCell>Reference</TableHeaderCell>
          </TableRow>
        </TableHeader>
        <TableBody>
          {rows.map(({ test, result }) => {
            const flag = result.flag && result.flag !== 'Normal' ? result.flag : null;
            const critical = flag?.startsWith('Critical') ?? false;
            return (
              <TableRow key={result.resultId}>
                <TableCell>
                  {result.name}
                  {result.name !== test && <Caption1 className={styles.muted}> · {test}</Caption1>}
                </TableCell>
                <TableCell>
                  <span className={flag ? (critical ? styles.critical : styles.abnormal) : undefined}>
                    {result.value ?? '—'} {result.unit ?? ''}
                    {flag ? ` (${FLAG_TEXT[flag]})` : ''}
                  </span>
                </TableCell>
                <TableCell>{result.referenceText ?? '—'}</TableCell>
              </TableRow>
            );
          })}
        </TableBody>
      </Table>
    </Section>
  );
}

/**
 * The imaging report as the study screen shows it, with Print and Download PDF, and, to people allowed to see images,
 * the study's images and the PACS viewer once the study is linked with PACS (IMG-02).
 */
function RadiologyResult({ studyId }: { studyId: string }) {
  const styles = useStyles();
  const canSeeImages = usePermission(RADIOLOGY_PERMISSIONS.images);
  const actions = useStudyActions();
  const study = useQuery(radiologyQueries.study(studyId));
  if (study.isPending) {
    return <LoadingState label="Opening the report…" />;
  }
  if (study.error) {
    return <ReportProblem kind="Radiology" error={study.error} onRetry={() => void study.refetch()} />;
  }
  const linked = study.data.pacsLinkedAt !== null;
  return (
    <>
      <RadiologyReportPanel study={study.data} />
      {canSeeImages &&
        (linked ? (
          <StudyImages
            study={study.data}
            canLink={false}
            linking={false}
            onLink={() => undefined}
            onOpenPacsViewer={() => void actions.openImages(studyId)}
          />
        ) : (
          <Caption1 className={styles.muted}>The images are not in HC360 yet.</Caption1>
        ))}
    </>
  );
}

/** The cardiology report as the study screen shows it, and the PACS viewer once the PACS holds the recordings. */
function CardiologyResult({ studyId }: { studyId: string }) {
  const styles = useStyles();
  const study = useQuery(cardiologyQueries.study(studyId));
  if (study.isPending) {
    return <LoadingState label="Opening the report…" />;
  }
  if (study.error) {
    return <ReportProblem kind="Cardiology" error={study.error} onRetry={() => void study.refetch()} />;
  }
  return (
    <>
      <CardiologyReportPanel study={study.data} />
      {pacsState(study.data.files).stored > 0 && (
        <div className={styles.head}>
          <Caption1 className={styles.muted}>The recordings are in PACS.</Caption1>
          <PacsButtons study={study.data} canSend={false} />
        </div>
      )}
    </>
  );
}

/**
 * The study of an imaging or cardiology report, which the department screens and their endpoints go by; null for a lab
 * report, which opens by its own id.
 */
function useReportStudy(result: ReleasedResult, canOpen: boolean): UseQueryResult<ReportStudyRef> | null {
  const radiology = useQuery({
    ...radiologyQueries.reportStudy(result.reportId),
    enabled: canOpen && result.kind === 'Radiology',
  });
  const cardiology = useQuery({
    ...cardiologyQueries.reportStudy(result.reportId),
    enabled: canOpen && result.kind === 'Cardiology',
  });
  return result.kind === 'Radiology' ? radiology : result.kind === 'Cardiology' ? cardiology : null;
}

/** The released report itself, read where the dialog opens, for people who may open the department's reports. */
function ResultReport({
  result,
  study,
  canOpen,
}: {
  result: ReleasedResult;
  study: UseQueryResult<ReportStudyRef> | null;
  canOpen: boolean;
}) {
  const styles = useStyles();
  if (!canOpen) {
    return (
      <Caption1 className={styles.muted}>
        You may not open {DEPARTMENT[result.kind]} reports. Ask the {DEPARTMENT[result.kind]} department for this one.
      </Caption1>
    );
  }
  if (!study) {
    return <LabResults reportId={result.reportId} />;
  }
  if (study.isPending) {
    return <LoadingState label="Finding the report…" />;
  }
  if (study.error) {
    return <ReportProblem kind={result.kind} error={study.error} onRetry={() => void study.refetch()} />;
  }
  return result.kind === 'Radiology' ? (
    <RadiologyResult studyId={study.data.studyId} />
  ) : (
    <CardiologyResult studyId={study.data.studyId} />
  );
}

function ResultFrame({ open, onClose, children }: { open: boolean; onClose: () => void; children: ReactNode }) {
  const closing = useCloseRequests({ onClose });
  return (
    <Dialog open={open} onOpenChange={closing.onOpenChange}>
      <DialogSurface style={{ maxWidth: 760 }}>
        <CloseRequestScope value={closing}>{children}</CloseRequestScope>
      </DialogSurface>
    </Dialog>
  );
}

/** Review a released result and acknowledge it (critical results stay at the top of the worklist until then). */
export function ResultReviewDialog({ result, onClose }: { result: WorklistResult | null; onClose: () => void }) {
  return (
    <ResultFrame open={result !== null} onClose={onClose}>
      {result && <ResultBody key={result.id} result={result} review={result} onClose={onClose} />}
    </ResultFrame>
  );
}

/**
 * A released result opened from the patient's record (IMG-03): the report as its department shows it, with Print and
 * Download PDF, and for an imaging study in PACS its images and the PACS viewer (IMG-02).
 */
export function ResultDialog({ result, onClose }: { result: ReleasedResult | null; onClose: () => void }) {
  return (
    <ResultFrame open={result !== null} onClose={onClose}>
      {result && <ResultBody key={result.reportId} result={result} review={null} onClose={onClose} />}
    </ResultFrame>
  );
}

/** The result, and on the worklist (`review`) the patient, a note and Acknowledge. */
function ResultBody({
  result,
  review,
  onClose,
}: {
  result: ReleasedResult;
  review: WorklistResult | null;
  onClose: () => void;
}) {
  const styles = useStyles();
  const notify = useNotify();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const canOpen = useResultReaders()[result.kind];
  const canAcknowledge = usePermission('clinical.emr.write') && review !== null;
  const [note, setNote] = useState('');
  // Imaging and cardiology screens open by study: resolve the report id as soon as the result is shown.
  const study = useReportStudy(result, canOpen);
  const studyId = study?.data?.studyId;
  const openStudy = (id: string) => {
    if (result.kind === 'Radiology') {
      void navigate({ to: '/radiology/studies/$studyId', params: { studyId: id } });
    } else {
      void navigate({ to: '/cardiology/studies/$studyId', params: { studyId: id } });
    }
  };
  const acknowledge = useMutation({
    mutationFn: (id: string) => emrApi.acknowledgeResult(id, note.trim() || null),
    onSuccess: () => {
      notify.success('Result acknowledged');
      void queryClient.invalidateQueries({ queryKey: emrKeys.all });
      onClose();
    },
    onError: (error) => notify.error(error, 'Could not acknowledge the result'),
  });

  const guard = useUnsavedChanges({ dirty: note.trim() !== '', busy: acknowledge.isPending, onClose });

  return (
    <DialogBody>
      <DialogTitle>{result.title}</DialogTitle>
      <DialogContent className={styles.body}>
        <div className={styles.head}>
          {result.hasCriticalFindings && (
            <Badge color="danger" appearance="filled">
              Critical
            </Badge>
          )}
          <Badge appearance="outline">{result.kind}</Badge>
          {review && (
            <Text>
              {review.patientName} · <span style={{ fontFamily: tokens.fontFamilyMonospace }}>{review.uhid}</span>
            </Text>
          )}
          {result.releasedAt && (
            <Caption1 className={styles.muted}>Released {formatDateTime(result.releasedAt)}</Caption1>
          )}
        </div>
        {review && result.hasCriticalFindings && (
          <MessageBar intent="error">
            <MessageBarBody>This report has critical findings. Act on it and record what you did.</MessageBarBody>
          </MessageBar>
        )}
        <ResultReport result={result} study={study} canOpen={canOpen} />
        {canAcknowledge && (
          <Field label="Note (optional)" hint="For example: patient informed, treatment changed">
            <Textarea value={note} onChange={(_, d) => setNote(d.value.slice(0, 500))} rows={2} resize="vertical" />
          </Field>
        )}
      </DialogContent>
      <DialogActions fluid>
        {review && (
          <Button
            icon={<PersonRegular />}
            onClick={() => void navigate({ to: '/emr/patients/$patientId', params: { patientId: review.patientId } })}
          >
            Open chart
          </Button>
        )}
        {result.kind === 'Laboratory' && canOpen && (
          <Button icon={<OpenRegular />} onClick={() => void navigate({ href: `/lab/reports/${result.reportId}` })}>
            Full report
          </Button>
        )}
        {studyId && (
          <Button icon={<OpenRegular />} onClick={() => openStudy(studyId)}>
            Open study
          </Button>
        )}
        <Button onClick={guard.requestClose}>Close</Button>
        {canAcknowledge && review && (
          <Button
            appearance="primary"
            icon={acknowledge.isPending ? <Spinner size="tiny" aria-hidden /> : <CheckmarkRegular />}
            disabled={acknowledge.isPending}
            onClick={() => acknowledge.mutate(review.id)}
          >
            Acknowledge
          </Button>
        )}
      </DialogActions>
      {guard.dialogs}
    </DialogBody>
  );
}
