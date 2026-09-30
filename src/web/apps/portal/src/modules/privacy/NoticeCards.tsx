import { makeStyles, tokens, useId } from '@fluentui/react-components';
import { ShieldErrorRegular } from '@fluentui/react-icons';
import { CardLink } from '@/components/links';
import { ListRow } from '@/components/ListRow';
import { useT } from '@/i18n';
import { useFormat } from '@/i18n/useFormat';
import type { PortalBreachNotice } from '@/lib/types';

const useStyles = makeStyles({
  section: { display: 'flex', flexDirection: 'column', gap: '10px' },
  title: {
    margin: 0,
    fontSize: tokens.fontSizeBase500,
    lineHeight: tokens.lineHeightBase500,
    fontWeight: tokens.fontWeightSemibold,
  },
  card: {
    border: `2px solid ${tokens.colorStatusWarningBorder1}`,
    backgroundColor: tokens.colorStatusWarningBackground1,
    ':hover': { backgroundColor: tokens.colorStatusWarningBackground2 },
  },
});

/**
 * Notices the hospital sent about an incident involving the patient's data (DPDP Rules 2025, rule 7). They come
 * first wherever they appear, and each opens the full notice.
 */
export function NoticeCards({ notices }: { notices: readonly PortalBreachNotice[] }) {
  const styles = useStyles();
  const t = useT();
  const f = useFormat();
  const headingId = useId('privacy-notices');
  if (notices.length === 0) {
    return null;
  }
  return (
    <section className={styles.section} aria-labelledby={headingId}>
      <h2 id={headingId} className={styles.title}>
        {t('myPrivacy.notices.title')}
      </h2>
      {notices.map((notice) => (
        <CardLink
          key={notice.id}
          to="/my-privacy/notices/$noticeId"
          params={{ noticeId: notice.id }}
          className={styles.card}
        >
          <ListRow
            icon={<ShieldErrorRegular />}
            tone="warning"
            title={notice.title}
            subtitle={t('myPrivacy.notices.sent', { date: f.date(notice.notifiedAt), reference: notice.reference })}
          />
        </CardLink>
      ))}
    </section>
  );
}
