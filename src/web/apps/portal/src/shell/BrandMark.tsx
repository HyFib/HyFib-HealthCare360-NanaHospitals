import { makeStyles, tokens } from '@fluentui/react-components';
import { useT } from '@/i18n';
import { useHospitalName } from './hooks';

const useStyles = makeStyles({
  root: { display: 'inline-flex', alignItems: 'center', gap: '10px', minWidth: 0 },
  text: { display: 'flex', flexDirection: 'column', minWidth: 0, lineHeight: 1.15 },
  hospital: {
    fontWeight: tokens.fontWeightSemibold,
    fontSize: tokens.fontSizeBase400,
    color: tokens.colorNeutralForeground1,
    whiteSpace: 'nowrap',
    overflow: 'hidden',
    textOverflow: 'ellipsis',
  },
  product: { fontSize: tokens.fontSizeBase200, color: tokens.colorNeutralForeground3, whiteSpace: 'nowrap' },
});

export function LogoMark({ size = 32 }: { size?: number }) {
  return (
    <svg width={size} height={size} viewBox="0 0 64 64" aria-hidden="true" focusable="false" style={{ flexShrink: 0 }}>
      <rect width="64" height="64" rx="14" fill="#0A7CA7" />
      <path d="M27 14h10v13h13v10H37v13H27V37H14V27h13z" fill="#fff" />
      <circle cx="48" cy="48" r="7" fill="#7AB8D7" />
    </svg>
  );
}

/** Logo, hospital name and "Patient portal" — the same mark on every screen. */
export function BrandMark({ compact }: { compact?: boolean }) {
  const styles = useStyles();
  const t = useT();
  const hospital = useHospitalName();
  return (
    <span className={styles.root}>
      <LogoMark />
      {!compact && (
        <span className={styles.text}>
          <span className={styles.hospital}>{hospital}</span>
          <span className={styles.product}>{t('app.name')}</span>
        </span>
      )}
    </span>
  );
}
