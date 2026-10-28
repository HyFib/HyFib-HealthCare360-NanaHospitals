import {
  Button,
  makeStyles,
  Menu,
  MenuItem,
  MenuList,
  MenuPopover,
  MenuTrigger,
  mergeClasses,
  shorthands,
  tokens,
} from '@fluentui/react-components';
import { ArrowSwapRegular, CompassNorthwestRegular } from '@fluentui/react-icons';
import { useEffect, useState } from 'react';
import type { DemoMeta } from './install';
import { PERSONAS } from './personas';
import { currentRole, MISSED, ROLE_CHANGED } from './replay';

const useStyles = makeStyles({
  // At the foot of the navigation column and no wider than it (the page gives the navigation room to scroll its last
  // items above this); kept clear of a phone's rounded corners.
  root: {
    position: 'fixed',
    left: 'max(12px, env(safe-area-inset-left, 0px))',
    bottom: 'max(12px, env(safe-area-inset-bottom, 0px))',
    zIndex: 1000,
    width: '236px',
    maxWidth: 'calc(100vw - 24px)',
    boxSizing: 'border-box',
    display: 'grid',
    gap: '2px',
    padding: '8px 6px 6px 12px',
    backgroundColor: tokens.colorNeutralBackground1,
    ...shorthands.border('1px', 'solid', tokens.colorNeutralStroke1),
    borderRadius: tokens.borderRadiusLarge,
    boxShadow: tokens.shadow16,
  },
  // The patient portal keeps its menu down the left on a wide screen and along the foot on a phone (below 900 px,
  // apps/portal's WIDE_LAYOUT_QUERY): the badge takes the other corner, above the phone's menu bar.
  portal: {
    left: 'auto',
    right: 'max(12px, env(safe-area-inset-right, 0px))',
    '@media (max-width: 899px)': { bottom: 'calc(88px + env(safe-area-inset-bottom, 0px))' },
  },
  tag: {
    fontSize: tokens.fontSizeBase100,
    fontWeight: tokens.fontWeightBold,
    letterSpacing: '0.1em',
    textTransform: 'uppercase',
    color: tokens.colorPaletteMarigoldForeground2,
  },
  who: {
    minWidth: 0,
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground2,
  },
  missed: {
    fontSize: tokens.fontSizeBase200,
    lineHeight: tokens.lineHeightBase200,
    color: tokens.colorNeutralForeground1,
  },
  actions: { display: 'flex', gap: '2px', marginLeft: '-8px' },
});

/** How long the badge explains a screen the demo has no recording for. */
const MISSED_FOR_MS = 8000;

export interface DemoBadgeProps {
  meta: DemoMeta;
  /** The app the badge sits over: it keeps clear of that app's menu. */
  app: 'staff' | 'portal';
  /** Opens a screen of the app, as a link inside it would. */
  navigate: (path: string) => void;
  /** Back to the demo's front page, to choose another role. */
  changeRole: () => void;
}

/**
 * Says, on every screen, that this is the read-only demo and who you are in it; leads to the role's tour, and explains
 * a screen the walk did not record (the app shows its own "not found" there).
 */
export function DemoBadge({ meta, app, navigate, changeRole }: DemoBadgeProps) {
  const styles = useStyles();
  const [role, setRole] = useState(currentRole);
  useEffect(() => {
    const update = () => setRole(currentRole());
    window.addEventListener(ROLE_CHANGED, update);
    return () => window.removeEventListener(ROLE_CHANGED, update);
  }, []);
  const [missed, setMissed] = useState(false);
  useEffect(() => {
    let timer: number | undefined;
    const explain = () => {
      setMissed(true);
      window.clearTimeout(timer);
      timer = window.setTimeout(() => setMissed(false), MISSED_FOR_MS);
    };
    window.addEventListener(MISSED, explain);
    return () => {
      window.removeEventListener(MISSED, explain);
      window.clearTimeout(timer);
    };
  }, []);
  const person = meta.roles.find((r) => r.role === role);
  const places = PERSONAS.find((p) => p.role === role)?.places ?? [];

  return (
    <aside className={mergeClasses(styles.root, app === 'portal' && styles.portal)} aria-label="Demo">
      <span className={styles.tag}>Demo · nothing is saved</span>
      {person && (
        <span className={styles.who}>
          {person.displayName}, {person.roleName}
        </span>
      )}
      <span className={styles.missed} role="status">
        {missed &&
          'This screen was not recorded for the demo, so parts of it are missing. The Tour has the ones that were.'}
      </span>
      <span className={styles.actions}>
        {places.length > 0 && (
          <Menu>
            <MenuTrigger disableButtonEnhancement>
              <Button size="small" appearance="subtle" icon={<CompassNorthwestRegular />}>
                Tour
              </Button>
            </MenuTrigger>
            <MenuPopover>
              <MenuList>
                {places.map((place) => (
                  <MenuItem key={place.path} onClick={() => navigate(place.path)}>
                    {place.label}
                  </MenuItem>
                ))}
              </MenuList>
            </MenuPopover>
          </Menu>
        )}
        <Button size="small" appearance="subtle" icon={<ArrowSwapRegular />} onClick={changeRole}>
          {person ? 'Change role' : 'Choose a role'}
        </Button>
      </span>
    </aside>
  );
}
