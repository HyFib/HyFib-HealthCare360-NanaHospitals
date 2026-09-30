import { Avatar, Button, makeStyles, Spinner, tokens } from '@fluentui/react-components';
import {
  DocumentCheckmarkRegular,
  MegaphoneRegular,
  ShieldLockRegular,
  ShieldPersonRegular,
  SignOutRegular,
} from '@fluentui/react-icons';
import { createFileRoute } from '@tanstack/react-router';
import { useState } from 'react';
import { InfoList } from '@/components/InfoList';
import { CardLink, TextLink } from '@/components/links';
import { ListRow } from '@/components/ListRow';
import { PortalPage } from '@/components/PortalPage';
import { useT } from '@/i18n';
import { useFormat } from '@/i18n/useFormat';
import { ageText, BLOOD_GROUP_LABEL, GENDER_KEY } from '@/lib/people';
import { IDLE_SIGN_OUT_MINUTES } from '@/lib/sessionEnd';
import { ReportCard } from '@/modules/reports/ReportParts';
import { useSignOut } from '@/shell/hooks';
import { LanguageChoice } from '@/shell/LanguageMenu';
import { useActiveProfile } from '@/shell/profileContext';
import { ProfileList } from '@/shell/ProfileSwitcher';

export const Route = createFileRoute('/_auth/profile/')({
  component: ProfilePage,
});

const useStyles = makeStyles({
  person: { display: 'flex', alignItems: 'center', gap: '14px' },
  personName: {
    margin: 0,
    fontSize: tokens.fontSizeBase500,
    lineHeight: tokens.lineHeightBase500,
    fontWeight: tokens.fontWeightSemibold,
    overflowWrap: 'anywhere',
  },
  personUhid: { margin: 0, color: tokens.colorNeutralForeground2 },
  hint: { margin: 0, color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200 },
  correct: { alignSelf: 'flex-start', display: 'inline-flex', alignItems: 'center', minHeight: '44px' },
  links: { display: 'flex', flexDirection: 'column', gap: '10px' },
  signOut: { alignSelf: 'flex-start', minWidth: '200px' },
});

function ProfilePage() {
  const styles = useStyles();
  const t = useT();
  const f = useFormat();
  const { profile, profiles } = useActiveProfile();
  const signOut = useSignOut();
  const [signingOut, setSigningOut] = useState(false);
  const bloodGroup = BLOOD_GROUP_LABEL[profile.bloodGroup];

  return (
    <PortalPage title={t('profile.title')} subtitle={t('profile.subtitle')} width="narrow">
      <ReportCard>
        <div className={styles.person}>
          <Avatar name={profile.fullName} size={56} color="colorful" aria-hidden />
          <div>
            <p className={styles.personName}>{profile.fullName}</p>
            <p className={styles.personUhid}>
              {t('common.uhid')} {profile.uhid}
            </p>
          </div>
        </div>
        <InfoList
          items={[
            { label: t('profile.age'), value: ageText(profile.age, t), hideEmpty: true },
            {
              label: t('profile.gender'),
              value: profile.gender === 'Unknown' ? null : t(GENDER_KEY[profile.gender]),
              hideEmpty: true,
            },
            {
              label: t('profile.dob'),
              value: profile.dateOfBirth ? f.calendarDate(profile.dateOfBirth) : null,
              hideEmpty: true,
            },
            { label: t('profile.bloodGroup'), value: bloodGroup, hideEmpty: true },
            { label: t('profile.phone'), value: profile.phone, hideEmpty: true },
          ]}
        />
        <p className={styles.hint}>{t('profile.correct')}</p>
        <TextLink to="/my-privacy/requests/new" search={{ type: 'correction' }} className={styles.correct}>
          {t('myPrivacy.hub.correct')}
        </TextLink>
      </ReportCard>

      {profiles.length > 1 && (
        <ReportCard title={t('profile.family')}>
          <ProfileList profiles={profiles} activeId={profile.id} />
        </ReportCard>
      )}

      <ReportCard title={t('profile.language')}>
        <LanguageChoice />
      </ReportCard>

      <ReportCard title={t('profile.more')}>
        <div className={styles.links}>
          <CardLink to="/my-privacy">
            <ListRow icon={<ShieldPersonRegular />} title={t('nav.myPrivacy')} subtitle={t('myPrivacy.profile.hint')} />
          </CardLink>
          <CardLink to="/profile/preferences">
            <ListRow
              icon={<MegaphoneRegular />}
              title={t('feedback.preferences')}
              subtitle={t('feedback.preferences.hint')}
            />
          </CardLink>
          <CardLink to="/privacy">
            <ListRow icon={<ShieldLockRegular />} title={t('nav.privacy')} subtitle={t('profile.privacy.hint')} />
          </CardLink>
          <CardLink to="/verify">
            <ListRow icon={<DocumentCheckmarkRegular />} title={t('nav.verify')} subtitle={t('profile.verify.hint')} />
          </CardLink>
        </div>
      </ReportCard>

      <p className={styles.hint}>{t('profile.idle', { minutes: IDLE_SIGN_OUT_MINUTES })}</p>
      <Button
        appearance="secondary"
        size="large"
        className={styles.signOut}
        icon={signingOut ? <Spinner size="tiny" /> : <SignOutRegular />}
        disabled={signingOut}
        onClick={() => {
          setSigningOut(true);
          void signOut().finally(() => setSigningOut(false));
        }}
      >
        {signingOut ? t('common.signingOut') : t('common.signOut')}
      </Button>
      <p className={styles.hint}>{t('profile.signOut.hint')}</p>
    </PortalPage>
  );
}
