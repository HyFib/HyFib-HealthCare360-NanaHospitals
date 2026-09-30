import { makeStyles, mergeClasses, tokens } from '@fluentui/react-components';
import { useEffect, useRef, type ReactNode } from 'react';
import { useT } from '@/i18n';

const useStyles = makeStyles({
  page: {
    display: 'flex',
    flexDirection: 'column',
    gap: '16px',
    width: '100%',
    marginInline: 'auto',
    padding: '12px 16px 24px',
    boxSizing: 'border-box',
    '@media (min-width: 900px)': { padding: '24px 32px 40px', gap: '20px' },
  },
  narrow: { maxWidth: '640px' },
  regular: { maxWidth: '880px' },
  wide: { maxWidth: '1120px' },
  header: { display: 'flex', flexWrap: 'wrap', alignItems: 'flex-end', justifyContent: 'space-between', gap: '12px' },
  headingBlock: { display: 'flex', flexDirection: 'column', gap: '4px', minWidth: 0, flex: '1 1 260px' },
  title: {
    margin: 0,
    fontSize: tokens.fontSizeBase600,
    lineHeight: tokens.lineHeightBase600,
    fontWeight: tokens.fontWeightSemibold,
    color: tokens.colorNeutralForeground1,
    overflowWrap: 'anywhere',
    ':focus-visible': {
      outline: `3px solid ${tokens.colorStrokeFocus2}`,
      outlineOffset: '4px',
      borderRadius: tokens.borderRadiusMedium,
    },
    '@media (min-width: 900px)': { fontSize: tokens.fontSizeHero700, lineHeight: tokens.lineHeightHero700 },
  },
  subtitle: {
    margin: 0,
    color: tokens.colorNeutralForeground2,
    fontSize: tokens.fontSizeBase400,
    lineHeight: tokens.lineHeightBase400,
  },
  actions: { display: 'flex', flexWrap: 'wrap', gap: '8px', alignItems: 'center' },
});

let firstPageShown = false;

/**
 * Page anatomy for every portal screen: optional "back" link, one h1, a short line of context, actions, then content.
 * It sets the browser tab title and, after an in-app navigation, moves keyboard/screen-reader focus to the heading so
 * the new page is announced.
 */
export function PortalPage({
  title,
  documentTitle,
  subtitle,
  back,
  actions,
  width = 'regular',
  children,
}: {
  title: ReactNode;
  /** Tab title when `title` is not plain text. */
  documentTitle?: string;
  subtitle?: ReactNode;
  back?: ReactNode;
  actions?: ReactNode;
  width?: 'narrow' | 'regular' | 'wide';
  children: ReactNode;
}) {
  const styles = useStyles();
  const t = useT();
  const heading = useRef<HTMLHeadingElement>(null);
  const tabTitle = documentTitle ?? (typeof title === 'string' ? title : undefined);
  const appName = t('app.name');

  useEffect(() => {
    if (tabTitle) {
      document.title = `${tabTitle} · ${appName}`;
    }
  }, [tabTitle, appName]);

  useEffect(() => {
    if (!firstPageShown) {
      firstPageShown = true;
      return;
    }
    heading.current?.focus({ preventScroll: false });
  }, []);

  return (
    <div className={mergeClasses(styles.page, styles[width])}>
      {back}
      <header className={styles.header}>
        <div className={styles.headingBlock}>
          <h1 ref={heading} tabIndex={-1} className={styles.title}>
            {title}
          </h1>
          {subtitle && <p className={styles.subtitle}>{subtitle}</p>}
        </div>
        {actions && <div className={styles.actions}>{actions}</div>}
      </header>
      {children}
    </div>
  );
}
