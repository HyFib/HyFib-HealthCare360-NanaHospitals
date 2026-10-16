import { makeStyles, mergeClasses, tokens } from '@fluentui/react-components';
import { DismissCircleRegular, ErrorCircleRegular, ShieldCheckmarkRegular } from '@fluentui/react-icons';
import { isApiError } from '@hc360/core';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute } from '@tanstack/react-router';
import type { ReactElement, ReactNode } from 'react';
import { InfoList } from '@/components/InfoList';
import { BackLink, ButtonLink } from '@/components/links';
import { PortalPage } from '@/components/PortalPage';
import { ErrorBlock, PageSpinner } from '@/components/States';
import { useT } from '@/i18n';
import { useFormat } from '@/i18n/useFormat';
import type { PrescriptionVerification, ReportAuthenticity } from '@/lib/types';
import { verifyQueries } from '@/modules/verify/api';
import { REPORT_KIND_TEXT, VERIFY_KIND_TEXT } from '@/modules/verify/labels';
import { isVerifyKind, isWellFormedCode, normalizeReportCode, type VerifyKind } from '@/modules/verify/verification';

/**
 * The answer for one printed document: /verify/prescription/{token} or /verify/{lab|radiology|cardiology}/{code}.
 * Printed QR codes open this page. It shows only whether the document is genuine, never results or identifiers; for a
 * prescription the doctor signed with their DSC, the signer, the certificate and the hash, as the server re-checked them.
 */
export const Route = createFileRoute('/_open/verify/$kind/$code')({
  component: VerificationPage,
});

