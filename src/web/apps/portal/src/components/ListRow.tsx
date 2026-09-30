import { makeStyles, mergeClasses, tokens } from '@fluentui/react-components';
import { ChevronRightRegular } from '@fluentui/react-icons';
import type { ReactNode } from 'react';

export type Tone = 'brand' | 'success' | 'warning' | 'danger' | 'neutral';

const useStyles = makeStyles({
  icon: {
    flexShrink: 0,
    width: '44px',
    height: '44px',
    borderRadius: '50%',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    fontSize: '22px',
  },
  brand: { backgroundColor: tokens.colorBrandBackground2, color: tokens.colorBrandForeground2 },
  success: { backgroundColor: tokens.colorStatusSuccessBackground1, color: tokens.colorStatusSuccessForeground1 },
  warning: { backgroundColor: tokens.colorStatusWarningBackground1, color: tokens.colorStatusWarningForeground3 },
  danger: { backgroundColor: tokens.colorStatusDangerBackground1, color: tokens.colorStatusDangerForeground1 },
  neutral: { backgroundColor: tokens.colorNeutralBackground3, color: tokens.colorNeutralForeground2 },
  text: { display: 'flex', flexDirection: 'column', gap: '2px', minWidth: 0, flex: 1 },
  title: {
    fontSize: tokens.fontSizeBase400,
    lineHeight: tokens.lineHeightBase400,
    fontWeight: tokens.fontWeightSemibold,
    overflowWrap: 'anywhere',
  },
  subtitle: {
    color: tokens.colorNeutralForeground2,
    fontSize: tokens.fontSizeBase300,
    lineHeight: tokens.lineHeightBase300,
    overflowWrap: 'anywhere',
  },
  meta: {
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase200,
    lineHeight: tokens.lineHeightBase200,
  },
  trailing: {
    flexShrink: 0,
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'flex-end',
    gap: '4px',
    textAlign: 'end',
  },
  chevron: { flexShrink: 0, color: tokens.colorNeutralForeground3, fontSize: '20px' },
  card: {
    display: 'flex',
    alignItems: 'center',
    gap: '12px',
    minHeight: '64px',
    padding: '12px 14px',
    boxSizing: 'border-box',
    backgroundColor: tokens.colorNeutralBackground1,
    borderRadius: tokens.borderRadiusXLarge,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
  },
});

/** The same card look as a `CardLink`, for rows that are not links (status lists, history). */
export function RowCard({ children }: { children: ReactNode }) {
  const styles = useStyles();
  return <div className={styles.card}>{children}</div>;
}

/**
 * The content of a list card: a tinted icon, what it is, when/who, and its status on the right. Put it inside a
 * `CardLink` so the whole card is one large target.
 */
export function ListRow({
  icon,
  tone = 'brand',
  title,
  subtitle,
  meta,
  trailing,
  chevron = true,
}: {
  icon?: ReactNode;
  tone?: Tone;
  title: ReactNode;
  subtitle?: ReactNode;
  meta?: ReactNode;
  trailing?: ReactNode;
  chevron?: boolean;
}) {
  const styles = useStyles();
  return (
    <>
      {icon && (
        <span className={mergeClasses(styles.icon, styles[tone])} aria-hidden>
          {icon}
        </span>
      )}
      <span className={styles.text}>
        <span className={styles.title}>{title}</span>
        {subtitle && <span className={styles.subtitle}>{subtitle}</span>}
        {meta && <span className={styles.meta}>{meta}</span>}
      </span>
      {trailing && <span className={styles.trailing}>{trailing}</span>}
      {chevron && <ChevronRightRegular className={styles.chevron} aria-hidden />}
    </>
  );
}

export function ToneIcon({ tone = 'brand', children, size = 44 }: { tone?: Tone; children: ReactNode; size?: number }) {
  const styles = useStyles();
  return (
    <span
      className={mergeClasses(styles.icon, styles[tone])}
      style={{ width: size, height: size, fontSize: Math.round(size / 2) }}
      aria-hidden
    >
      {children}
    </span>
  );
}
