import { makeStyles, tokens } from '@fluentui/react-components';
import {
  ClipboardTaskListLtrRegular,
  FolderPersonRegular,
  MegaphoneRegular,
  PersonHeartRegular,
} from '@fluentui/react-icons';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute } from '@tanstack/react-router';
import { CardLink } from '@/components/links';
import { ListRow } from '@/components/ListRow';
import { PortalPage } from '@/components/PortalPage';
import { ErrorBlock, LoadingCards } from '@/components/States';
import { useT } from '@/i18n';
import { privacyQueries, publicPrivacyQueries } from '@/modules/privacy/api';
import { ContactCard } from '@/modules/privacy/ContactCard';
import { NoticeCards } from '@/modules/privacy/NoticeCards';
import { RequestLinks } from '@/modules/privacy/RequestLinks';
import { useHospitalName } from '@/shell/hooks';
import { useActiveProfile } from '@/shell/profileContext';

/** "Your privacy": the patient's rights under the DPDP Act 2023, in one place. */
export const Route = createFileRoute('/_auth/my-privacy/')({
  component: PrivacyHubPage,
});

const useStyles = makeStyles({
  intro: {
    margin: 0,
    fontSize: tokens.fontSizeBase400,
    lineHeight: tokens.lineHeightBase400,
    color: tokens.colorNeutralForeground1,
  },
  section: { display: 'flex', flexDirection: 'column', gap: '10px' },
  sectionTitle: {
    margin: '8px 0 0',
    fontSize: tokens.fontSizeBase500,
    lineHeight: tokens.lineHeightBase500,
    fontWeight: tokens.fontWeightSemibold,
  },
});

function PrivacyHubPage() {
  const styles = useStyles();
  const t = useT();
  const hospital = useHospitalName();
  const { profile, profiles } = useActiveProfile();
  const notices = useQuery(privacyQueries.notices(profile.id));
  // Counts and the nominee's name only: unlike the request list or the nominee, not recorded as record access.
  const status = useQuery(privacyQueries.status(profile.id));
  const info = useQuery(publicPrivacyQueries.info());
  const open = status.data?.openRequests ?? 0;
  const nomineeName = status.data?.hasNominee ? status.data.nomineeName?.trim() : undefined;

  let nomineeLine = t('myPrivacy.hub.nominee.hint');
  if (nomineeName) {
    nomineeLine = t('myPrivacy.hub.nominee.name', { name: nomineeName });
  } else if (status.data && !status.data.hasNominee) {
    nomineeLine = t('myPrivacy.hub.nominee.none');
  }

  return (
    <PortalPage
      title={t('nav.myPrivacy')}
      subtitle={profiles.length > 1 ? t('common.forPerson', { name: profile.fullName }) : undefined}
      width="narrow"
    >
      {notices.isError ? (
        <ErrorBlock error={notices.error} title="myPrivacy.notices.error" onRetry={() => void notices.refetch()} />
      ) : (
        <NoticeCards notices={notices.data ?? []} />
      )}

      <p className={styles.intro}>{t('myPrivacy.hub.intro', { hospital })}</p>

      <section className={styles.section} aria-labelledby="privacy-rights">
        <h2 id="privacy-rights" className={styles.sectionTitle}>
          {t('myPrivacy.hub.data')}
        </h2>
        <CardLink to="/my-privacy/data">
          <ListRow
            icon={<FolderPersonRegular />}
            title={t('myPrivacy.hub.see')}
            subtitle={t('myPrivacy.hub.see.hint')}
          />
        </CardLink>
        <RequestLinks />
      </section>

      <section className={styles.section} aria-labelledby="privacy-more">
        <h2 id="privacy-more" className={styles.sectionTitle}>
          {t('myPrivacy.hub.more')}
        </h2>
        <CardLink to="/my-privacy/requests">
          <ListRow
            icon={<ClipboardTaskListLtrRegular />}
            tone={open > 0 ? 'warning' : 'brand'}
            title={t('myPrivacy.hub.requests')}
            subtitle={open > 0 ? t('myPrivacy.hub.requests.open', { count: open }) : t('myPrivacy.hub.requests.hint')}
          />
        </CardLink>
        <CardLink to="/my-privacy/nominee">
          <ListRow icon={<PersonHeartRegular />} title={t('myPrivacy.hub.nominee')} subtitle={nomineeLine} />
        </CardLink>
        <CardLink to="/profile/preferences">
          <ListRow
            icon={<MegaphoneRegular />}
            title={t('myPrivacy.hub.messages')}
            subtitle={t('feedback.preferences.hint')}
          />
        </CardLink>
      </section>

      {info.isPending ? (
        <LoadingCards count={1} />
      ) : info.isError ? (
        <ErrorBlock error={info.error} onRetry={() => void info.refetch()} />
      ) : (
        <ContactCard contact={info.data.contact} responseDays={info.data.responseDays} />
      )}
    </PortalPage>
  );
}
