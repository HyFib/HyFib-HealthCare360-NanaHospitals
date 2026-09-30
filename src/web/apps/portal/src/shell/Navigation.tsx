import {
  Button,
  Divider,
  DrawerBody,
  DrawerHeader,
  DrawerHeaderTitle,
  makeStyles,
  mergeClasses,
  OverlayDrawer,
  tokens,
} from '@fluentui/react-components';
import { DismissRegular, NavigationRegular, SignOutRegular } from '@fluentui/react-icons';
import { Link, useLocation } from '@tanstack/react-router';
import { useState } from 'react';
import { useT } from '@/i18n';
import { useSignOut } from './hooks';
import { LanguageChoice } from './LanguageMenu';
import { EXTRA_ITEMS, isActivePath, NAV_ITEMS, type NavItem } from './navItems';

const useStyles = makeStyles({
  side: {
    position: 'sticky',
    top: '64px',
    alignSelf: 'start',
    width: '248px',
    flexShrink: 0,
    height: 'calc(100dvh - 64px)',
    overflowY: 'auto',
    boxSizing: 'border-box',
    padding: '16px 12px',
    display: 'flex',
    flexDirection: 'column',
    gap: '4px',
    borderRight: `1px solid ${tokens.colorNeutralStroke2}`,
    backgroundColor: tokens.colorNeutralBackground1,
  },
  sideList: { listStyle: 'none', margin: 0, padding: 0, display: 'flex', flexDirection: 'column', gap: '2px' },
  sideLink: {
    display: 'flex',
    alignItems: 'center',
    gap: '12px',
    minHeight: '48px',
    padding: '0 14px',
    borderRadius: tokens.borderRadiusLarge,
    color: tokens.colorNeutralForeground2,
    fontSize: tokens.fontSizeBase400,
    textDecorationLine: 'none',
    position: 'relative',
    ':hover': { backgroundColor: tokens.colorSubtleBackgroundHover, color: tokens.colorNeutralForeground1 },
    ':focus-visible': { outline: `3px solid ${tokens.colorStrokeFocus2}`, outlineOffset: '-3px' },
  },
  sideActive: {
    backgroundColor: tokens.colorBrandBackground2,
    color: tokens.colorBrandForeground1,
    fontWeight: tokens.fontWeightSemibold,
    ':hover': { backgroundColor: tokens.colorBrandBackground2Hover, color: tokens.colorBrandForeground1 },
    '::before': {
      content: '""',
      position: 'absolute',
      left: '0',
      top: '12px',
      bottom: '12px',
      width: '3px',
      borderRadius: tokens.borderRadiusCircular,
      backgroundColor: tokens.colorBrandForeground1,
    },
  },
  icon: { fontSize: '22px', flexShrink: 0 },
  sideFooter: { marginTop: 'auto', paddingTop: '12px', display: 'flex', flexDirection: 'column', gap: '8px' },
  bottom: {
    position: 'fixed',
    left: 0,
    right: 0,
    bottom: 0,
    zIndex: 10,
    display: 'grid',
    gridTemplateColumns: 'repeat(5, minmax(0, 1fr))',
    backgroundColor: tokens.colorNeutralBackground1,
    borderTop: `1px solid ${tokens.colorNeutralStroke2}`,
    boxShadow: tokens.shadow8,
    paddingBottom: 'env(safe-area-inset-bottom)',
  },
  bottomItem: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    justifyContent: 'center',
    gap: '2px',
    minHeight: '60px',
    padding: '4px 2px',
    color: tokens.colorNeutralForeground2,
    fontSize: tokens.fontSizeBase200,
    lineHeight: tokens.lineHeightBase200,
    textDecorationLine: 'none',
    textAlign: 'center',
    backgroundColor: 'transparent',
    border: 'none',
    fontFamily: 'inherit',
    cursor: 'pointer',
    WebkitTapHighlightColor: 'transparent',
    ':focus-visible': { outline: `3px solid ${tokens.colorStrokeFocus2}`, outlineOffset: '-3px' },
  },
  bottomIcon: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    width: '56px',
    height: '28px',
    borderRadius: tokens.borderRadiusCircular,
    fontSize: '22px',
  },
  bottomActive: { color: tokens.colorBrandForeground1, fontWeight: tokens.fontWeightSemibold },
  bottomActiveIcon: { backgroundColor: tokens.colorBrandBackground2 },
  label: { maxWidth: '100%', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' },
  sheet: {
    maxHeight: '88dvh',
    borderTopLeftRadius: tokens.borderRadiusXLarge,
    borderTopRightRadius: tokens.borderRadiusXLarge,
  },
  sheetSection: { display: 'flex', flexDirection: 'column', gap: '12px', padding: '12px 0' },
  sheetTitle: {
    margin: 0,
    fontSize: tokens.fontSizeBase300,
    color: tokens.colorNeutralForeground3,
    fontWeight: tokens.fontWeightSemibold,
  },
});

