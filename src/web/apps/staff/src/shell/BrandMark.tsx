import { makeStyles, Text, tokens } from '@fluentui/react-components';

const useStyles = makeStyles({
  root: { display: 'inline-flex', alignItems: 'center', gap: '10px', minWidth: 0 },
  text: { display: 'flex', flexDirection: 'column', lineHeight: 1.1, minWidth: 0 },
  product: { fontWeight: tokens.fontWeightSemibold, whiteSpace: 'nowrap' },
  facility: {
    color: tokens.colorNeutralForeground3,
    whiteSpace: 'nowrap',
    overflow: 'hidden',
    textOverflow: 'ellipsis',
  },
});

export function LogoMark({ size = 28 }: { size?: number }) {
  return (
    <svg width={size} height={size} viewBox="0 0 64 64" aria-hidden="true" focusable="false">
      <rect width="64" height="64" rx="14" fill="#0A7CA7" />
      <path d="M27 14h10v13h13v10H37v13H27V37H14V27h13z" fill="#fff" />
      <circle cx="48" cy="48" r="7" fill="#7AB8D7" />
    </svg>
  );
}

export function BrandMark({ facility, compact }: { facility?: string | null; compact?: boolean }) {
  const styles = useStyles();
  return (
    <span className={styles.root}>
      <LogoMark />
      {!compact && (
        <span className={styles.text}>
          <Text className={styles.product}>HealthCare360</Text>
          {facility && (
            <Text size={200} className={styles.facility}>
              {facility}
            </Text>
          )}
        </span>
      )}
    </span>
  );
}
