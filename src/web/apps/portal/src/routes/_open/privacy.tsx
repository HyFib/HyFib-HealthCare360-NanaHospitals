import { makeStyles, tokens } from '@fluentui/react-components';
import { ShieldPersonRegular } from '@fluentui/react-icons';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute } from '@tanstack/react-router';
import { ButtonLink, TextLink } from '@/components/links';
import { PortalPage } from '@/components/PortalPage';
import { Notice } from '@/components/States';
import { useT, type MessageKey } from '@/i18n';
import { IDLE_SIGN_OUT_MINUTES } from '@/lib/sessionEnd';
import { publicPrivacyQueries } from '@/modules/privacy/api';
import { ContactCard } from '@/modules/privacy/ContactCard';
import { ReportCard } from '@/modules/reports/ReportParts';
import { useHospitalName } from '@/shell/hooks';
import { useOptionalProfile } from '@/shell/profileContext';

/**
 * The portal's privacy notice (DPDP Act 2023), open to everyone, in plain language and in the patient's language.
 * It names the grievance officer and the reply period the hospital publishes (left out if they cannot be loaded),
 * and a signed-in patient can go straight to "Your privacy".
 */
export const Route = createFileRoute('/_open/privacy')({
  component: PrivacyPage,
});

const SECTIONS: { title: MessageKey; items: MessageKey[] }[] = [
  { title: 'privacy.what.title', items: ['privacy.what.1', 'privacy.what.2', 'privacy.what.3', 'privacy.what.4'] },
  { title: 'privacy.why.title', items: ['privacy.why.1', 'privacy.why.2', 'privacy.why.3'] },
  { title: 'privacy.share.title', items: ['privacy.share.body'] },
  { title: 'privacy.keep.title', items: ['privacy.keep.body'] },
  { title: 'privacy.family.title', items: ['privacy.family.body'] },
  {
    title: 'privacy.rights.title',
    items: [
      'privacy.rights.1',
      'privacy.rights.2',
      'privacy.rights.5',
      'privacy.rights.3',
      'privacy.rights.6',
      'privacy.rights.4',
    ],
  },
];

const useStyles = makeStyles({
  intro: { margin: 0, fontSize: tokens.fontSizeBase400, lineHeight: tokens.lineHeightBase400 },
  list: {
    margin: 0,
    paddingLeft: '20px',
    display: 'flex',
    flexDirection: 'column',
    gap: '8px',
    fontSize: tokens.fontSizeBase400,
    lineHeight: tokens.lineHeightBase400,
  },
  text: { margin: 0, fontSize: tokens.fontSizeBase400, lineHeight: tokens.lineHeightBase400 },
  links: { display: 'flex', flexWrap: 'wrap', gap: '8px 20px' },
  myPrivacy: { alignSelf: 'flex-start' },
});

function PrivacyPage() {
  const styles = useStyles();
  const t = useT();
  const hospital = useHospitalName();
  const signedIn = useOptionalProfile() !== null;
  // Anonymous and rate-limited: while it loads, or if it fails, the notice simply goes without the contact block.
  const info = useQuery(publicPrivacyQueries.info());

  return (
    <PortalPage title={t('privacy.title')} width="narrow">
      <p className={styles.intro}>{t('privacy.intro', { hospital })}</p>
      {SECTIONS.map((section) => (
        <ReportCard key={section.title} title={t(section.title)}>
          {section.items.length === 1 ? (
            <p className={styles.text}>{t(section.items[0] as MessageKey)}</p>
          ) : (
            <ul className={styles.list}>
              {section.items.map((item) => (
                <li key={item}>{t(item)}</li>
              ))}
            </ul>
          )}
        </ReportCard>
      ))}
      {signedIn && (
        <ButtonLink to="/my-privacy" appearance="primary" icon={<ShieldPersonRegular />} className={styles.myPrivacy}>
          {t('privacy.myPrivacy')}
        </ButtonLink>
      )}
      {info.data && <ContactCard contact={info.data.contact} responseDays={info.data.responseDays} showBoard={false} />}
      <ReportCard title={t('privacy.device.title')}>
        <p className={styles.text}>{t('privacy.device.body', { minutes: IDLE_SIGN_OUT_MINUTES })}</p>
      </ReportCard>
      <Notice intent="warning">{t('privacy.otp')}</Notice>
      <div className={styles.links}>
        <TextLink to="/records/request">{t('privacy.requestCopy')}</TextLink>
        <TextLink to="/profile/preferences">{t('privacy.preferences')}</TextLink>
      </div>
    </PortalPage>
  );
}
