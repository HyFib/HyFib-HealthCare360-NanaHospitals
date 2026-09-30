import {
  Breadcrumb,
  BreadcrumbButton,
  BreadcrumbDivider,
  BreadcrumbItem,
  makeStyles,
  mergeClasses,
  Subtitle2,
  Text,
  Title2,
  tokens,
} from '@fluentui/react-components';
import { Fragment, useEffect, type CSSProperties, type ReactNode } from 'react';

const useStyles = makeStyles({
  page: {
    display: 'flex',
    flexDirection: 'column',
    gap: '16px',
    padding: '20px 24px 32px',
    marginInline: 'auto',
    width: '100%',
    boxSizing: 'border-box',
    '@media (max-width: 640px)': { padding: '12px 16px 24px' },
  },
  header: {
    display: 'flex',
    alignItems: 'flex-start',
    justifyContent: 'space-between',
    gap: '16px',
    flexWrap: 'wrap',
  },
  headerText: { display: 'flex', flexDirection: 'column', gap: '2px', minWidth: 0 },
  titleRow: { display: 'flex', alignItems: 'center', gap: '10px' },
  icon: { color: tokens.colorBrandForeground1, fontSize: '28px', display: 'flex' },
  subtitle: { color: tokens.colorNeutralForeground3 },
  actions: { display: 'flex', gap: '8px', flexWrap: 'wrap', alignItems: 'center' },
  section: {
    backgroundColor: tokens.colorNeutralBackground1,
    borderRadius: tokens.borderRadiusLarge,
    boxShadow: tokens.shadow4,
    display: 'flex',
    flexDirection: 'column',
    minWidth: 0,
  },
  sectionHeader: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: '12px',
    padding: '14px 16px 0',
    flexWrap: 'wrap',
  },
  sectionBody: { padding: '12px 16px 16px' },
  sectionBodyFlush: { padding: '8px 0 0' },
  sectionDescription: { color: tokens.colorNeutralForeground3 },
  grid: { display: 'grid', gap: '12px 16px', alignItems: 'start' },
  filterBar: { display: 'flex', gap: '8px', flexWrap: 'wrap', alignItems: 'end' },
});

export function Page({
  children,
  maxWidth,
  className,
}: {
  children: ReactNode;
  maxWidth?: number;
  className?: string;
}) {
  const styles = useStyles();
  return (
    <main className={mergeClasses(styles.page, className)} style={maxWidth ? { maxWidth } : undefined}>
      {children}
    </main>
  );
}

export interface Crumb {
  label: string;
  onClick?: () => void;
}

/** Title, context and primary actions — the same anatomy on every screen (SRS UX-07). */
export function PageHeader({
  title,
  subtitle,
  icon,
  breadcrumbs,
  actions,
}: {
  title: ReactNode;
  subtitle?: ReactNode;
  icon?: ReactNode;
  breadcrumbs?: Crumb[];
  actions?: ReactNode;
}) {
  const styles = useStyles();
  // The window/taskbar title follows the screen (HC360 Desktop shows it in the taskbar).
  useEffect(() => {
    if (typeof title === 'string') {
      document.title = `${title} · HealthCare360`;
    }
  }, [title]);
  return (
    <header className={styles.header}>
      <div className={styles.headerText}>
        {breadcrumbs && breadcrumbs.length > 0 && (
          <Breadcrumb size="small" aria-label="Breadcrumb">
            {breadcrumbs.map((crumb, index) => (
              <Fragment key={crumb.label}>
                <BreadcrumbItem>
                  <BreadcrumbButton onClick={crumb.onClick} current={index === breadcrumbs.length - 1}>
                    {crumb.label}
                  </BreadcrumbButton>
                </BreadcrumbItem>
                {index < breadcrumbs.length - 1 && <BreadcrumbDivider />}
              </Fragment>
            ))}
          </Breadcrumb>
        )}
        <div className={styles.titleRow}>
          {icon && <span className={styles.icon}>{icon}</span>}
          <Title2 as="h1">{title}</Title2>
        </div>
        {subtitle && <Text className={styles.subtitle}>{subtitle}</Text>}
      </div>
      {actions && <div className={styles.actions}>{actions}</div>}
    </header>
  );
}

/** A titled card region. Use `flush` for tables that should run edge to edge. */
export function Section({
  title,
  description,
  actions,
  children,
  flush,
  className,
  style,
}: {
  title?: ReactNode;
  description?: ReactNode;
  actions?: ReactNode;
  children: ReactNode;
  flush?: boolean;
  className?: string;
  style?: CSSProperties;
}) {
  const styles = useStyles();
  return (
    <section className={mergeClasses(styles.section, className)} style={style}>
      {(title || actions) && (
        <div className={styles.sectionHeader}>
          <div>
            {title && <Subtitle2 as="h2">{title}</Subtitle2>}
            {description && (
              <div>
                <Text size={200} className={styles.sectionDescription}>
                  {description}
                </Text>
              </div>
            )}
          </div>
          {actions && <div className={styles.actions}>{actions}</div>}
        </div>
      )}
      <div className={flush ? styles.sectionBodyFlush : styles.sectionBody}>{children}</div>
    </section>
  );
}

/** Responsive form/detail grid: `columns` on wide screens, collapsing to one column on phones/tablets. */
export function FormGrid({
  columns = 3,
  children,
  minColumnWidth = 220,
}: {
  columns?: number;
  children: ReactNode;
  minColumnWidth?: number;
}) {
  const styles = useStyles();
  return (
    <div
      className={styles.grid}
      style={{
        gridTemplateColumns: `repeat(auto-fill, minmax(min(100%, max(${minColumnWidth}px, calc((100% - ${(columns - 1) * 16}px) / ${columns}))), 1fr))`,
      }}
    >
      {children}
    </div>
  );
}

export function FilterBar({ children }: { children: ReactNode }) {
  const styles = useStyles();
  return <div className={styles.filterBar}>{children}</div>;
}

/** Spans a FormGrid item across the full row. */
export function FullRow({ children }: { children: ReactNode }) {
  return <div style={{ gridColumn: '1 / -1' }}>{children}</div>;
}
