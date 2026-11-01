import { Button, Text } from '@fluentui/react-components';
import { CheckmarkCircleRegular, DocumentBulletListRegular, EditRegular } from '@fluentui/react-icons';
import { isApiError } from '@hc360/core';
import { ConfirmDialog, ErrorState, LoadingState, Page, PageHeader, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { DocumentButtons } from '@/lib/DocumentButtons';
import { usePermission } from '@/lib/permissions';
import { PendingApprovals } from '@/modules/approvals/PendingApprovals';
import { AbdmLinkButton } from '@/modules/emr/AbdmLinkButton';
import { PatientBannerWithFlags } from '@/modules/emr/PatientBannerWithFlags';
import { AmendReportDialog } from '@/modules/lab/AmendReportDialog';
import {
  abdmLabReportBundle,
  LAB_PERMISSIONS,
  labApi,
  labKeys,
  labQueries,
  labReportDocument,
  type LabReportView,
} from '@/modules/lab/api';
import { labReportExportable } from '@/modules/lab/common/abdm';
import { AbdmReportButton } from '@/modules/lab/common/AbdmReportButton';
import { ReportStatusBadge } from '@/modules/lab/badges';
import { LabReportPreview } from '@/modules/lab/LabReportPreview';
import { useLabLive } from '@/modules/lab/useLabLive';

export const Route = createFileRoute('/_app/lab/reports/$reportId')({
  component: LabReportPage,
});

/** Verified tests that are waiting for release. */
function releasableTests(view: LabReportView): string[] {
  return view.sections.flatMap((section) =>
    section.tests.filter((test) => test.status === 'Verified').map((test) => test.name),
  );
}

/** Tests already released (the ones an amendment can correct). */
function releasedTests(view: LabReportView): number {
  return view.sections.reduce(
    (sum, section) => sum + section.tests.filter((test) => test.status === 'Released').length,
    0,
  );
}

/**
 * The lab report with release, amendment, and the A4 report with QR verification (SRS LIS-07) to print or download as a
 * PDF: the server makes both from the same template as the copy the patient receives.
 */
function LabReportPage() {
  const { reportId } = Route.useParams();
  const notify = useNotify();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const canVerify = usePermission(LAB_PERMISSIONS.verify);
  const report = useQuery(labQueries.report(reportId));
  const [confirmRelease, setConfirmRelease] = useState(false);
  const [amending, setAmending] = useState(false);
  useLabLive();

  const release = useMutation({
    mutationFn: () => labApi.releaseReport(reportId),
    onSuccess: (outcome) => {
      setConfirmRelease(false);
      notify.success(
        outcome.releasedItemIds.length === 1 ? 'Test released' : `${outcome.releasedItemIds.length} tests released`,
        outcome.isPartial
          ? 'The report is partial: the other tests follow when they are verified.'
          : 'The doctor and the patient are informed.',
      );
      void queryClient.invalidateQueries({ queryKey: labKeys.reports });
      void queryClient.invalidateQueries({ queryKey: labKeys.orders });
    },
    onError: (error) => {
      setConfirmRelease(false);
      notify.error(error, 'Could not release the report');
    },
  });

  if (report.isLoading) {
    return <LoadingState label="Opening the report…" />;
  }
  if (report.error || !report.data) {
    return (
      <Page maxWidth={900}>
        <ErrorState
          error={report.error}
          onRetry={() => void report.refetch()}
          title={isApiError(report.error) && report.error.status === 404 ? 'Report not found' : undefined}
        />
      </Page>
    );
  }

  const view = report.data;
  const waiting = releasableTests(view);

  return (
    <Page>
      <PatientBannerWithFlags
        patientId={view.patient.id}
        patient={{
          id: view.patient.id,
          uhid: view.patient.uhid,
          fullName: view.patient.name,
          gender: view.patient.gender,
          age: view.patient.age,
        }}
        actions={
          <Button
            icon={<DocumentBulletListRegular />}
            onClick={() => void navigate({ to: '/lab/orders/$orderId', params: { orderId: view.labOrderId } })}
          >
            Order
          </Button>
        }
      />
      <PageHeader
        title={`Lab report ${view.reportNumber}`}
        subtitle={<ReportStatusBadge status={view.status} partial={view.isPartial} />}
        breadcrumbs={[
          { label: 'Lab reports', onClick: () => void navigate({ to: '/lab/reports' }) },
          { label: view.reportNumber },
        ]}
        actions={
          <>
            {canVerify && waiting.length > 0 && (
              <Button appearance="primary" icon={<CheckmarkCircleRegular />} onClick={() => setConfirmRelease(true)}>
                Release {waiting.length} {waiting.length === 1 ? 'test' : 'tests'}
              </Button>
            )}
            <DocumentButtons
              path={labReportDocument(reportId)}
              printAppearance={canVerify && waiting.length > 0 ? 'secondary' : 'primary'}
            />
            {labReportExportable(view.status) && (
              <>
                <AbdmReportButton path={abdmLabReportBundle(view.reportId)} what="lab report" />
                <AbdmLinkButton record={{ type: 'LabReport', id: view.reportId }} what="lab report" />
              </>
            )}
            {canVerify && releasedTests(view) > 0 && !view.amendmentWaitingSince && (
              <Button icon={<EditRegular />} onClick={() => setAmending(true)}>
                Amend
              </Button>
            )}
          </>
        }
      />

      {canVerify && <PendingApprovals subjectType="LabReport" subjectId={view.reportId} />}

      <LabReportPreview view={view} />

      <ConfirmDialog
        open={confirmRelease}
        title="Release to the doctor and the patient?"
        confirmLabel="Release"
        busy={release.isPending}
        onConfirm={() => release.mutate()}
        onCancel={() => setConfirmRelease(false)}
      >
        <Text>
          {waiting.join(', ')} will be visible in the patient record and the patient portal, and the patient is informed
          by SMS or WhatsApp. Corrections after release are recorded as amendments.
        </Text>
      </ConfirmDialog>

      {amending && <AmendReportDialog view={view} onClose={() => setAmending(false)} />}
    </Page>
  );
}
