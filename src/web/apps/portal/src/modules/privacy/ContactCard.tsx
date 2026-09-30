import { makeStyles, tokens } from '@fluentui/react-components';
import { CallRegular, LocationRegular, MailRegular, PersonSupportRegular } from '@fluentui/react-icons';
import type { ReactNode } from 'react';
import { ToneIcon } from '@/components/ListRow';
import { useT } from '@/i18n';
import { useFormat } from '@/i18n/useFormat';
import type { PrivacyContact } from '@/lib/types';
import { ReportCard } from '@/modules/reports/ReportParts';
import { contactTitle, formatPhone, mailtoHref, telHref } from './model';

const useStyles = makeStyles({
  who: { display: 'flex', alignItems: 'center', gap: '12px' },
  name: {
    margin: 0,
    fontSize: tokens.fontSizeBase400,
    lineHeight: tokens.lineHeightBase400,
    fontWeight: tokens.fontWeightSemibold,
    overflowWrap: 'anywhere',
  },
  role: { margin: 0, color: tokens.colorNeutralForeground2 },
  list: { listStyle: 'none', margin: 0, padding: 0, display: 'flex', flexDirection: 'column', gap: '8px' },
  row: { display: 'flex', alignItems: 'flex-start', gap: '12px' },
  icon: { display: 'flex', fontSize: '20px', color: tokens.colorNeutralForeground3, flexShrink: 0, marginTop: '1px' },
  rowText: { display: 'flex', flexDirection: 'column', alignItems: 'flex-start', minWidth: 0 },
  label: {
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase200,
    lineHeight: tokens.lineHeightBase200,
  },
  value: {
    padding: '4px 0',
    fontSize: tokens.fontSizeBase400,
    lineHeight: tokens.lineHeightBase400,
    overflowWrap: 'anywhere',
  },
  link: {
    display: 'inline-flex',
    alignItems: 'center',
    minHeight: '44px',
    fontSize: tokens.fontSizeBase400,
    fontWeight: tokens.fontWeightSemibold,
    color: tokens.colorBrandForegroundLink,
    textDecorationLine: 'underline',
    textUnderlineOffset: '3px',
    overflowWrap: 'anywhere',
    ':hover': { color: tokens.colorBrandForegroundLinkHover },
    ':focus-visible': {
      outline: `3px solid ${tokens.colorStrokeFocus2}`,
      outlineOffset: '2px',
      borderRadius: tokens.borderRadiusSmall,
    },
  },
  note: { margin: 0, color: tokens.colorNeutralForeground2, fontSize: tokens.fontSizeBase300 },
});

/**
 * Whom to contact about personal data: the grievance officer (or Data Protection Officer) the hospital publishes,
 * with e-mail and call links, the reply period and, where it helps, the right to go to the Data Protection Board.
 */
export function ContactCard({
  contact,
  responseDays,
  showBoard = true,
  title,
}: {
  contact: PrivacyContact;
  responseDays?: number | null;
  showBoard?: boolean;
  title?: string;
}) {
  const styles = useStyles();
  const t = useT();
  const f = useFormat();
  const role = contactTitle(contact.title, t);
  const name = contact.name?.trim();
  const email = contact.email?.trim();
  const phone = formatPhone(contact.phone);
  const address = contact.address?.trim();
  const emailHref = mailtoHref(email);
  const phoneHref = telHref(contact.phone);

  const row = (icon: ReactNode, label: string, value: ReactNode) => (
    <li className={styles.row}>
      <span className={styles.icon} aria-hidden>
        {icon}
      </span>
      <span className={styles.rowText}>
        <span className={styles.label}>{label}</span>
        {value}
      </span>
    </li>
  );

  return (
    <ReportCard title={title ?? t('myPrivacy.contact.title')}>
      <div className={styles.who}>
        <ToneIcon tone="brand">
          <PersonSupportRegular />
        </ToneIcon>
        <div>
          <p className={styles.name}>{name || role}</p>
          {name && <p className={styles.role}>{role}</p>}
        </div>
      </div>
      {(email || phone || address) && (
        <ul className={styles.list}>
          {email &&
            row(
              <MailRegular />,
              t('myPrivacy.contact.email'),
              emailHref ? (
                <a href={emailHref} className={styles.link}>
                  {email}
                </a>
              ) : (
                <span className={styles.value}>{email}</span>
              ),
            )}
          {phone &&
            row(
              <CallRegular />,
              t('myPrivacy.contact.phone'),
              phoneHref ? (
                <a href={phoneHref} className={styles.link}>
                  {phone}
                </a>
              ) : (
                <span className={styles.value}>{phone}</span>
              ),
            )}
          {address &&
            row(<LocationRegular />, t('myPrivacy.contact.address'), <span className={styles.value}>{address}</span>)}
        </ul>
      )}
      {responseDays ? (
        <p className={styles.note}>{t('myPrivacy.contact.reply', { days: f.number(responseDays) })}</p>
      ) : null}
      {showBoard && <p className={styles.note}>{t('myPrivacy.contact.board')}</p>}
    </ReportCard>
  );
}