const useStyles = makeStyles({
  verdict: {
    display: 'flex',
    alignItems: 'flex-start',
    gap: '14px',
    padding: '16px',
    borderRadius: tokens.borderRadiusXLarge,
  },
  genuine: {
    backgroundColor: tokens.colorStatusSuccessBackground1,
    border: `2px solid ${tokens.colorStatusSuccessBorder1}`,
  },
  bad: { backgroundColor: tokens.colorStatusDangerBackground1, border: `2px solid ${tokens.colorStatusDangerBorder1}` },
  unknown: {
    backgroundColor: tokens.colorStatusWarningBackground1,
    border: `2px solid ${tokens.colorStatusWarningBorder1}`,
  },
  verdictIcon: { fontSize: '40px', flexShrink: 0 },
  genuineIcon: { color: tokens.colorStatusSuccessForeground1 },
  badIcon: { color: tokens.colorStatusDangerForeground1 },
  unknownIcon: { color: tokens.colorStatusWarningForeground3 },
  verdictText: { display: 'flex', flexDirection: 'column', gap: '4px' },
  verdictTitle: {
    margin: 0,
    fontSize: tokens.fontSizeBase500,
    lineHeight: tokens.lineHeightBase500,
    fontWeight: tokens.fontWeightSemibold,
  },
  verdictBody: { margin: 0, fontSize: tokens.fontSizeBase400, lineHeight: tokens.lineHeightBase400 },
  details: {
    padding: '16px',
    borderRadius: tokens.borderRadiusXLarge,
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  tests: { margin: 0, paddingLeft: '20px' },
  note: { margin: 0, color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200 },
  revised: { margin: '12px 0 0', color: tokens.colorStatusWarningForeground3, fontWeight: tokens.fontWeightSemibold },
  dscTitle: { margin: '0 0 8px', fontSize: tokens.fontSizeBase400, fontWeight: tokens.fontWeightSemibold },
  dscText: { margin: '0 0 12px' },
  hash: { fontFamily: tokens.fontFamilyMonospace, overflowWrap: 'anywhere' },
  download: { display: 'inline-block', marginTop: '12px' },
});

function VerificationPage() {
  const styles = useStyles();
  const params = Route.useParams();
  const t = useT();
  const kind: VerifyKind | null = isVerifyKind(params.kind) ? params.kind : null;
  const code = kind && kind !== 'prescription' ? normalizeReportCode(params.code) : params.code;
  const wellFormed = kind !== null && isWellFormedCode(kind, code);
  const check = useQuery({ ...verifyQueries.check(kind ?? 'prescription', code), enabled: wellFormed });
  const notFound = !wellFormed || (isApiError(check.error) && check.error.status === 404);

  let content: ReactNode;
  if (notFound) {
    content = <Verdict tone="unknown" title={t('verify.notFound.title')} body={t('verify.notFound.body')} />;
  } else if (check.isPending) {
    content = <PageSpinner />;
  } else if (!check.data) {
    content = <ErrorBlock error={check.error} onRetry={() => void check.refetch()} />;
  } else if (check.data.kind === 'prescription') {
    content = <PrescriptionAnswer result={check.data.result} />;
  } else {
    content = <ReportAnswer result={check.data.result} />;
  }

  return (
    <PortalPage
      title={kind ? t(VERIFY_KIND_TEXT[kind].title) : t('verify.title')}
      subtitle={t('verify.title')}
      back={<BackLink to="/verify">{t('verify.title')}</BackLink>}
      width="narrow"
    >
      {content}
      <p className={styles.note}>{t('verify.privacy')}</p>
      <div>
        <ButtonLink to="/verify" appearance="secondary" search={kind ? { kind } : {}}>
          {t('verify.another')}
        </ButtonLink>
      </div>
    </PortalPage>
  );
}

function Verdict({ tone, title, body }: { tone: 'genuine' | 'bad' | 'unknown'; title: string; body: string }) {
  const styles = useStyles();
  const icon: Record<typeof tone, ReactElement> = {
    genuine: <ShieldCheckmarkRegular className={mergeClasses(styles.verdictIcon, styles.genuineIcon)} aria-hidden />,
    bad: <DismissCircleRegular className={mergeClasses(styles.verdictIcon, styles.badIcon)} aria-hidden />,
    unknown: <ErrorCircleRegular className={mergeClasses(styles.verdictIcon, styles.unknownIcon)} aria-hidden />,
  };
  return (
    <div className={mergeClasses(styles.verdict, styles[tone])} role="status">
      {icon[tone]}
      <div className={styles.verdictText}>
        <h2 className={styles.verdictTitle}>{title}</h2>
        <p className={styles.verdictBody}>{body}</p>
      </div>
    </div>
  );
}

function PrescriptionAnswer({ result }: { result: PrescriptionVerification }) {
  const styles = useStyles();
  const t = useT();
  const f = useFormat();
  if (!result.found) {
    return <Verdict tone="unknown" title={t('verify.notFound.title')} body={t('verify.notFound.body')} />;
  }
  const cancelled = result.status !== 'Valid';
  const dsc = result.digitalSignature;
  const badSignature = dsc?.state === 'Invalid';
  return (
    <>
      {cancelled ? (
        <Verdict tone="bad" title={t('verify.cancelled.title')} body={t('verify.cancelled.body')} />
      ) : badSignature ? (
        <Verdict tone="bad" title={t('verify.dsc.title')} body={t('verify.dsc.invalid')} />
      ) : (
        <Verdict tone="genuine" title={t('verify.genuine.title')} body={t('verify.genuine.prescription')} />
      )}
      <div className={styles.details}>
        <InfoList
          items={[
            { label: t('verify.prescriptionNumber'), value: result.prescriptionNumber, hideEmpty: true },
            { label: t('verify.doctor'), value: result.doctor, hideEmpty: true },
            { label: t('verify.qualification'), value: result.qualification, hideEmpty: true },
            { label: t('verify.registration'), value: result.registrationNumber, hideEmpty: true },
            { label: t('verify.facility'), value: result.facility, hideEmpty: true },
            {
              label: t('verify.issuedOn'),
              value: result.issuedOn ? f.calendarDate(result.issuedOn) : null,
              hideEmpty: true,
            },
            { label: t('verify.itemCount'), value: f.number(result.itemCount) },
          ]}
        />
      </div>
      {dsc && (
        <div className={styles.details}>
          <h2 className={styles.dscTitle}>{t('verify.dsc.title')}</h2>
          <p className={styles.dscText}>{t(badSignature ? 'verify.dsc.invalid' : 'verify.dsc.valid')}</p>
          <InfoList
            items={[
              { label: t('verify.dsc.signer'), value: dsc.signerName },
              { label: t('verify.dsc.signedAt'), value: f.dateTime(dsc.signedAt) },
              {
                label: t('verify.dsc.serial'),
                value: <span className={styles.hash}>{dsc.certificateSerialNumber}</span>,
              },
              { label: t('verify.dsc.issuer'), value: dsc.certificateIssuer },
              { label: t('verify.dsc.hash'), value: <span className={styles.hash}>{dsc.contentHash}</span> },
            ]}
          />
          {result.signaturePath && (
            <a className={styles.download} href={result.signaturePath} download>
              {t('verify.dsc.download')}
            </a>
          )}
        </div>
      )}
    </>
  );
}

function ReportAnswer({ result }: { result: ReportAuthenticity }) {
  const styles = useStyles();
  const t = useT();
  const f = useFormat();
  if (!result.valid) {
    return <Verdict tone="unknown" title={t('verify.notFound.title')} body={t('verify.notFound.body')} />;
  }
  return (
    <>
      <Verdict tone="genuine" title={t('verify.genuine.title')} body={t('verify.genuine.report')} />
      <div className={styles.details}>
        <InfoList
          items={[
            { label: t('common.details'), value: t(REPORT_KIND_TEXT[result.kind]) },
            { label: t('verify.reportNumber'), value: result.reportNumber },
            { label: t('verify.facility'), value: result.facilityName, hideEmpty: true },
            { label: t('verify.patient'), value: result.patientInitials, hideEmpty: true },
            {
              label: t('verify.released'),
              value: result.releasedAt ? f.dateTime(result.releasedAt) : null,
              hideEmpty: true,
            },
            {
              label: t('verify.tests'),
              value: result.tests.length ? (
                <ul className={styles.tests}>
                  {result.tests.map((test, index) => (
                    <li key={`${test}-${index}`}>{test}</li>
                  ))}
                </ul>
              ) : null,
              hideEmpty: true,
            },
          ]}
        />
        {result.revision > 0 && <p className={styles.revised}>{t('verify.revised')}</p>}
      </div>
    </>
  );
}
