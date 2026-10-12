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
import { LoadingState, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient, type UseQueryResult } from '@tanstack/react-query';
import { useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { CARDIOLOGY_STUDY_READERS, cardiologyQueries } from '@/modules/cardiology/api';
import type { ReportStudyRef } from '@/modules/lab/common/types';
import { RADIOLOGY_PERMISSIONS, RADIOLOGY_STUDY_READERS, radiologyQueries } from '@/modules/radiology/api';
import { StudyThumbnails } from '@/modules/radiology/StudyImages';
import { emrApi, emrKeys, emrQueries, type LabResultFlag, type WorklistResult } from './api';

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

function LabResults({ reportId }: { reportId: string }) {
  const styles = useStyles();
  const report = useQuery(emrQueries.labReport(reportId));
  if (report.isLoading) {
    return <LoadingState label="Loading results…" />;
  }
  if (!report.data) {
    return (
      <Caption1 className={styles.muted}>
        The report could not be loaded here. Open it from the lab reports screen.
      </Caption1>
    );
  }
  const rows = report.data.sections.flatMap((section) =>
    section.tests.flatMap((test) => test.results.map((result) => ({ test: test.name, result }))),
  );
  return (
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
  );
}

/** Where the full imaging or cardiology report is: the results list holds only the report id, the screens use the study. */
function StudyReportNote({ kind, study }: { kind: 'Radiology' | 'Cardiology'; study: UseQueryResult<ReportStudyRef> }) {
  const styles = useStyles();
  const department = kind === 'Radiology' ? 'radiology' : 'cardiology';
  if (study.isPending) {
    return <LoadingState label="Finding the report…" />;
  }
  if (study.error) {
    return (
      <Caption1 className={styles.muted}>
        {isApiError(study.error) && study.error.status === 404
          ? `This report could not be found. It may have been withdrawn; check with the ${department} department.`
          : `The report could not be opened here. Open the patient's chart or the ${department} module to read it.`}
      </Caption1>
    );
  }
  return (
    <Caption1 className={styles.muted}>
      Open the full report to read the findings
      {kind === 'Radiology' ? ' and view the images' : ' and the device recordings'}.
    </Caption1>
  );
}

/** Review a released result and acknowledge it (critical results stay at the top of the worklist until then). */
export function ResultReviewDialog({ result, onClose }: { result: WorklistResult | null; onClose: () => void }) {
  return (
    <Dialog open={result !== null} onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface style={{ maxWidth: 760 }}>
        {result && <ReviewBody key={result.id} result={result} onClose={onClose} />}
      </DialogSurface>
    </Dialog>
  );
}

function ReviewBody({ result, onClose }: { result: WorklistResult; onClose: () => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const canReadLab = usePermission('lab.orders.read');
  const canReadRadiology = usePermission(RADIOLOGY_STUDY_READERS);
  const canReadCardiology = usePermission(CARDIOLOGY_STUDY_READERS);
  const canSeeImages = usePermission(RADIOLOGY_PERMISSIONS.images);
  const canAcknowledge = usePermission('clinical.emr.write');
  const [note, setNote] = useState('');
  // Radiology and cardiology screens open by study: resolve the report id as soon as the result is shown.
  const radiologyStudy = useQuery({
    ...radiologyQueries.reportStudy(result.reportId),
    enabled: result.kind === 'Radiology' && canReadRadiology,
  });
  const cardiologyStudy = useQuery({
    ...cardiologyQueries.reportStudy(result.reportId),
    enabled: result.kind === 'Cardiology' && canReadCardiology,
  });
  const study =
    result.kind === 'Radiology' && canReadRadiology
      ? { kind: 'Radiology' as const, query: radiologyStudy }
      : result.kind === 'Cardiology' && canReadCardiology
        ? { kind: 'Cardiology' as const, query: cardiologyStudy }
        : null;
  const studyId = study?.query.data?.studyId;
  const openStudy = (id: string) => {
    if (study?.kind === 'Radiology') {
      void navigate({ to: '/radiology/studies/$studyId', params: { studyId: id } });
    } else {
      void navigate({ to: '/cardiology/studies/$studyId', params: { studyId: id } });
    }
  };
  const acknowledge = useMutation({
    mutationFn: () => emrApi.acknowledgeResult(result.id, note.trim() || null),
    onSuccess: () => {
      notify.success('Result acknowledged');
      void queryClient.invalidateQueries({ queryKey: emrKeys.all });
      onClose();
    },
    onError: (error) => notify.error(error, 'Could not acknowledge the result'),
  });

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
          <Text>
            {result.patientName} · <span style={{ fontFamily: tokens.fontFamilyMonospace }}>{result.uhid}</span>
          </Text>
          <Caption1 className={styles.muted}>Released {formatDateTime(result.releasedAt)}</Caption1>
        </div>
        {result.hasCriticalFindings && (
          <MessageBar intent="error">
            <MessageBarBody>This report has critical findings. Act on it and record what you did.</MessageBarBody>
          </MessageBar>
        )}
        {result.kind === 'Laboratory' && canReadLab ? (
          <LabResults reportId={result.reportId} />
        ) : study ? (
          <>
            <StudyReportNote kind={study.kind} study={study.query} />
            {study.kind === 'Radiology' && studyId && canSeeImages && (
              <StudyThumbnails studyId={studyId} onOpen={() => openStudy(studyId)} />
            )}
          </>
        ) : (
          <Caption1 className={styles.muted}>
            Open the patient's chart or the {result.kind.toLowerCase()} module to read the full report.
          </Caption1>
        )}
        {canAcknowledge && (
          <Field label="Note (optional)" hint="For example: patient informed, treatment changed">
            <Textarea value={note} onChange={(_, d) => setNote(d.value.slice(0, 500))} rows={2} resize="vertical" />
          </Field>
        )}
      </DialogContent>
      <DialogActions fluid>
        <Button
          icon={<PersonRegular />}
          onClick={() => void navigate({ to: '/emr/patients/$patientId', params: { patientId: result.patientId } })}
        >
          Open chart
        </Button>
        {result.kind === 'Laboratory' && canReadLab && (
          <Button icon={<OpenRegular />} onClick={() => void navigate({ href: `/lab/reports/${result.reportId}` })}>
            Full report
          </Button>
        )}
        {studyId && (
          <Button icon={<OpenRegular />} onClick={() => openStudy(studyId)}>
            Full report
          </Button>
        )}
        <Button onClick={onClose}>Close</Button>
        {canAcknowledge && (
          <Button
            appearance="primary"
            icon={acknowledge.isPending ? <Spinner size="tiny" aria-hidden /> : <CheckmarkRegular />}
            disabled={acknowledge.isPending}
            onClick={() => acknowledge.mutate()}
          >
            Acknowledge
          </Button>
        )}
      </DialogActions>
    </DialogBody>
  );
}
