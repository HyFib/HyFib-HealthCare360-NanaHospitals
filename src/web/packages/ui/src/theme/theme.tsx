import {
  createDarkTheme,
  createLightTheme,
  FluentProvider,
  makeStaticStyles,
  teamsHighContrastTheme,
  tokens,
  type BrandVariants,
  type Theme,
} from '@fluentui/react-components';
import { useEffect, useMemo, useState, useSyncExternalStore, type ReactNode } from 'react';

/**
 * HyFib clinical teal. Brand 80 (#0A7CA7) carries white text at 4.7:1 (WCAG AA); the light theme draws brand text one
 * step darker than Fluent does (below), and the dark theme lifts foregrounds to brand 110/120 for contrast on dark
 * surfaces.
 */
export const hc360Brand: BrandVariants = {
  10: '#020E12',
  20: '#06202A',
  30: '#073343',
  40: '#074157',
  50: '#064F6B',
  60: '#045E80',
  70: '#016D95',
  80: '#0A7CA7',
  90: '#2F8BB4',
  100: '#4A9AC0',
  110: '#62A9CC',
  120: '#7AB8D7',
  130: '#92C7E1',
  140: '#AAD5EA',
  150: '#C3E3F2',
  160: '#DCF0F9',
};

const fontFamily = '"Segoe UI Variable Text", "Segoe UI", system-ui, -apple-system, "Noto Sans", sans-serif';

/**
 * The teal is lighter than Fluent's own blue, so Fluent's light brand text (brand 80, and brand 70 on a brand tint) falls
 * short of 4.5:1 on the surfaces it is paired with: brand 80 has 4.0:1 on colorBrandBackground2 (a selected link in the
 * portal's navigation) and 4.3:1 on colorNeutralBackground3. One step darker each keeps every pairing at AA: brand 70
 * has 4.9:1 on colorBrandBackground2 and 5.3:1 on colorNeutralBackground3; brand 60, for text on a hovered tint, 5.3:1
 * on colorBrandBackground2Hover. Checked in a browser by a11y/*.spec.ts.
 */
export const hc360LightTheme: Theme = {
  ...createLightTheme(hc360Brand),
  fontFamilyBase: fontFamily,
  colorBrandForeground1: hc360Brand[70],
  colorBrandForeground2: hc360Brand[60],
};

export const hc360DarkTheme: Theme = {
  ...createDarkTheme(hc360Brand),
  fontFamilyBase: fontFamily,
  colorBrandForeground1: hc360Brand[110],
  colorBrandForeground2: hc360Brand[120],
  colorBrandForegroundLink: hc360Brand[120],
};

export const hc360HighContrastTheme: Theme = { ...teamsHighContrastTheme, fontFamilyBase: fontFamily };

export type ThemeMode = 'system' | 'light' | 'dark' | 'high-contrast';

const STORAGE_KEY = 'hc360:theme';
const subscribers = new Set<() => void>();

function readMode(): ThemeMode {
  try {
    const stored = localStorage.getItem(STORAGE_KEY);
    return stored === 'light' || stored === 'dark' || stored === 'high-contrast' ? stored : 'system';
  } catch {
    return 'system';
  }
}

/** User theme preference (per workstation), defaulting to the Windows setting. */
export function useThemeMode(): [ThemeMode, (mode: ThemeMode) => void] {
  const mode = useSyncExternalStore(
    (notify) => {
      subscribers.add(notify);
      return () => subscribers.delete(notify);
    },
    readMode,
    () => 'system' as ThemeMode,
  );
  const setMode = (next: ThemeMode) => {
    try {
      localStorage.setItem(STORAGE_KEY, next);
    } catch {
      // Preference is a convenience only.
    }
    subscribers.forEach((notify) => notify());
  };
  return [mode, setMode];
}

function useMediaQuery(query: string): boolean {
  const [matches, setMatches] = useState(() => typeof window !== 'undefined' && window.matchMedia(query).matches);
  useEffect(() => {
    const media = window.matchMedia(query);
    const onChange = () => setMatches(media.matches);
    media.addEventListener('change', onChange);
    return () => media.removeEventListener('change', onChange);
  }, [query]);
  return matches;
}

export function useResolvedTheme(): { theme: Theme; isDark: boolean; mode: ThemeMode } {
  const [mode] = useThemeMode();
  const prefersDark = useMediaQuery('(prefers-color-scheme: dark)');
  const forcedColors = useMediaQuery('(forced-colors: active)');

  return useMemo(() => {
    if (mode === 'high-contrast' || (mode === 'system' && forcedColors)) {
      return { theme: hc360HighContrastTheme, isDark: true, mode };
    }
    const dark = mode === 'dark' || (mode === 'system' && prefersDark);
    return { theme: dark ? hc360DarkTheme : hc360LightTheme, isDark: dark, mode };
  }, [mode, prefersDark, forcedColors]);
}

/**
 * Text boxes as wide as the column or card they are in, never wider. A browser sizes an input for 20 characters
 * (Fluent's input fills its box but keeps that as its smallest width), wider than a phone's sign-in card or a narrow
 * column of vital signs, so fields spilled out over their neighbours (WCAG 1.4.10). Given a width in per cent, an input
 * no longer holds its box open, and Fluent's own flex layout still fills the box.
 */
const useFieldSizing = makeStaticStyles({
  '.fui-Input__input, .fui-SpinButton__input': { width: '100%' },
});

/** Applies the HC360 theme to the whole app and sets the page background/color-scheme. */
export function Hc360ThemeProvider({ children }: { children: ReactNode }) {
  const { theme, isDark } = useResolvedTheme();
  useFieldSizing();

  useEffect(() => {
    document.documentElement.style.colorScheme = isDark ? 'dark' : 'light';
  }, [isDark]);

  return (
    <FluentProvider theme={theme} style={{ minHeight: '100vh', backgroundColor: tokens.colorNeutralBackground2 }}>
      {children}
    </FluentProvider>
  );
}
