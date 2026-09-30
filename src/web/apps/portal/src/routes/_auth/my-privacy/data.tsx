import { makeStyles, tokens } from '@fluentui/react-components';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute } from '@tanstack/react-router';
import { DocumentActions } from '@/components/DocumentActions';
import { InfoList } from '@/components/InfoList';
import { BackLink, TextLink } from '@/components/links';
import { PortalPage } from '@/components/PortalPage';
import { ErrorBlock, LoadingCards, Notice } from '@/components/States';
import { StatusPill } from '@/components/StatusPill';
import { useT } from '@/i18n';
import { useFormat } from '@/i18n/useFormat';
import type { DataSummary, SummaryRecipient } from '@/lib/types';
import { privacyQueries } from '@/modules/privacy/api';
import { ContactCard } from '@/modules/privacy/ContactCard';
import { categoryView, consentView, personRows, splitCategories, type CategoryView } from '@/modules/privacy/model';
import { dataSummaryDocument } from '@/modules/privacy/print';
import { ReportCard } from '@/modules/reports/ReportParts';
import { useActiveProfile } from '@/shell/profileContext';

/** The summary of the patient's personal data (DPDP Act s11): free, and always up to date. */
export const Route = createFileRoute('/_auth/my-privacy/data')({
  component: DataSummaryPage,
});

const useStyles = makeStyles({
  text: {
    margin: 0,
    fontSize: tokens.fontSizeBase400,
    lineHeight: tokens.lineHeightBase400,
    overflowWrap: 'anywhere',
  },
  muted: { margin: 0, color: tokens.colorNeutralForeground2 },
  items: { listStyle: 'none', margin: 0, padding: 0, display: 'flex', flexDirection: 'column', gap: '10px' },
  item: {
    display: 'flex',
    alignItems: 'flex-start',
    justifyContent: 'space-between',
    gap: '12px',
    paddingBottom: '10px',
    borderBottom: `1px solid ${tokens.colorNeutralStroke3}`,
    ':last-child': { borderBottom: 'none', paddingBottom: 0 },
  },
  itemText: { display: 'flex', flexDirection: 'column', gap: '2px', minWidth: 0 },
  itemTitle: { fontWeight: tokens.fontWeightSemibold, fontSize: tokens.fontSizeBase400, overflowWrap: 'anywhere' },
  itemMeta: { color: tokens.colorNeutralForeground2, overflowWrap: 'anywhere' },
  count: { flexShrink: 0, color: tokens.colorNeutralForeground2, textAlign: 'end' },
  facts: {
    padding: '12px',
    borderRadius: tokens.borderRadiusLarge,
    backgroundColor: tokens.colorNeutralBackground2,
  },
  link: { alignSelf: 'flex-start', display: 'inline-flex', alignItems: 'center', minHeight: '44px' },
});

function DataSummaryPage() {
  const t = useT();
  const f = useFormat();
  const { profile } = useActiveProfile();
  const summary = useQuery(privacyQueries.summary(profile.id));
  const data = summary.data;

  return (
    <PortalPage
      title={t('myPrivacy.data.title')}
      subtitle={data ? t('myPrivacy.data.subtitle', { date: f.dateTime(data.generatedAt) }) : undefined}
      back={<BackLink to="/my-privacy">{t('nav.myPrivacy')}</BackLink>}
      actions={
        data ? (
          <DocumentActions build={() => dataSummaryDocument(data, { t, f, printedAt: new Date().toISOString() })} />
        ) : undefined
      }
    >
      {summary.isPending ? (
        <LoadingCards count={4} />
      ) : summary.isError ? (
        <ErrorBlock error={summary.error} onRetry={() => void summary.refetch()} />
      ) : (
        <SummaryView summary={summary.data} />
      )}
    </PortalPage>
  );
}

