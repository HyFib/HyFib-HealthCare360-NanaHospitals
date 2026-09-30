import { makeStyles, tokens } from '@fluentui/react-components';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute } from '@tanstack/react-router';
import type { ReactNode } from 'react';
import { BackLink } from '@/components/links';
import { OwnerGuard } from '@/components/OwnerGuard';
import { PortalPage } from '@/components/PortalPage';
import { ErrorBlock, Notice, PageSpinner } from '@/components/States';
import { useT } from '@/i18n';
import { useFormat } from '@/i18n/useFormat';
import { isGuid } from '@/lib/ids';
import type { PortalBreachNotice } from '@/lib/types';
import { privacyQueries } from '@/modules/privacy/api';
import { ContactCard } from '@/modules/privacy/ContactCard';
import { noticeData, noticeOwner, noticeWhen } from '@/modules/privacy/model';
import { ReportCard } from '@/modules/reports/ReportParts';
import { useActiveProfile } from '@/shell/profileContext';

/**
 * A notice about an incident involving the patient's personal data (DPDP Act s8(6); DPDP Rules 2025, rule 7), in
 * full: what happened, when, what data, what it may mean, what the hospital is doing and what the patient can do.
 * The SMS the hospital sends links here.
 */
export const Route = createFileRoute('/_auth/my-privacy/notices/$noticeId')({
  component: PrivacyNoticePage,
});

const useStyles = makeStyles({
  lead: {
    margin: 0,
    fontSize: tokens.fontSizeBase400,
    lineHeight: tokens.lineHeightBase400,
    color: tokens.colorNeutralForeground2,
  },
  text: {
    margin: 0,
    whiteSpace: 'pre-wrap',
    overflowWrap: 'anywhere',
    fontSize: tokens.fontSizeBase400,
    lineHeight: tokens.lineHeightBase400,
  },
  meta: { margin: 0, color: tokens.colorNeutralForeground2 },
  list: {
    margin: 0,
    paddingLeft: '20px',
    display: 'flex',
    flexDirection: 'column',
    gap: '6px',
    fontSize: tokens.fontSizeBase400,
    lineHeight: tokens.lineHeightBase400,
  },
});

function PrivacyNoticePage() {
  const { noticeId } = Route.useParams();
  const t = useT();
  const { profile, profiles } = useActiveProfile();
  const notice = useQuery({ ...privacyQueries.notice(profile.id, noticeId), enabled: isGuid(noticeId) });
  const back = <BackLink to="/my-privacy">{t('nav.myPrivacy')}</BackLink>;

  if (!isGuid(noticeId)) {
    return (
      <PortalPage title={t('myPrivacy.notice.fallbackTitle')} back={back} width="narrow">
        <Notice intent="warning">{t('errors.notFound')}</Notice>
      </PortalPage>
    );
  }
  if (notice.isPending) {
    return <PageSpinner />;
  }
  if (notice.isError) {
    return (
      <PortalPage title={t('myPrivacy.notice.fallbackTitle')} back={back} width="narrow">
        <ErrorBlock error={notice.error} onRetry={() => void notice.refetch()} />
      </PortalPage>
    );
  }
  // A notice about another family member (the SMS link opened while viewing someone else) asks to switch first, so
  // one person's notice never reads as another's.
  return (
    <OwnerGuard ownerId={noticeOwner(notice.data.patientIds, profile.id, profiles)}>
      <NoticeView notice={notice.data} back={back} />
    </OwnerGuard>
  );
}

function NoticeView({ notice, back }: { notice: PortalBreachNotice; back: ReactNode }) {
  const styles = useStyles();
  const t = useT();
  const f = useFormat();
  const data = noticeData(notice.dataCategories, t);

  const section = (title: string, text: string | null | undefined) =>
    text?.trim() ? (
      <ReportCard title={title}>
        <p className={styles.text}>{text.trim()}</p>
      </ReportCard>
    ) : null;

  return (
    <PortalPage
      title={notice.title}
      subtitle={t('myPrivacy.notices.sent', { date: f.date(notice.notifiedAt), reference: notice.reference })}
      back={back}
      width="narrow"
    >
      <p className={styles.lead}>{t('myPrivacy.notice.intro')}</p>

      <ReportCard title={t('myPrivacy.notice.what')}>
        <p className={styles.text}>{notice.description.trim()}</p>
        {notice.location?.trim() && (
          <p className={styles.meta}>{t('myPrivacy.notice.where', { location: notice.location.trim() })}</p>
        )}
      </ReportCard>

      <ReportCard title={t('myPrivacy.notice.when')}>
        {noticeWhen(notice, t, f).map((line) => (
          <p key={line} className={styles.text}>
            {line}
          </p>
        ))}
      </ReportCard>

      <ReportCard title={t('myPrivacy.notice.data')}>
        {data.length > 0 ? (
          <ul className={styles.list}>
            {data.map((item) => (
              <li key={item}>{item}</li>
            ))}
          </ul>
        ) : (
          <p className={styles.text}>{t('myPrivacy.notice.data.unknown')}</p>
        )}
      </ReportCard>

      {section(t('myPrivacy.notice.consequences'), notice.consequences)}
      {section(t('myPrivacy.notice.mitigation'), notice.mitigation)}
      {section(t('myPrivacy.notice.steps'), notice.principalSteps)}

      <Notice intent="warning">{t('myPrivacy.notice.safety')}</Notice>

      <ContactCard contact={notice.contact} />
    </PortalPage>
  );
}
