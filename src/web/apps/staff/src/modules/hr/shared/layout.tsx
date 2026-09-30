import { Button, Caption1, makeStyles, mergeClasses, Text, tokens, Tooltip } from '@fluentui/react-components';
import { EyeOffRegular, EyeRegular } from '@fluentui/react-icons';
import { formatInr } from '@hc360/core';
import type { CSSProperties, ReactNode } from 'react';

const useStyles = makeStyles({
  kpis: { display: 'grid', gap: '12px', gridTemplateColumns: 'repeat(auto-fill, minmax(180px, 1fr))' },
  columns: {
    display: 'grid',
    gap: '16px',
    alignItems: 'start',
    gridTemplateColumns: 'minmax(0, 1fr) minmax(0, 1fr)',
    '@media (max-width: 1100px)': { gridTemplateColumns: 'minmax(0, 1fr)' },
  },
  mainAside: {
    display: 'grid',
    gap: '16px',
    alignItems: 'start',
    gridTemplateColumns: 'minmax(0, 1fr) 340px',
    '@media (max-width: 1100px)': { gridTemplateColumns: 'minmax(0, 1fr)' },
  },
  stack: { display: 'flex', flexDirection: 'column', gap: '12px', minWidth: 0 },
  toolbar: { display: 'flex', gap: '8px', alignItems: 'center', flexWrap: 'wrap' },
  muted: { color: tokens.colorNeutralForeground3 },
  masked: { letterSpacing: '2px', color: tokens.colorNeutralForeground3 },
  pad: { padding: '0 16px 12px' },
});

/** Responsive row of KPI cards. */
export function KpiGrid({ children }: { children: ReactNode }) {
  const styles = useStyles();
  return <div className={styles.kpis}>{children}</div>;
}

/** Two equal columns on workstations, one column on tablets. */
export function Columns({ children }: { children: ReactNode }) {
  const styles = useStyles();
  return <div className={styles.columns}>{children}</div>;
}

/** Main content with a 340 px side panel (stacks on tablets). */
export function MainAside({ children }: { children: ReactNode }) {
  const styles = useStyles();
  return <div className={styles.mainAside}>{children}</div>;
}

export function Stack({ children, gap, style }: { children: ReactNode; gap?: number; style?: CSSProperties }) {
  const styles = useStyles();
  return (
    <div className={styles.stack} style={gap === undefined ? style : { ...style, gap }}>
      {children}
    </div>
  );
}

/** A row of buttons/filters; `spaced` adds the gap above a table. */
export function Toolbar({
  children,
  className,
  spaced = true,
  style,
}: {
  children: ReactNode;
  className?: string;
  spaced?: boolean;
  style?: CSSProperties;
}) {
  const styles = useStyles();
  return (
    <div className={mergeClasses(styles.toolbar, className)} style={spaced ? { marginBottom: 12, ...style } : style}>
      {children}
    </div>
  );
}

/** Padding that lines a FilterBar up with a flush Section's table. */
export function FilterPad({ children }: { children: ReactNode }) {
  const styles = useStyles();
  return <div className={styles.pad}>{children}</div>;
}

export function Muted({ children, block }: { children: ReactNode; block?: boolean }) {
  const styles = useStyles();
  return (
    <Caption1 className={styles.muted} block={block}>
      {children}
    </Caption1>
  );
}

/**
 * Salary figure that stays hidden until the viewer chooses to show amounts (screens are often visible to patients and
 * colleagues on the ward).
 */
export function Amount({
  value,
  hidden,
  whole,
}: {
  value: number | null | undefined;
  hidden: boolean;
  whole?: boolean;
}) {
  const styles = useStyles();
  if (hidden) {
    return (
      <Text className={styles.masked} aria-label="Amount hidden">
        ₹ ••••
      </Text>
    );
  }
  return <>{formatInr(value, { whole })}</>;
}

/** "Show amounts / Hide amounts" switch that goes with {@link Amount}. */
export function RevealButton({
  revealed,
  onChange,
  label = 'amounts',
}: {
  revealed: boolean;
  onChange: (revealed: boolean) => void;
  label?: string;
}) {
  return (
    <Tooltip content={revealed ? `Hide ${label} again` : `Show ${label} on this screen`} relationship="description">
      <Button
        appearance="subtle"
        icon={revealed ? <EyeOffRegular /> : <EyeRegular />}
        onClick={() => onChange(!revealed)}
      >
        {revealed ? `Hide ${label}` : `Show ${label}`}
      </Button>
    </Tooltip>
  );
}