function SummaryView({ summary }: { summary: DataSummary }) {
  const styles = useStyles();
  const t = useT();
  const f = useFormat();
  const { registration, others } = splitCategories(summary.categories);
  const registrationView = registration ? categoryView(registration, summary.periods, t, f) : null;

  return (
    <>
      <Notice intent="info">
        {t('myPrivacy.data.free')} {t('myPrivacy.data.copy')}{' '}
        <TextLink to="/records/request">{t('records.request')}</TextLink>
      </Notice>

      <ReportCard title={registrationView?.title ?? t('myPrivacy.category.registration.title')}>
        <InfoList items={personRows(summary.person, t, f)} />
        {registrationView && <CategoryFacts view={registrationView} />}
        <TextLink to="/my-privacy/requests/new" search={{ type: 'correction' }} className={styles.link}>
          {t('myPrivacy.data.correct')}
        </TextLink>
      </ReportCard>

      {others.map((category) => (
        <CategoryCard key={category.key} view={categoryView(category, summary.periods, t, f)} />
      ))}

      <ReportCard title={t('myPrivacy.data.consents')}>
        {summary.consents.length === 0 ? (
          <p className={styles.muted}>{t('myPrivacy.data.consents.none')}</p>
        ) : (
          <ul className={styles.items}>
            {summary.consents.map((consent, index) => {
              const view = consentView(consent, t, f);
              return (
                <li key={`${consent.type}-${consent.capturedAt}-${index}`} className={styles.item}>
                  <span className={styles.itemText}>
                    <span className={styles.itemTitle}>{view.label}</span>
                    <span className={styles.itemMeta}>{view.detail}</span>
                  </span>
                  <StatusPill tone={view.tone}>{t(view.state)}</StatusPill>
                </li>
              );
            })}
          </ul>
        )}
      </ReportCard>

      <ReportCard title={t('myPrivacy.data.shared')}>
        <p className={styles.muted}>{t('myPrivacy.data.shared.lead')}</p>
        <Recipients
          list={summary.sharedWith}
          none={t('myPrivacy.data.shared.none')}
          data={(recipient) => t('myPrivacy.data.recipient.data', { data: recipient.data })}
        />
      </ReportCard>

      <ReportCard title={t('myPrivacy.data.processors')}>
        <p className={styles.muted}>{t('myPrivacy.data.processors.lead')}</p>
        <Recipients
          list={summary.processors}
          none={t('myPrivacy.data.processors.none')}
          data={(processor) => t('myPrivacy.data.processor.data', { data: processor.data })}
        />
      </ReportCard>

      <ReportCard title={t('myPrivacy.hub.nominee')}>
        <p className={styles.text}>
          {summary.nominee
            ? t('myPrivacy.hub.nominee.named', {
                name: summary.nominee.name,
                relationship: summary.nominee.relationship,
              })
            : t('myPrivacy.hub.nominee.none')}
        </p>
        <TextLink to="/my-privacy/nominee" className={styles.link}>
          {t('myPrivacy.data.nominee.manage')}
        </TextLink>
      </ReportCard>

      <ContactCard contact={summary.contact} />
    </>
  );
}

function CategoryCard({ view }: { view: CategoryView }) {
  const styles = useStyles();
  return (
    <ReportCard title={view.title}>
      <p className={styles.muted}>{view.description}</p>
      {view.items.length > 0 && (
        <ul className={styles.items}>
          {view.items.map((item, index) => (
            <li key={`${item.label}-${index}`} className={styles.item}>
              <span className={styles.itemText}>
                <span className={styles.itemTitle}>{item.label}</span>
                {item.dates && <span className={styles.itemMeta}>{item.dates}</span>}
              </span>
              <span className={styles.count}>{item.count}</span>
            </li>
          ))}
        </ul>
      )}
      <CategoryFacts view={view} />
    </ReportCard>
  );
}

/** Why a group is kept, the legal reason, and for how long. */
function CategoryFacts({ view }: { view: CategoryView }) {
  const styles = useStyles();
  const t = useT();
  return (
    <div className={styles.facts}>
      <InfoList
        singleColumn
        items={[
          { label: t('myPrivacy.data.purpose'), value: view.purpose, hideEmpty: true },
          { label: t('myPrivacy.data.basis'), value: view.basis, hideEmpty: true },
          { label: t('myPrivacy.data.retention'), value: view.retention, hideEmpty: true },
        ]}
      />
    </div>
  );
}

function Recipients({
  list,
  none,
  data,
}: {
  list: readonly SummaryRecipient[];
  none: string;
  data: (recipient: SummaryRecipient) => string;
}) {
  const styles = useStyles();
  if (list.length === 0) {
    return <p className={styles.text}>{none}</p>;
  }
  return (
    <ul className={styles.items}>
      {list.map((recipient, index) => (
        <li key={`${recipient.name}-${recipient.role}-${index}`} className={styles.item}>
          <span className={styles.itemText}>
            <span className={styles.itemTitle}>{recipient.name}</span>
            <span className={styles.itemMeta}>{recipient.role}</span>
            <span className={styles.itemMeta}>{data(recipient)}</span>
          </span>
        </li>
      ))}
    </ul>
  );
}
