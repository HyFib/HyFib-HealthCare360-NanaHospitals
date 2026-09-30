import { makeStyles, mergeClasses, tokens } from '@fluentui/react-components';
import { ShieldCheckmarkRegular } from '@fluentui/react-icons';
import { QRCodeSVG } from 'qrcode.react';
import type { ReactNode } from 'react';
import { useT } from '@/i18n';
import type { ReportSignatory } from '@/lib/types';
import { rangePosition, type NumericRange } from './labResults';
import { REPORT_QR_ID } from './verifyLink';

const useStyles = makeStyles({
  card: {
    display: 'flex',
    flexDirection: 'column',
    gap: '12px',
    padding: '16px',
    borderRadius: tokens.borderRadiusXLarge,
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    '@media (min-width: 900px)': { padding: '20px 24px' },
  },
  cardTitle: {
    margin: 0,
    fontSize: tokens.fontSizeBase500,
    lineHeight: tokens.lineHeightBase500,
    fontWeight: tokens.fontWeightSemibold,
  },
  signers: { listStyle: 'none', margin: 0, padding: 0, display: 'flex', flexDirection: 'column', gap: '6px' },
  signer: { display: 'flex', flexDirection: 'column' },
  signerName: { fontWeight: tokens.fontWeightSemibold },
  muted: { color: tokens.colorNeutralForeground2, fontSize: tokens.fontSizeBase300 },
  verification: {
    display: 'flex',
    flexWrap: 'wrap',
    gap: '12px',
    alignItems: 'center',
    color: tokens.colorNeutralForeground2,
    margin: 0,
  },
  verificationText: { display: 'flex', gap: '10px', alignItems: 'flex-start', flex: '1 1 220px', minWidth: 0 },
  verificationIcon: { flexShrink: 0, marginTop: '2px' },
  qrBox: {
    padding: '8px',
    backgroundColor: '#fff',
    borderRadius: tokens.borderRadiusLarge,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    lineHeight: 0,
  },
  code: {
    fontFamily: 'Consolas, "Courier New", monospace',
    letterSpacing: '0.08em',
    color: tokens.colorNeutralForeground1,
    fontWeight: tokens.fontWeightSemibold,
  },
  bar: {
    position: 'relative',
    height: '10px',
    borderRadius: tokens.borderRadiusCircular,
    display: 'flex',
    overflow: 'hidden',
    marginTop: '4px',
  },
  zoneOut: { backgroundColor: tokens.colorNeutralBackground5 },
  zoneIn: { backgroundColor: tokens.colorStatusSuccessBackground3 },
  marker: {
    position: 'absolute',
    top: '-3px',
    width: '6px',
    height: '16px',
    marginLeft: '-3px',
    borderRadius: tokens.borderRadiusSmall,
    backgroundColor: tokens.colorNeutralForeground1,
    boxShadow: `0 0 0 2px ${tokens.colorNeutralBackground1}`,
  },
  barWrap: { position: 'relative', paddingTop: '3px', paddingBottom: '3px' },
});

/** A titled white card; the building block of every report page. */
export function ReportCard({
  title,
  children,
  className,
}: {
  title?: ReactNode;
  children: ReactNode;
  className?: string;
}) {
  const styles = useStyles();
  return (
    <section className={mergeClasses(styles.card, className)}>
      {title && <h2 className={styles.cardTitle}>{title}</h2>}
      {children}
    </section>
  );
}

export function Signatories({ signatories }: { signatories: ReportSignatory[] }) {
  const styles = useStyles();
  const t = useT();
  if (signatories.length === 0) {
    return null;
  }
  return (
    <ReportCard title={t('reports.signedBy')}>
      <ul className={styles.signers}>
        {signatories.map((signer) => (
          <li key={signer.userId} className={styles.signer}>
            <span className={styles.signerName}>{signer.name}</span>
            {(signer.qualification || signer.registrationNumber) && (
              <span className={styles.muted}>
                {[signer.qualification, signer.registrationNumber].filter(Boolean).join(' · ')}
              </span>
            )}
          </li>
        ))}
      </ul>
    </ReportCard>
  );
}

/** The report's verification code, with a QR code that opens the public check page (also printed on the copy). */
export function VerificationCode({ code, url }: { code: string | null; url?: string | null }) {
  const styles = useStyles();
  const t = useT();
  if (!code) {
    return null;
  }
  return (
    <div className={styles.verification}>
      {url && (
        <div className={styles.qrBox} id={REPORT_QR_ID}>
          <QRCodeSVG value={url} size={112} level="M" marginSize={0} title={t('reports.qr.label')} />
        </div>
      )}
      <p className={styles.verificationText}>
        <ShieldCheckmarkRegular aria-hidden className={styles.verificationIcon} />
        <span>
          {t('reports.verification', { code: '' })}
          <span className={styles.code}>{code}</span>
          <br />
          {t('reports.verificationHint')}
        </span>
      </p>
    </div>
  );
}

/**
 * Where a value sits against its normal range: grey outside, green inside, a marker at the value. Decorative only
 * (the words "Above normal" and the range text say the same thing to screen readers).
 */
export function RangeBar({ value, range }: { value: number; range: NumericRange }) {
  const styles = useStyles();
  const left = range.low !== null ? 25 : 0;
  const right = range.high !== null ? 25 : 0;
  return (
    <div className={styles.barWrap} aria-hidden>
      <div className={styles.bar}>
        {left > 0 && <span className={styles.zoneOut} style={{ width: `${left}%` }} />}
        <span className={styles.zoneIn} style={{ width: `${100 - left - right}%` }} />
        {right > 0 && <span className={styles.zoneOut} style={{ width: `${right}%` }} />}
      </div>
      <span className={styles.marker} style={{ left: `${rangePosition(value, range) * 100}%` }} />
    </div>
  );
}
