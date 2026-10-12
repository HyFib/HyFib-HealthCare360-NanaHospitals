import { Button, makeStyles, tokens } from '@fluentui/react-components';
import { PersonQuestionMarkRegular, SignOutRegular } from '@fluentui/react-icons';
import { markActivity, useIdle, useSession } from '@hc360/core';
import { EmptyState } from '@hc360/ui';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useLocation, useNavigate } from '@tanstack/react-router';
import { Fragment, useEffect, useMemo, type ReactNode } from 'react';
import { PortalPage } from '@/components/PortalPage';
import { ErrorBlock, PageSpinner } from '@/components/States';
import { useT } from '@/i18n';
import { resolveActiveProfile } from '@/lib/profiles';
import { useProfileStore } from '@/lib/profileStore';
import { forgetLocalState } from '@/lib/session';
import { IDLE_SIGN_OUT_MINUTES, takeSessionEndReason, useCallActivity } from '@/lib/sessionEnd';
import type { PortalProfile } from '@/lib/types';
import { profileQueries } from '@/modules/profiles/api';
import { useSignOut } from './hooks';
import { PortalFrame } from './PortalFrame';
import { ProfileContext } from './profileContext';
import { ProfileList, ProfileSwitcher } from './ProfileSwitcher';

const useStyles = makeStyles({
  chooser: { display: 'flex', flexDirection: 'column', gap: '12px' },
  lead: { margin: 0, color: tokens.colorNeutralForeground2 },
});

/**
 * The frame for a signed-in patient: navigation, "whose records" switcher, idle sign-out, and the family member
 * resolution (the only person on the number, the one chosen earlier in this tab, or a chooser).
 *
 * `requireSession`: signed-in-only pages leave for the sign-in page when the session ends. Open pages (doctors,
 * document checks, privacy notice) stay, and fall back to the public frame; a patient whose number has no hospital
 * registration yet still sees them.
 */
export function SignedInFrame({ children, requireSession }: { children: ReactNode; requireSession: boolean }) {
  const t = useT();
  const status = useSession((s) => s.status);
  const queryClient = useQueryClient();
  const navigate = useNavigate();
  const href = useLocation({ select: (location) => location.href });
  const signOut = useSignOut();
  const inCall = useCallActivity((s) => s.inCall);
  const profiles = useQuery(profileQueries.list());
  const storedId = useProfileStore((s) => s.profileId);

  // The session ended elsewhere (signed out, expired, revoked by the hospital) or here: forget everything, then go
  // to the sign-in page, which explains why.
  useEffect(() => {
    if (status === 'anonymous') {
      const reason = takeSessionEndReason();
      forgetLocalState(queryClient);
      if (requireSession || reason) {
        // The session is over: unsaved changes cannot be saved any more, so nothing holds the sign-in page back.
        void navigate({
          to: '/sign-in',
          search: reason ? { ended: reason } : { redirect: href },
          replace: true,
          ignoreBlocker: true,
        });
      }
    }
  }, [status, queryClient, navigate, href, requireSession]);

  // Shared phones: sign out after a quiet spell, but never in the middle of a video consultation.
  useIdle(IDLE_SIGN_OUT_MINUTES * 60_000, () => void signOut('idle'), status === 'authenticated' && !inCall);

  // The server ends a session nobody has used for as long, by what the app reports: a call is use, taps or not.
  useEffect(() => {
    if (!inCall) {
      return;
    }
    markActivity();
    const timer = setInterval(markActivity, 60_000);
    return () => clearInterval(timer);
  }, [inCall]);

  const list = useMemo(() => profiles.data ?? [], [profiles.data]);
  const active = resolveActiveProfile(list, storedId);
  const context = useMemo(() => (active ? { profile: active, profiles: list } : null), [active, list]);

  let content: ReactNode;
  if (profiles.isPending) {
    content = <PageSpinner />;
  } else if (profiles.isError) {
    content = (
      <PortalPage title={t('app.name')} width="narrow">
        <ErrorBlock error={profiles.error} onRetry={() => void profiles.refetch()} />
      </PortalPage>
    );
  } else if (list.length === 0) {
    content = requireSession ? (
      <PortalPage title={t('shell.noProfiles.title')} width="narrow">
        <EmptyState
          icon={<PersonQuestionMarkRegular />}
          title={t('shell.noProfiles.title')}
          description={t('shell.noProfiles.body')}
          action={
            <Button appearance="primary" size="large" icon={<SignOutRegular />} onClick={() => void signOut()}>
              {t('common.signOut')}
            </Button>
          }
        />
      </PortalPage>
    ) : (
      children
    );
  } else if (!context) {
    content = <ProfileChooser profiles={list} />;
  } else {
    content = (
      <ProfileContext.Provider value={context}>
        {/* Remount the page for another family member so no form or scroll state carries over. */}
        <Fragment key={context.profile.id}>{children}</Fragment>
      </ProfileContext.Provider>
    );
  }

  return (
    <PortalFrame profileSwitcher={active ? <ProfileSwitcher profiles={list} active={active} /> : undefined}>
      {content}
    </PortalFrame>
  );
}

function ProfileChooser({ profiles }: { profiles: PortalProfile[] }) {
  const styles = useStyles();
  const t = useT();
  return (
    <PortalPage title={t('shell.chooser.title')} width="narrow">
      <div className={styles.chooser}>
        <p className={styles.lead}>{t('shell.chooser.body')}</p>
        <ProfileList profiles={profiles} activeId={null} />
      </div>
    </PortalPage>
  );
}
