import { Button, makeStyles, tokens } from '@fluentui/react-components';
import { ArrowRepeatAllRegular, InfoRegular, QrCodeRegular } from '@fluentui/react-icons';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute } from '@tanstack/react-router';
import { QRCodeSVG } from 'qrcode.react';
import { useRef, useState, type ReactNode } from 'react';
import { DocumentActions } from '@/components/DocumentActions';
import { InfoList } from '@/components/InfoList';
import { BackLink } from '@/components/links';
import { OwnerGuard } from '@/components/OwnerGuard';
import { PortalPage } from '@/components/PortalPage';
import { ErrorBlock, Notice, PageSpinner } from '@/components/States';
import { StatusPill } from '@/components/StatusPill';
import { useLanguage, useT } from '@/i18n';
import { useFormat } from '@/i18n/useFormat';
import { isGuid } from '@/lib/ids';
import type { PortalPrescriptionDetail } from '@/lib/types';
import { prescriptionPdfPath, prescriptionQueries } from '@/modules/prescriptions/api';
import { frequencyText, medicineName, RX_STATUS } from '@/modules/prescriptions/labels';
import { prescriptionDocument } from '@/modules/prescriptions/print';
import { RefillSheet } from '@/modules/prescriptions/RefillSheet';
import { verificationPageUrl } from '@/modules/verify/verification';
import { ReportCard } from '@/modules/reports/ReportParts';
import { useActiveProfile } from '@/shell/profileContext';

export const Route = createFileRoute('/_auth/prescriptions/$prescriptionId')({
  component: PrescriptionPage,
});

const useStyles = makeStyles({
  lines: { listStyle: 'none', margin: 0, padding: 0, display: 'flex', flexDirection: 'column', gap: '10px' },
  line: {
    display: 'flex',
    flexDirection: 'column',
    gap: '6px',
    padding: '12px',
    borderRadius: tokens.borderRadiusLarge,
    backgroundColor: tokens.colorNeutralBackground2,
  },
  lineName: { fontSize: tokens.fontSizeBase400, fontWeight: tokens.fontWeightSemibold, overflowWrap: 'anywhere' },
  generic: { color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200 },
  how: { display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(140px, 1fr))', gap: '6px 16px' },
  label: { display: 'block', color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200 },
  strong: { fontWeight: tokens.fontWeightSemibold },
  text: { margin: 0, whiteSpace: 'pre-line' },
  qr: { display: 'flex', gap: '16px', alignItems: 'center', flexWrap: 'wrap' },
  qrBox: {
    padding: '10px',
    backgroundColor: '#fff',
    borderRadius: tokens.borderRadiusLarge,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    lineHeight: 0,
  },
  muted: { margin: 0, color: tokens.colorNeutralForeground2, display: 'flex', gap: '8px', alignItems: 'flex-start' },
});

function PrescriptionPage() {
  const { prescriptionId } = Route.useParams();
  const t = useT();
  const { profile } = useActiveProfile();
  const rx = useQuery({ ...prescriptionQueries.detail(profile.id, prescriptionId), enabled: isGuid(prescriptionId) });
  const back = <BackLink to="/prescriptions">{t('rx.title')}</BackLink>;

  if (!isGuid(prescriptionId)) {
    return (
      <PortalPage title={t('rx.title')} back={back} width="narrow">
        <Notice intent="warning">{t('errors.notFound')}</Notice>
      </PortalPage>
    );
  }
  if (rx.isPending) {
    return <PageSpinner />;
  }
  if (!rx.data) {
    return (
      <PortalPage title={t('rx.title')} back={back} width="narrow">
        <ErrorBlock error={rx.error} onRetry={() => void rx.refetch()} />
      </PortalPage>
    );
  }
  return (
    <OwnerGuard ownerId={rx.data.patientId}>
      <PrescriptionDetail rx={rx.data} back={back} />
    </OwnerGuard>
  );
}

