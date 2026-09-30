import { makeStyles, mergeClasses, tokens } from '@fluentui/react-components';
import type { ReactNode } from 'react';

const useStyles = makeStyles({
  list: {
    display: 'grid',
    gridTemplateColumns: 'minmax(0, 1fr)',
    gap: '12px 24px',
    margin: 0,
    '@media (min-width: 600px)': { gridTemplateColumns: 'repeat(2, minmax(0, 1fr))' },
  },
  single: { '@media (min-width: 600px)': { gridTemplateColumns: 'minmax(0, 1fr)' } },
  item: { display: 'flex', flexDirection: 'column', gap: '2px', minWidth: 0 },
  label: {
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase200,
    lineHeight: tokens.lineHeightBase200,
  },
  value: {
    margin: 0,
    fontSize: tokens.fontSizeBase400,
    lineHeight: tokens.lineHeightBase400,
    overflowWrap: 'anywhere',
  },
});

export interface InfoItem {
  label: ReactNode;
  value: ReactNode;
  /** Hidden when empty (null/undefined/''), so detail pages never show blank rows. */
  hideEmpty?: boolean;
}

/** Label/value pairs as a description list (read correctly by screen readers), two columns on wider screens. */
export function InfoList({ items, singleColumn }: { items: InfoItem[]; singleColumn?: boolean }) {
  const styles = useStyles();
  const visible = items.filter(
    (item) => !(item.hideEmpty && (item.value === null || item.value === undefined || item.value === '')),
  );
  return (
    <dl className={mergeClasses(styles.list, singleColumn && styles.single)}>
      {visible.map((item, index) => (
        <div key={index} className={styles.item}>
          <dt className={styles.label}>{item.label}</dt>
          <dd className={styles.value}>{item.value}</dd>
        </div>
      ))}
    </dl>
  );
}
