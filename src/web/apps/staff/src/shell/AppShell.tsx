import {
  Button,
  Caption1,
  Hamburger,
  makeStyles,
  NavCategory,
  NavCategoryItem,
  NavDrawer,
  NavDrawerBody,
  NavItem,
  NavSectionHeader,
  NavSubItem,
  NavSubItemGroup,
  tokens,
  Tooltip,
} from '@fluentui/react-components';
import { HomeRegular, SearchRegular } from '@fluentui/react-icons';
import { useHotkey, useIdle, useSession } from '@hc360/core';
import { Link, useLocation, useNavigate } from '@tanstack/react-router';
import { useEffect, useMemo, useState, type ReactNode } from 'react';
import { useFeatureSwitches } from '@/lib/features';
import { usePublicSettings } from '@/lib/lookups';
import { moduleManifests } from '@/lib/moduleRegistry';
import { matchLink, NAV_SECTIONS, visibleModules } from '@/lib/modules';
import { AssistantButton } from './assistant/AssistantButton';
import { BrandMark } from './BrandMark';
import { CommandPalette } from './CommandPalette';
import { ConnectionIndicator, DegradedBanner, OfflineBanner } from './ConnectionIndicator';
import { NotificationsButton } from './NotificationsButton';
import { UserMenu } from './UserMenu';

const useStyles = makeStyles({
  root: { display: 'grid', gridTemplateRows: '48px minmax(0, 1fr)', height: '100vh', overflow: 'hidden' },
  topbar: {
    display: 'flex',
    alignItems: 'center',
    gap: '8px',
    padding: '0 8px 0 4px',
    backgroundColor: tokens.colorNeutralBackground1,
    borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
    zIndex: 3,
  },
  brandLink: { textDecoration: 'none', color: 'inherit', display: 'inline-flex', minWidth: 0, marginRight: '8px' },
  search: {
    flex: '0 1 440px',
    justifyContent: 'flex-start',
    color: tokens.colorNeutralForeground3,
    fontWeight: tokens.fontWeightRegular,
    marginInline: 'auto',
    '@media (max-width: 640px)': { flex: '0 0 auto' },
  },
  searchText: { flex: 1, textAlign: 'start', '@media (max-width: 640px)': { display: 'none' } },
  kbd: {
    border: `1px solid ${tokens.colorNeutralStroke1}`,
    borderRadius: tokens.borderRadiusSmall,
    padding: '0 6px',
    '@media (max-width: 640px)': { display: 'none' },
  },
  actions: { display: 'flex', alignItems: 'center', gap: '2px' },
  body: { display: 'flex', minHeight: 0 },
  nav: { height: '100%' },
  main: { flex: 1, minWidth: 0, overflowY: 'auto', display: 'flex', flexDirection: 'column', outline: 'none' },
  skip: {
    position: 'absolute',
    left: '-9999px',
    ':focus': { left: '8px', top: '8px', zIndex: 10, padding: '8px', backgroundColor: tokens.colorNeutralBackground1 },
  },
});

function useMediaQuery(query: string): boolean {
  const [matches, setMatches] = useState(() => window.matchMedia(query).matches);
  useEffect(() => {
    const media = window.matchMedia(query);
    const onChange = () => setMatches(media.matches);
    media.addEventListener('change', onChange);
    return () => media.removeEventListener('change', onChange);
  }, [query]);
  return matches;
}

/**
 * The staff application frame: Windows 11-style navigation grouped by hospital area (filtered by the user's
 * permissions), global search (Ctrl+K), connection status, notifications and the account menu.
 */
