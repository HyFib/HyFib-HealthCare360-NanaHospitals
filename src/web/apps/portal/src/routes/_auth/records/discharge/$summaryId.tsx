import { makeStyles, tokens } from '@fluentui/react-components';
import { CalendarAddRegular } from '@fluentui/react-icons';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute } from '@tanstack/react-router';
import type { ReactNode } from 'react';
import { DocumentActions } from '@/components/DocumentActions';
import { BackLink, ButtonLink } from '@/components/links';
import { OwnerGuard } from '@/components/OwnerGuard';
import { PortalPage } from '@/components/PortalPage';
import { ErrorBlock, Notice, PageSpinner } from '@/components/States';
import { useT } from '@/i18n';
import { useFormat } from '@/i18n/useFormat';
import { isGuid } from '@/lib/ids';
import type { DischargeSummaryDto } from '@/lib/types';
import { dischargeSummaryPdfPath, recordQueries } from '@/modules/records/api';
import { splitLines } from '@/modules/records/labels';
import { dischargeSummaryDocument } from '@/modules/records/print';
import { ReportCard } from '@/modules/reports/ReportParts';
import { useHospitalName } from '@/shell/hooks';
import { useActiveProfile } from '@/shell/profileContext';

export const Route = createFileRoute('/_auth/records/discharge/$summaryId')({
  component: DischargeSummaryPage,
});

const useStyles = makeStyles({
  text: {
    margin: 0,
    whiteSpace: 'pre-wrap',
    overflowWrap: 'anywhere',
    fontSize: tokens.fontSizeBase400,
    lineHeight: tokens.lineHeightBase400,
  },
  lines: {
    margin: 0,
    paddingLeft: '20px',
    display: 'flex',
    flexDirection: 'column',
    gap: '4px',
    fontSize: tokens.fontSizeBase400,
  },
  meta: { margin: 0, color: tokens.colorNeutralForeground2, fontSize: tokens.fontSizeBase200 },
  followUp: { margin: 0, fontWeight: tokens.fontWeightSemibold, fontSize: tokens.fontSizeBase400 },
});

function DischargeSummaryPage() {
  const { summaryId } = Route.useParams();
  const t = useT();
  const { profile } = useActiveProfile();
  const summary = useQuery({ ...recordQueries.discharge(profile.id, summaryId), enabled: isGuid(summaryId) });
  const back = <BackLink to="/records">{t('records.title')}</BackLink>;

  if (!isGuid(summaryId)) {
    return (
      <PortalPage title={t('discharge.title')} back={back} width="narrow">
        <Notice intent="warning">{t('errors.notFound')}</Notice>
      </PortalPage>
    );
  }
  if (summary.isPending) {
    return <PageSpinner />;
  }
  if (!summary.data) {
    return (
      <PortalPage title={t('discharge.title')} back={back} width="narrow">
        <ErrorBlock error={summary.error} onRetry={() => void summary.refetch()} />
      </PortalPage>
    );
  }
  return (
    <OwnerGuard ownerId={summary.data.patientId}>
      <DischargeSummaryView summary={summary.data} back={back} />
    </OwnerGuard>
  );
}

function DischargeSummaryView({ summary, back }: { summary: DischargeSummaryDto; back: ReactNode }) {
  const styles = useStyles();
  const t = useT();
  const f = useFormat();
  const hospital = useHospitalName();
  const { profile } = useActiveProfile();
  const diagnoses = splitLines(summary.diagnoses);
  const stay = [
    summary.admittedAt ? t('discharge.admitted', { date: f.date(summary.admittedAt) }) : null,
    summary.dischargedAt ? t('discharge.discharged', { date: f.date(summary.dischargedAt) }) : null,
  ]
    .filter(Boolean)
    .join(' · ');

  const section = (title: string, text: string | null | undefined, extra?: ReactNode) =>
    text?.trim() ? (
      <ReportCard title={title}>
        <p className={styles.text}>{text.trim()}</p>
        {extra}
      </ReportCard>
    ) : null;

  return (
    <PortalPage
      title={t('discharge.title')}
      subtitle={stay || undefined}
      back={back}
      actions={
        <DocumentActions
          build={() =>
            dischargeSummaryDocument(summary, {
              t,
              f,
              hospital,
              printedAt: new Date().toISOString(),
              patient: { fullName: profile.fullName, uhid: profile.uhid },
            })
          }
          pdf={dischargeSummaryPdfPath(summary.id)}
        />
      }
    >
      {(summary.practitionerName || summary.signedAt || summary.amendedAt) && (
        <div>
          {summary.practitionerName && (
            <p className={styles.meta}>
              {t('discharge.doctor')}: {summary.practitionerName}
            </p>
          )}
          {summary.signedAt && (
            <p className={styles.meta}>{t('discharge.signed', { date: f.dateTime(summary.signedAt) })}</p>
          )}
          {summary.amendedAt && (
            <p className={styles.meta}>{t('discharge.amended', { date: f.dateTime(summary.amendedAt) })}</p>
          )}
        </div>
      )}

      {diagnoses.length > 0 && (
        <ReportCard title={t('discharge.diagnoses')}>
          {diagnoses.length === 1 ? (
            <p className={styles.text}>{diagnoses[0]}</p>
          ) : (
            <ul className={styles.lines}>
              {diagnoses.map((line, index) => (
                <li key={index}>{line}</li>
              ))}
            </ul>
          )}
        </ReportCard>
      )}

      {(summary.followUpDate || summary.followUpInstructions?.trim()) && (
        <ReportCard title={t('discharge.followUp')}>
          {summary.followUpDate && (
            <p className={styles.followUp}>
              {t('discharge.followUpOn', { date: f.calendarDate(summary.followUpDate) })}
            </p>
          )}
          {summary.followUpInstructions?.trim() && <p className={styles.text}>{summary.followUpInstructions.trim()}</p>}
          <div>
            <ButtonLink to="/doctors" appearance="secondary" icon={<CalendarAddRegular />}>
              {t('discharge.book')}
            </ButtonLink>
          </div>
        </ReportCard>
      )}

      {section(
        t('discharge.medications'),
        summary.medications,
        <p className={styles.meta}>{t('discharge.medicationsNote')}</p>,
      )}
      {section(t('discharge.advice'), summary.advice)}
      {section(t('discharge.complaints'), summary.presentingComplaints)}
      {section(t('discharge.course'), summary.courseInHospital)}
      {section(t('discharge.procedures'), summary.procedures)}
      {section(t('discharge.investigations'), summary.investigationsSummary)}
      {section(t('discharge.condition'), summary.conditionAtDischarge)}

      <Notice intent="info">{t('discharge.emergency')}</Notice>
    </PortalPage>
  );
}
