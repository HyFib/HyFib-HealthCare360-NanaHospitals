import { Caption1, makeStyles, mergeClasses, Text, tokens } from '@fluentui/react-components';
import type { ReactNode } from 'react';

const useStyles = makeStyles({
  grid: { display: 'grid', gap: '14px 24px', alignItems: 'start' },
  item: { display: 'flex', flexDirection: 'column', gap: '2px', minWidth: 0 },
  label: { color: tokens.colorNeutralForeground3 },
  value: { overflowWrap: 'anywhere' },
  mono: { fontFamily: tokens.fontFamilyMonospace },
  wide: { gridColumn: '1 / -1' },
});

/** Read-only label/value grid used on profile and detail screens. */
export function DetailGrid({ children, minColumnWidth = 220 }: { children: ReactNode; minColumnWidth?: number }) {
  const styles = useStyles();
  return (
    <div
      className={styles.grid}
      style={{ gridTemplateColumns: `repeat(auto-fill, minmax(min(100%, ${minColumnWidth}px), 1fr))` }}
    >
      {children}
    </div>
  );
}

/** One labelled value; empty values show an em dash so blanks never look like missing data. */
export function Detail({
  label,
  children,
  mono,
  wide,
}: {
  label: string;
  children?: ReactNode;
  mono?: boolean;
  wide?: boolean;
}) {
  const styles = useStyles();
  const empty = children === null || children === undefined || children === '' || children === false;
  return (
    <div className={mergeClasses(styles.item, wide && styles.wide)}>
      <Caption1 className={styles.label}>{label}</Caption1>
      <Text className={mergeClasses(styles.value, mono && styles.mono)}>{empty ? '—' : children}</Text>
    </div>
  );
}