export function AppShell({ children }: { children: ReactNode }) {
  const styles = useStyles();
  const user = useSession((s) => s.user);
  const setLocked = useSession((s) => s.setLocked);
  const narrow = useMediaQuery('(max-width: 1023px)');
  const [navOpen, setNavOpen] = useState(!narrow);
  const [paletteOpen, setPaletteOpen] = useState(false);
  const { pathname } = useLocation();
  const navigate = useNavigate();
  const settings = usePublicSettings();
  const features = useFeatureSwitches();

  const modules = useMemo(() => visibleModules(user, moduleManifests, features), [user, features]);
  const current = useMemo(() => matchLink(pathname, modules), [pathname, modules]);
  const [openCategories, setOpenCategories] = useState<string[]>([]);

  // Adjust state while rendering when the inputs change (no effect round-trip): collapse the navigation on narrow
  // screens, and open the category of the screen being shown.
  const [lastNarrow, setLastNarrow] = useState(narrow);
  if (lastNarrow !== narrow) {
    setLastNarrow(narrow);
    setNavOpen(!narrow);
  }

  const currentCategory = current && current.module.links.length > 1 ? current.module.id : null;
  const [lastCategory, setLastCategory] = useState<string | null>(null);
  if (lastCategory !== currentCategory) {
    setLastCategory(currentCategory);
    if (currentCategory && !openCategories.includes(currentCategory)) {
      setOpenCategories([currentCategory]);
    }
  }

  const lockMinutes =
    typeof settings.data?.['ui.idleLockMinutes'] === 'number' ? (settings.data['ui.idleLockMinutes'] as number) : 10;
  useIdle(lockMinutes * 60_000, () => setLocked(true), lockMinutes > 0);
  useHotkey('mod+k', () => setPaletteOpen(true), { allowInInputs: true });
  useHotkey('mod+shift+l', () => setLocked(true), { allowInInputs: true, overDialogs: true });

  const sections = NAV_SECTIONS.map((section) => ({
    ...section,
    modules: modules.filter((m) => m.section === section.id),
  })).filter((section) => section.modules.length > 0);

  return (
    <div className={styles.root}>
      <a href="#main" className={styles.skip}>
        Skip to main content
      </a>
      <header className={styles.topbar}>
        <Tooltip content={navOpen ? 'Hide navigation' : 'Show navigation'} relationship="label">
          <Hamburger onClick={() => setNavOpen((open) => !open)} aria-expanded={navOpen} />
        </Tooltip>
        <Link to="/" className={styles.brandLink} aria-label="Home">
          <BrandMark facility={user?.facilityName} compact={narrow} />
        </Link>
        <Button
          className={styles.search}
          appearance="outline"
          icon={<SearchRegular />}
          onClick={() => setPaletteOpen(true)}
          aria-keyshortcuts="Control+K"
        >
          <span className={styles.searchText}>Search patients or screens</span>
          <Caption1 className={styles.kbd}>Ctrl+K</Caption1>
        </Button>
        <div className={styles.actions}>
          <AssistantButton />
          <ConnectionIndicator />
          <NotificationsButton />
          <UserMenu onLock={() => setLocked(true)} />
        </div>
      </header>

      <div className={styles.body}>
        <NavDrawer
          className={styles.nav}
          open={navOpen}
          type={narrow ? 'overlay' : 'inline'}
          onOpenChange={(_, data) => setNavOpen(data.open)}
          selectedValue={current?.link.to ?? (pathname === '/' ? '/' : '')}
          selectedCategoryValue={current && current.module.links.length > 1 ? current.module.id : ''}
          openCategories={openCategories}
          onNavCategoryItemToggle={(_, data) =>
            setOpenCategories((open) =>
              open.includes(String(data.value)) ? open.filter((v) => v !== data.value) : [String(data.value)],
            )
          }
          onNavItemSelect={(_, data) => {
            void navigate({ href: String(data.value) });
            if (narrow) {
              setNavOpen(false);
            }
          }}
          aria-label="Main navigation"
        >
          <NavDrawerBody>
            <NavItem value="/" icon={<HomeRegular />}>
              Home
            </NavItem>
            {sections.map((section) => (
              <div key={section.id} role="group" aria-label={section.label}>
                <NavSectionHeader>{section.label}</NavSectionHeader>
                {section.modules.map((module) => {
                  const Icon = module.icon;
                  const [only] = module.links;
                  if (module.links.length === 1 && only) {
                    return (
                      <NavItem key={module.id} value={only.to} icon={<Icon />}>
                        {module.title}
                      </NavItem>
                    );
                  }
                  return (
                    <NavCategory key={module.id} value={module.id}>
                      <NavCategoryItem icon={<Icon />}>{module.title}</NavCategoryItem>
                      <NavSubItemGroup>
                        {module.links.map((link) => (
                          <NavSubItem key={link.to} value={link.to}>
                            {link.label}
                          </NavSubItem>
                        ))}
                      </NavSubItemGroup>
                    </NavCategory>
                  );
                })}
              </div>
            ))}
          </NavDrawerBody>
        </NavDrawer>

        <div id="main" className={styles.main} tabIndex={-1}>
          <OfflineBanner />
          <DegradedBanner />
          {children}
        </div>
      </div>

      <CommandPalette open={paletteOpen} onOpenChange={setPaletteOpen} />
    </div>
  );
}