function SideNavLink({ item, onNavigate }: { item: NavItem; onNavigate?: () => void }) {
  const styles = useStyles();
  const t = useT();
  const Icon = item.icon;
  return (
    <li>
      <Link
        to={item.to}
        className={styles.sideLink}
        activeProps={{ className: mergeClasses(styles.sideLink, styles.sideActive) }}
        onClick={onNavigate}
      >
        <Icon className={styles.icon} aria-hidden />
        <span>{t(item.label)}</span>
      </Link>
    </li>
  );
}

/** Wide screens: every destination in a column on the left. */
export function SideNav() {
  const styles = useStyles();
  const t = useT();
  const signOut = useSignOut();
  return (
    <nav aria-label={t('nav.main')} className={styles.side}>
      <ul className={styles.sideList}>
        {NAV_ITEMS.map((item) => (
          <SideNavLink key={item.to} item={item} />
        ))}
      </ul>
      <div className={styles.sideFooter}>
        <Divider />
        <ul className={styles.sideList}>
          {EXTRA_ITEMS.map((item) => (
            <SideNavLink key={item.to} item={item} />
          ))}
        </ul>
        <Button
          appearance="subtle"
          size="large"
          icon={<SignOutRegular />}
          onClick={() => void signOut()}
          style={{ justifyContent: 'flex-start' }}
        >
          {t('common.signOut')}
        </Button>
      </div>
    </nav>
  );
}

/** Phones: the four most used destinations plus "More", always under the thumb. */
export function BottomNav() {
  const styles = useStyles();
  const t = useT();
  const pathname = useLocation({ select: (location) => location.pathname });
  const [moreOpen, setMoreOpen] = useState(false);
  const primary = NAV_ITEMS.filter((item) => item.primary);
  const moreActive = !primary.some((item) => isActivePath(pathname, item.to));

  return (
    <>
      <nav aria-label={t('nav.main')} className={styles.bottom}>
        {primary.map((item) => {
          const active = isActivePath(pathname, item.to);
          const Icon = item.icon;
          return (
            <Link key={item.to} to={item.to} className={mergeClasses(styles.bottomItem, active && styles.bottomActive)}>
              <span className={mergeClasses(styles.bottomIcon, active && styles.bottomActiveIcon)}>
                <Icon aria-hidden />
              </span>
              <span className={styles.label}>{t(item.label)}</span>
            </Link>
          );
        })}
        <button
          type="button"
          className={mergeClasses(styles.bottomItem, moreActive && styles.bottomActive)}
          aria-haspopup="dialog"
          aria-expanded={moreOpen}
          onClick={() => setMoreOpen(true)}
        >
          <span className={mergeClasses(styles.bottomIcon, moreActive && styles.bottomActiveIcon)}>
            <NavigationRegular aria-hidden />
          </span>
          <span className={styles.label}>{t('nav.more')}</span>
        </button>
      </nav>
      <MoreSheet open={moreOpen} onClose={() => setMoreOpen(false)} />
    </>
  );
}

function MoreSheet({ open, onClose }: { open: boolean; onClose: () => void }) {
  const styles = useStyles();
  const t = useT();
  const signOut = useSignOut();
  const others = NAV_ITEMS.filter((item) => !item.primary);
  return (
    <OverlayDrawer
      open={open}
      onOpenChange={(_, data) => !data.open && onClose()}
      position="bottom"
      size="large"
      className={styles.sheet}
    >
      <DrawerHeader>
        <DrawerHeaderTitle
          action={
            <Button
              appearance="subtle"
              size="large"
              icon={<DismissRegular />}
              aria-label={t('common.close')}
              onClick={onClose}
            />
          }
        >
          {t('nav.menu')}
        </DrawerHeaderTitle>
      </DrawerHeader>
      <DrawerBody>
        <nav aria-label={t('nav.more')}>
          <ul className={styles.sideList}>
            {others.map((item) => (
              <SideNavLink key={item.to} item={item} onNavigate={onClose} />
            ))}
            {EXTRA_ITEMS.map((item) => (
              <SideNavLink key={item.to} item={item} onNavigate={onClose} />
            ))}
          </ul>
        </nav>
        <div className={styles.sheetSection}>
          <Divider />
          <h2 className={styles.sheetTitle}>{t('shell.language.label')}</h2>
          <LanguageChoice />
          <Divider />
          <Button
            appearance="secondary"
            size="large"
            icon={<SignOutRegular />}
            onClick={() => {
              onClose();
              void signOut();
            }}
          >
            {t('common.signOut')}
          </Button>
        </div>
      </DrawerBody>
    </OverlayDrawer>
  );
}
