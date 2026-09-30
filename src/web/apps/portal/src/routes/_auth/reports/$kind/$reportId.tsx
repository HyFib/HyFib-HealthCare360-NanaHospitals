import { useQuery } from '@tanstack/react-query';
import { createFileRoute } from '@tanstack/react-router';
import type { ReactNode } from 'react';
import { DocumentActions } from '@/components/DocumentActions';
import { BackLink } from '@/components/links';
import { OwnerGuard } from '@/components/OwnerGuard';
import { PortalPage } from '@/components/PortalPage';
import { ErrorBlock, Notice, PageSpinner } from '@/components/States';
import { useT } from '@/i18n';
import { useFormat } from '@/i18n/useFormat';
import { isGuid } from '@/lib/ids';
import { isReportKind, reportQueries, type ReportKindSegment } from '@/modules/reports/api';
import { CardiologyReportView, RadiologyReportView } from '@/modules/reports/ImagingReportViews';
import { CARDIOLOGY_TYPE_KEY } from '@/modules/reports/labResults';
import { LabReportView } from '@/modules/reports/LabReportView';
import { cardiologyReportDocument, labReportDocument, radiologyReportDocument } from '@/modules/reports/print';
import { renderedReportQr } from '@/modules/reports/verifyLink';
import { useActiveProfile } from '@/shell/profileContext';

export const Route = createFileRoute('/_auth/reports/$kind/$reportId')({
  component: ReportPage,
});

function ReportPage() {
  const { kind, reportId } = Route.useParams();
  const t = useT();
  const back = <BackLink to="/reports">{t('reports.title')}</BackLink>;
  if (!isReportKind(kind) || !isGuid(reportId)) {
    return (
      <PortalPage title={t('reports.title')} back={back} width="narrow">
        <Notice intent="warning">{t('errors.notFound')}</Notice>
      </PortalPage>
    );
  }
  return <ReportLoader kind={kind} reportId={reportId} back={back} />;
}

function ReportLoader({ kind, reportId, back }: { kind: ReportKindSegment; reportId: string; back: ReactNode }) {
  const t = useT();
  const f = useFormat();
  const { profile } = useActiveProfile();
  const lab = useQuery({ ...reportQueries.lab(profile.id, reportId), enabled: kind === 'lab' });
  const radiology = useQuery({ ...reportQueries.radiology(profile.id, reportId), enabled: kind === 'radiology' });
  const cardiology = useQuery({ ...reportQueries.cardiology(profile.id, reportId), enabled: kind === 'cardiology' });
  const query = kind === 'lab' ? lab : kind === 'radiology' ? radiology : cardiology;
  // The printed copy carries the same QR code as the screen (copied from the page when printing).
  const printContext = () => ({ t, f, printedAt: new Date().toISOString(), qrSvg: renderedReportQr() });

  if (query.isPending) {
    return <PageSpinner />;
  }
  if (!query.data) {
    return (
      <PortalPage title={t('reports.title')} back={back} width="narrow">
        <ErrorBlock error={query.error} onRetry={() => void query.refetch()} />
      </PortalPage>
    );
  }

  if (kind === 'lab' && lab.data) {
    const report = lab.data;
    const title =
      report.sections.flatMap((section) => section.tests.map((test) => test.name)).join(', ') ||
      t('reports.kind.Laboratory');
    return (
      <OwnerGuard ownerId={report.patient.id}>
        <PortalPage
          title={title}
          subtitle={`${t('reports.kind.Laboratory')} · ${t('reports.released', { date: f.date(report.releasedAt) })}`}
          back={back}
          actions={<DocumentActions build={() => labReportDocument(report, printContext())} />}
        >
          <LabReportView report={report} />
        </PortalPage>
      </OwnerGuard>
    );
  }
  if (kind === 'radiology' && radiology.data) {
    const report = radiology.data;
    return (
      <OwnerGuard ownerId={report.patient.id}>
        <PortalPage
          title={report.studyName}
          subtitle={`${t('reports.kind.Radiology')} · ${t('reports.released', { date: f.date(report.releasedAt) })}`}
          back={back}
          actions={<DocumentActions build={() => radiologyReportDocument(report, printContext())} />}
        >
          <RadiologyReportView report={report} />
        </PortalPage>
      </OwnerGuard>
    );
  }
  if (kind === 'cardiology' && cardiology.data) {
    const report = cardiology.data;
    return (
      <OwnerGuard ownerId={report.patient.id}>
        <PortalPage
          title={report.studyName || t(CARDIOLOGY_TYPE_KEY[report.studyType])}
          subtitle={`${t(CARDIOLOGY_TYPE_KEY[report.studyType])} · ${t('reports.released', { date: f.date(report.releasedAt) })}`}
          back={back}
          actions={<DocumentActions build={() => cardiologyReportDocument(report, printContext())} />}
        >
          <CardiologyReportView report={report} />
        </PortalPage>
      </OwnerGuard>
    );
  }
  return null;
}
