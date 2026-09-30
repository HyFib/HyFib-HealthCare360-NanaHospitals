import { makeStyles, mergeClasses, tokens } from '@fluentui/react-components';
import { Link } from '@tanstack/react-router';
import type { ReactNode } from 'react';
import { useT } from '@/i18n';
import { TextLink } from '@/components/links';
import { useMediaQuery, WIDE_LAYOUT_QUERY } from '@/lib/hooks';
import { BrandMark } from './BrandMark';
import { ConnectionBanner } from './ConnectionBanner';
import { LanguageMenu } from './LanguageMenu';
import { BottomNav, SideNav } from './Navigation';

const useStyles = makeStyles({
  app: {
    minHeight: '100dvh',
    display: 'flex',
    flexDirection: 'column',
    backgroundColor: tokens.colorNeutralBackground2,
  },
  skip: {
    position: 'absolute',
    left: '8px',
    top: '-100px',
    zIndex: 100,
    padding: '12px 16px',
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground1,
    color: tokens.colorBrandForegroundLink,
    fontWeight: tokens.fontWeightSemibold,
    boxShadow: tokens.shadow16,
    ':focus': { top: '8px' },
  },
  header: {
    position: 'sticky',
    top: 0,
    zIndex: 20,
    backgroundColor: tokens.colorNeutralBackground1,
    borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
    paddingTop: 'env(safe-area-inset-top)',
  },
  headerInner: {
    height: '64px',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: '8px',
    padding: '0 8px 0 16px',
    boxSizing: 'border-box',
  },
  brandLink: {
    display: 'inline-flex',
    minWidth: 0,
    minHeight: '44px',
    alignItems: 'center',
    textDecorationLine: 'none',
    borderRadius: tokens.borderRadiusMedium,
    ':focus-visible': { outline: `3px solid ${tokens.colorStrokeFocus2}`, outlineOffset: '2px' },
  },
  headerActions: { display: 'flex', alignItems: 'center', gap: '4px', flexShrink: 0 },
  body: { display: 'flex', flex: 1, minHeight: 0 },
  main: { flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column', outlineStyle: 'none' },
  withBottomNav: { paddingBottom: 'calc(76px + env(safe-area-inset-bottom))' },
  content: { flex: 1 },
  footer: {
    margin: '8px auto 0',
    padding: '16px',
    maxWidth: '880px',
    width: '100%',
    boxSizing: 'border-box',
    display: 'flex',
    flexDirection: 'column',
    gap: '6px',
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase200,
    lineHeight: tokens.lineHeightBase200,
    borderTop: `1px solid ${tokens.colorNeutralStroke3}`,
  },
  footerLinks: { display: 'flex', flexWrap: 'wrap', gap: '4px 16px' },
});

/** Where the brand mark leads: home when signed in, the sign-in page otherwise. */
type BrandTarget = '/home' | '/sign-in';

/**
 * The frame around every page: header (brand, language, whose records), the connection banner, navigation (left
 * column on wide screens, bottom bar on phones) and a footer with help and the emergency number.
 */
export function PortalFrame({
  children,
  profileSwitcher,
  navigation = true,
  brandTarget = '/home',
}: {
  children: ReactNode;
  profileSwitcher?: ReactNode;
  navigation?: boolean;
  brandTarget?: BrandTarget;
}) {
  const styles = useStyles();
  const t = useT();
  const wide = useMediaQuery(WIDE_LAYOUT_QUERY);
  const narrowPhone = useMediaQuery('(max-width: 359px)');

  return (
    <div className={styles.app}>
      <a
        href="#main"
        className={styles.skip}
        onClick={(event) => {
          event.preventDefault();
          document.getElementById('main')?.focus();
        }}
      >
        {t('common.skipToContent')}
      </a>
      <header className={styles.header}>
        <div className={styles.headerInner}>
          <Link to={brandTarget} className={styles.brandLink}>
            <BrandMark compact={narrowPhone} />
          </Link>
          <div className={styles.headerActions}>
            <LanguageMenu />
            {profileSwitcher}
          </div>
        </div>
      </header>
      <ConnectionBanner />
      <div className={styles.body}>
        {navigation && wide && <SideNav />}
        <main
          id="main"
          tabIndex={-1}
          className={mergeClasses(styles.main, navigation && !wide && styles.withBottomNav)}
        >
          <div className={styles.content}>{children}</div>
          <footer className={styles.footer}>
            <span>{t('shell.help')}</span>
            <span>{t('shell.emergency')}</span>
            <span className={styles.footerLinks}>
              <TextLink to="/privacy">{t('nav.privacy')}</TextLink>
              <TextLink to="/verify">{t('nav.verify')}</TextLink>
            </span>
          </footer>
        </main>
      </div>
      {navigation && !wide && <BottomNav />}
    </div>
  );
}
