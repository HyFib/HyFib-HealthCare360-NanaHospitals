import { formatDateTime, useSession } from '@hc360/core';
import { EmptyState, ErrorState, LoadingState, Page, PageHeader } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { usePermission } from '@/lib/permissions';
import { emrQueries } from '@/modules/emr/api';
import { BannerSkeleton, ChartLoadError, ClinicalBanner } from '@/modules/emr/ClinicalBanner';
import { DischargeSummaryEditor } from '@/modules/emr/DischargeSummaryEditor';
import { useClinicalSummary } from '@/modules/emr/hooks';

export const Route = createFileRoute('/_app/emr/patients/$patientId/discharge/$admissionId')({
  component: DischargeSummaryPage,
});

/** Discharge summary of one admission: prefill, draft, sign, amend and print (A4). */
function DischargeSummaryPage() {
  const { patientId, admissionId } = Route.useParams();
  const navigate = useNavigate();
  const facilityName = useSession((s) => s.user?.facilityName) ?? 'Nana Hospitals';
  const canWrite = usePermission('clinical.discharge_summary.write');
  const canSeeAdmissions = usePermission('inpatient.admissions.read');
  const summary = useClinicalSummary(patientId);
  const admissions = useQuery({ ...emrQueries.admissions(patientId), enabled: canSeeAdmissions });
  const discharge = useQuery(emrQueries.discharge(admissionId));
  const admission = admissions.data?.find((a) => a.id === admissionId);

  if (summary.isLoading) {
    return (
      <Page>
        <BannerSkeleton />
      </Page>
    );
  }
  if (summary.error || !summary.data) {
    return <ChartLoadError patientId={patientId} error={summary.error} onRetry={() => void summary.refetch()} />;
  }

  const toChart = () =>
    void navigate({ to: '/emr/patients/$patientId', params: { patientId }, search: { tab: 'admissions' } });
  const subtitle = admission
    ? `${admission.admissionNumber} · admitted ${formatDateTime(admission.admittedAt)}${admission.dischargedAt ? ` · discharged ${formatDateTime(admission.dischargedAt)}` : ' · still admitted'}${admission.consultantName ? ` · ${admission.consultantName}` : ''}`
    : 'In-patient stay';

  return (
    <Page maxWidth={1400}>
      <ClinicalBanner summary={summary.data} />
      <PageHeader
        title="Discharge summary"
        subtitle={subtitle}
        breadcrumbs={[
          { label: 'Chart', onClick: toChart },
          { label: 'Admissions', onClick: toChart },
          { label: 'Discharge summary' },
        ]}
      />
      {discharge.isLoading ? (
        <LoadingState />
      ) : discharge.error ? (
        <ErrorState error={discharge.error} onRetry={() => void discharge.refetch()} />
      ) : discharge.data === null && !canWrite ? (
        <EmptyState
          title="No discharge summary yet"
          description="The treating doctor writes it before discharge. It appears here once saved."
        />
      ) : (
        <DischargeSummaryEditor
          key={admissionId}
          patientId={patientId}
          admissionId={admissionId}
          admission={admission}
          summary={discharge.data ?? null}
          facilityName={facilityName}
        />
      )}
    </Page>
  );
}
