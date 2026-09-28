import {
  createDarkTheme,
  createLightTheme,
  FluentProvider,
  teamsHighContrastTheme,
  tokens,
  type BrandVariants,
  type Theme,
} from '@fluentui/react-components';
import { useEffect, useMemo, useState, useSyncExternalStore, type ReactNode } from 'react';

/**
 * HyFib clinical teal. Brand 80 (#0A7CA7) carries white text at 4.7:1 (WCAG AA); dark theme lifts
 * foregrounds to brand 110/120 for contrast on dark surfaces.
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

export const hc360LightTheme: Theme = { ...createLightTheme(hc360Brand), fontFamilyBase: fontFamily };

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

/** Applies the HC360 theme to the whole app and sets the page background/color-scheme. */
export function Hc360ThemeProvider({ children }: { children: ReactNode }) {
  const { theme, isDark } = useResolvedTheme();

  useEffect(() => {
    document.documentElement.style.colorScheme = isDark ? 'dark' : 'light';
  }, [isDark]);

  return (
    <FluentProvider theme={theme} style={{ minHeight: '100vh', backgroundColor: tokens.colorNeutralBackground2 }}>
      {children}
    </FluentProvider>
  );
}
