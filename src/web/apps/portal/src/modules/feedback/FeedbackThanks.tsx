import { makeStyles, tokens } from '@fluentui/react-components';
import { CheckmarkCircleRegular } from '@fluentui/react-icons';
import type { ReactNode } from 'react';
import { useT } from '@/i18n';
import type { FeedbackReceipt } from '@/lib/types';

const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    gap: '12px',
    padding: '28px 16px',
    textAlign: 'center',
    borderRadius: tokens.borderRadiusXLarge,
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  icon: { fontSize: '64px', color: tokens.colorStatusSuccessForeground1 },
  title: {
    margin: 0,
    fontSize: tokens.fontSizeBase600,
    lineHeight: tokens.lineHeightBase600,
    fontWeight: tokens.fontWeightSemibold,
  },
  text: {
    margin: 0,
    color: tokens.colorNeutralForeground2,
    fontSize: tokens.fontSizeBase400,
    lineHeight: tokens.lineHeightBase400,
    whiteSpace: 'pre-line',
  },
  complaint: { margin: 0, fontWeight: tokens.fontWeightSemibold, fontSize: tokens.fontSizeBase400 },
});

/** "Thank you" after feedback: the hospital's own message, and the complaint number when a call-back was asked for. */
export function FeedbackThanks({ receipt, action }: { receipt: FeedbackReceipt; action?: ReactNode }) {
  const styles = useStyles();
  const t = useT();
  return (
    <div className={styles.root} role="status">
      <CheckmarkCircleRegular className={styles.icon} aria-hidden />
      <h2 className={styles.title}>{t('feedback.done.title')}</h2>
      {receipt.thankYou.trim() && <p className={styles.text}>{receipt.thankYou.trim()}</p>}
      {receipt.complaintNumber && (
        <p className={styles.complaint}>{t('feedback.done.complaint', { number: receipt.complaintNumber })}</p>
      )}
      {action}
    </div>
  );
}
