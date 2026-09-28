import type { Theme } from '@fluentui/react-components';

/**
 * Type-scale overrides for patients reading on phones (merged into the HC360 light/dark/high-contrast theme by a
 * nested FluentProvider). Body text and medium controls move from 14 px to 16 px; headings follow. 16 px inputs also
 * stop iOS Safari from zooming the page when a field is focused.
 */
export const portalTypeScale: Partial<Theme> = {
  fontSizeBase200: '13px',
  lineHeightBase200: '18px',
  fontSizeBase300: '16px',
  lineHeightBase300: '22px',
  fontSizeBase400: '17px',
  lineHeightBase400: '24px',
  fontSizeBase500: '20px',
  lineHeightBase500: '28px',
  fontSizeBase600: '24px',
  lineHeightBase600: '32px',
};