function PrescriptionDetail({ rx, back }: { rx: PortalPrescriptionDetail; back: ReactNode }) {
  const styles = useStyles();
  const t = useT();
  const f = useFormat();
  const { language } = useLanguage();
  const { profile } = useActiveProfile();
  const refills = useQuery(prescriptionQueries.refills(profile.id));
  const qrBox = useRef<HTMLDivElement>(null);
  const [refillOpen, setRefillOpen] = useState(false);
  const status = RX_STATUS[rx.status];
  const refillable =
    refills.data?.prescriptions.find((item) => item.prescriptionNumber === rx.prescriptionNumber) ?? null;
  const verifyUrl = verificationPageUrl(
    'prescription',
    rx.verificationToken,
    rx.verificationUrl,
    window.location.origin,
  );

  return (
    <PortalPage
      title={t('rx.print.title')}
      subtitle={[rx.doctor, f.date(rx.signedAt)].filter(Boolean).join(' · ')}
      back={back}
      actions={
        <DocumentActions
          build={() =>
            prescriptionDocument(rx, {
              t,
              f,
              language,
              printedAt: new Date().toISOString(),
              patient: profile,
              qrSvg: qrBox.current?.innerHTML ?? null,
              verifyUrl,
            })
          }
          pdf={prescriptionPdfPath(rx.id)}
        />
      }
    >
      {rx.status === 'Cancelled' && <Notice intent="error">{t('rx.cancelledNote')}</Notice>}

      <ReportCard>
        <div>
          <StatusPill tone={status.tone}>{t(status.key)}</StatusPill>
        </div>
        <InfoList
          items={[
            { label: t('common.doctor'), value: [rx.doctor, rx.doctorQualification].filter(Boolean).join(', ') },
            { label: t('rx.registration'), value: rx.doctorRegistrationNumber, hideEmpty: true },
            { label: t('common.hospital'), value: rx.facility, hideEmpty: true },
            { label: t('rx.date'), value: f.dateTime(rx.signedAt) },
            { label: t('rx.numberLabel'), value: rx.prescriptionNumber },
          ]}
        />
      </ReportCard>

      <ReportCard title={t('rx.medicines')}>
        <ul className={styles.lines}>
          {rx.lines.map((line, index) => (
            <li key={`${line.drugName}-${index}`} className={styles.line}>
              <span className={styles.lineName}>{medicineName(line)}</span>
              {line.genericName && <span className={styles.generic}>{line.genericName}</span>}
              <div className={styles.how}>
                <span>
                  <span className={styles.label}>{t('rx.dose')}</span>
                  <span className={styles.strong}>{line.dose}</span>
                </span>
                <span>
                  <span className={styles.label}>{t('rx.howOften')}</span>
                  <span className={styles.strong}>{frequencyText(line, language)}</span>
                </span>
                <span>
                  <span className={styles.label}>{t('rx.forDays', { count: line.durationDays })}</span>
                  <span className={styles.strong}>{t('rx.quantity', { count: f.number(line.quantity) })}</span>
                </span>
              </div>
              {line.instructions && (
                <span>
                  <span className={styles.label}>{t('rx.instructions')}</span>
                  {line.instructions}
                </span>
              )}
            </li>
          ))}
        </ul>
      </ReportCard>

      {rx.advice && (
        <ReportCard title={t('rx.advice')}>
          <p className={styles.text}>{rx.advice}</p>
        </ReportCard>
      )}

      {rx.status === 'Signed' && verifyUrl && rx.verificationToken && (
        <ReportCard title={t('rx.qr.title')}>
          <div className={styles.qr}>
            <div className={styles.qrBox} ref={qrBox}>
              <QRCodeSVG value={verifyUrl} size={168} level="M" marginSize={0} title={t('rx.qr.label')} />
            </div>
            <p className={styles.muted}>
              <QrCodeRegular aria-hidden style={{ flexShrink: 0, marginTop: 3 }} />
              {t('rx.qr.body')}
            </p>
          </div>
        </ReportCard>
      )}

      {rx.status === 'Signed' && refillable && (
        <ReportCard title={t('rx.refill.title')}>
          {refillable.hasOpenRequest ? (
            <Notice intent="info">{t('rx.refill.pending')}</Notice>
          ) : (
            <>
              <p className={styles.muted}>
                {refillable.refillValidUntil
                  ? t('rx.refill.available', {
                      count: refillable.refillsRemaining,
                      date: f.calendarDate(refillable.refillValidUntil),
                    })
                  : t('rx.refill.availableNoDate', { count: refillable.refillsRemaining })}
              </p>
              <div>
                <Button
                  appearance="primary"
                  size="large"
                  icon={<ArrowRepeatAllRegular />}
                  onClick={() => setRefillOpen(true)}
                >
                  {t('rx.refill.request')}
                </Button>
              </div>
              <RefillSheet open={refillOpen} onClose={() => setRefillOpen(false)} refill={refillable} />
            </>
          )}
        </ReportCard>
      )}

      <p className={styles.muted}>
        <InfoRegular aria-hidden style={{ flexShrink: 0, marginTop: 3 }} />
        {t('rx.safety')}
      </p>
    </PortalPage>
  );
}
