import { Button, makeStyles, Text, Title2, tokens } from '@fluentui/react-components';
import { SignOutRegular } from '@fluentui/react-icons';
import { auth, useSession } from '@hc360/core';
import { useNavigate } from '@tanstack/react-router';
import { ChangePasswordForm, MfaSetup } from '@/modules/account/security';
import { BrandMark } from './BrandMark';

const useStyles = makeStyles({
  root: {
    minHeight: '100vh',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    padding: '24px 16px',
    boxSizing: 'border-box',
  },
  card: {
    width: 'min(520px, 100%)',
    display: 'flex',
    flexDirection: 'column',
    gap: '16px',
    padding: '32px',
    borderRadius: tokens.borderRadiusXLarge,
    backgroundColor: tokens.colorNeutralBackground1,
    boxShadow: tokens.shadow16,
    boxSizing: 'border-box',
  },
  muted: { color: tokens.colorNeutralForeground3 },
});

/**
 * Shown instead of the app while the session is restricted: a first sign-in with a temporary password, a password
 * published from data breaches (SRS SEC-01), or a role that must use two-step verification (SRS SEC-02/03). The server
 * rejects every other call until done.
 */
export function RestrictedSession({ restriction }: { restriction: 'password' | 'mfa-setup' }) {
  const styles = useStyles();
  const user = useSession((s) => s.user);
  const navigate = useNavigate();

  const signOut = async () => {
    await auth.logout();
    await navigate({ to: '/login', search: {} });
  };

  return (
    <div className={styles.root}>
      <div className={styles.card}>
        <BrandMark facility={user?.facilityName} />
        {restriction === 'password' ? (
          <>
            <Title2 as="h1">Choose a new password</Title2>
            <Text className={styles.muted}>
              {user?.passwordBreached
                ? `Welcome, ${user.displayName}. Your password is on published lists of passwords from data breaches, so attackers try it first. Choose a new one to continue.`
                : `Welcome, ${user?.displayName}. You signed in with a temporary password. Choose your own password to continue.`}
            </Text>
            <ChangePasswordForm />
          </>
        ) : (
          <>
            <Title2 as="h1">Turn on two-step verification</Title2>
            <Text className={styles.muted}>
              Your account has administrative or financial rights, so the hospital requires two-step verification.
            </Text>
            <MfaSetup />
          </>
        )}
        <div>
          <Button appearance="subtle" icon={<SignOutRegular />} onClick={() => void signOut()}>
            Sign out
          </Button>
        </div>
      </div>
    </div>
  );
}
