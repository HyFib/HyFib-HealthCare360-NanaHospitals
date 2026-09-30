import { makeStyles, tokens } from '@fluentui/react-components';
import { InfoRegular } from '@fluentui/react-icons';
import { InfoList } from '@/components/InfoList';
import { Notice } from '@/components/States';
import { useT } from '@/i18n';
import { useFormat } from '@/i18n/useFormat';
import type { CardiologyReportView as CardiologyReport, RadiologyReportView as RadiologyReport } from '@/lib/types';
import { cardiologyMeasurements } from './cardiology';
import { CARDIOLOGY_TYPE_KEY, MODALITY_KEY } from './labResults';
import { ReportCard, Signatories, VerificationCode } from './ReportParts';
import { reportVerifyUrl } from './verifyLink';

const useStyles = makeStyles({
  impression: { border: `2px solid ${tokens.colorBrandStroke1}`, backgroundColor: tokens.colorBrandBackground2 },
  text: { margin: 0, whiteSpace: 'pre-line', lineHeight: tokens.lineHeightBase400, overflowWrap: 'anywhere' },
  muted: { margin: 0, color: tokens.colorNeutralForeground2, display: 'flex', gap: '8px', alignItems: 'flex-start' },
  addenda: { listStyle: 'none', margin: 0, padding: 0, display: 'flex', flexDirection: 'column', gap: '10px' },
  addendumMeta: { color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200 },
});

/** A released radiology report: the radiologist's summary first, then what was seen and how. */
export function RadiologyReportView({ report }: { report: RadiologyReport }) {
  const styles = useStyles();
  const t = useT();
  const f = useFormat();
  return (
    <>
      {report.isCriticalFinding && <Notice intent="error">{t('reports.critical')}</Notice>}
      <ReportCard>
        <InfoList
          items={[
            { label: t('reports.kind.Radiology'), value: t(MODALITY_KEY[report.modality]) },
            { label: t('rad.bodyPart'), value: report.bodyPart, hideEmpty: true },
            { label: t('reports.numberLabel'), value: report.accessionNumber },
            { label: t('rad.acquired'), value: f.dateTime(report.acquiredAt), hideEmpty: !report.acquiredAt },
            { label: t('reports.releasedOn'), value: f.dateTime(report.releasedAt), hideEmpty: !report.releasedAt },
            { label: t('reports.referredBy'), value: report.referredBy, hideEmpty: true },
            { label: t('common.hospital'), value: report.letterhead.facilityName },
          ]}
        />
      </ReportCard>
      <ReportCard title={t('rad.impression')} className={styles.impression}>
        <p className={styles.text}>{report.impression}</p>
        <p className={styles.muted}>
          <InfoRegular aria-hidden style={{ flexShrink: 0, marginTop: 3 }} />
          {t('reports.doctorNote')}
        </p>
      </ReportCard>
      <ReportCard title={t('rad.findings')}>
        <p className={styles.text}>{report.findings}</p>
      </ReportCard>
      {(report.clinicalHistory || report.technique || report.comparison) && (
        <ReportCard>
          <InfoList
            singleColumn
            items={[
              { label: t('rad.history'), value: report.clinicalHistory, hideEmpty: true },
              { label: t('rad.technique'), value: report.technique, hideEmpty: true },
              { label: t('rad.comparison'), value: report.comparison, hideEmpty: true },
            ]}
          />
        </ReportCard>
      )}
      {report.addenda.length > 0 && (
        <ReportCard title={t('rad.addenda')}>
          <ul className={styles.addenda}>
            {report.addenda.map((addendum) => (
              <li key={addendum.id}>
                <span className={styles.addendumMeta}>
                  {[f.dateTime(addendum.addedAt), addendum.addedBy].filter(Boolean).join(' · ')}
                </span>
                <p className={styles.text}>{addendum.text}</p>
              </li>
            ))}
          </ul>
        </ReportCard>
      )}
      <Signatories signatories={report.signatories} />
      <p className={styles.muted}>
        <InfoRegular aria-hidden style={{ flexShrink: 0, marginTop: 3 }} />
        {t('rad.imagesNote')}
      </p>
      <VerificationCode
        code={report.verificationCode}
        url={reportVerifyUrl('radiology', report, window.location.origin)}
      />
    </>
  );
}

/** A released cardiology report (ECG, echo, TMT, Holter or ABPM). */
export function CardiologyReportView({ report }: { report: CardiologyReport }) {
  const styles = useStyles();
  const t = useT();
  const f = useFormat();
  const measurements = cardiologyMeasurements(report, t);
  return (
    <>
      {report.isCriticalFinding && <Notice intent="error">{t('reports.critical')}</Notice>}
      <ReportCard>
        <InfoList
          items={[
            { label: t('reports.kind.Cardiology'), value: t(CARDIOLOGY_TYPE_KEY[report.studyType]) },
            { label: t('reports.numberLabel'), value: report.accessionNumber },
            { label: t('card.recorded'), value: f.dateTime(report.acquiredAt), hideEmpty: !report.acquiredAt },
            { label: t('reports.releasedOn'), value: f.dateTime(report.releasedAt), hideEmpty: !report.releasedAt },
            { label: t('reports.referredBy'), value: report.referredBy, hideEmpty: true },
            { label: t('common.hospital'), value: report.letterhead.facilityName },
          ]}
        />
      </ReportCard>
      {report.conclusion && (
        <ReportCard title={t('card.conclusion')} className={styles.impression}>
          <p className={styles.text}>{report.conclusion}</p>
          <p className={styles.muted}>
            <InfoRegular aria-hidden style={{ flexShrink: 0, marginTop: 3 }} />
            {t('reports.doctorNote')}
          </p>
        </ReportCard>
      )}
      {measurements.length > 0 && (
        <ReportCard title={t('card.measurements')}>
          <InfoList items={measurements} />
        </ReportCard>
      )}
      {report.recommendations && (
        <ReportCard title={t('card.advice')}>
          <p className={styles.text}>{report.recommendations}</p>
        </ReportCard>
      )}
      <Signatories signatories={report.signatories} />
      <VerificationCode
        code={report.verificationCode}
        url={reportVerifyUrl('cardiology', report, window.location.origin)}
      />
    </>
  );
}
