import { PersonRegular } from '@fluentui/react-icons';
import { useSession } from '@hc360/core';
import { createFileRoute, Outlet, useLocation } from '@tanstack/react-router';
import { ButtonLink } from '@/components/links';
import { useT } from '@/i18n';
import { safeRedirect } from '@/lib/redirect';
import { ensurePortalSession } from '@/lib/session';
import { PortalFrame } from '@/shell/PortalFrame';
import { SignedInFrame } from '@/shell/SignedInFrame';

/**
 * Pages anyone may open without signing in: the doctor directory and doctor pages (links in waiting-list SMS), the
 * document checks behind printed QR codes, and the privacy notice. A signed-in patient keeps the full navigation;
 * everyone else gets the public frame with a "Sign in" button that returns to the same page.
 */
export const Route = createFileRoute('/_open')({
  beforeLoad: async () => {
    // Restores a session from the refresh cookie if there is one; never redirects.
    await ensurePortalSession();
  },
  component: OpenLayout,
});

function OpenLayout() {
  const t = useT();
  const signedIn = useSession((s) => s.status === 'authenticated' && s.user?.type === 'patient');
  const href = useLocation({ select: (location) => location.href });

  if (signedIn) {
    return (
      <SignedInFrame requireSession={false}>
        <Outlet />
      </SignedInFrame>
    );
  }
  return (
    <PortalFrame
      navigation={false}
      brandTarget="/sign-in"
      profileSwitcher={
        <ButtonLink appearance="primary" size="medium" icon={<PersonRegular />} to="/sign-in" search={{ redirect: safeRedirect(href) }}>
          {t('common.signIn')}
        </ButtonLink>
      }
    >
      <Outlet />
    </PortalFrame>
  );
}
