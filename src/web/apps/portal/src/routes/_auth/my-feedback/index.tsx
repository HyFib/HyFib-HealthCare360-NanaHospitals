import { makeStyles, tokens } from '@fluentui/react-components';
import { CommentRegular, MegaphoneRegular, PersonFeedbackRegular } from '@fluentui/react-icons';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute } from '@tanstack/react-router';
import { CardLink } from '@/components/links';
import { ListRow, RowCard } from '@/components/ListRow';
import { PortalPage } from '@/components/PortalPage';
import { ErrorBlock, LoadingCards } from '@/components/States';
import { StatusPill } from '@/components/StatusPill';
import { useT } from '@/i18n';
import { useFormat } from '@/i18n/useFormat';
import { feedbackQueries } from '@/modules/feedback/api';
import { COMPLAINT_STATUS } from '@/modules/feedback/model';
import { ReportCard } from '@/modules/reports/ReportParts';
import { useActiveProfile } from '@/shell/profileContext';

export const Route = createFileRoute('/_auth/my-feedback/')({
  component: FeedbackPage,
});

const useStyles = makeStyles({
  list: { display: 'flex', flexDirection: 'column', gap: '10px' },
  none: { margin: 0, color: tokens.colorNeutralForeground2 },
});

function FeedbackPage() {
  const styles = useStyles();
  const t = useT();
  const f = useFormat();
  const { profile } = useActiveProfile();
  const forms = useQuery(feedbackQueries.forms(profile.id));
  const mine = useQuery(feedbackQueries.mine(profile.id));

  return (
    <PortalPage title={t('feedback.title')} subtitle={t('feedback.subtitle')}>
      <ReportCard title={t('feedback.forms')}>
        {forms.isPending ? (
          <LoadingCards count={1} />
        ) : forms.isError ? (
          <ErrorBlock error={forms.error} onRetry={() => void forms.refetch()} />
        ) : forms.data.length === 0 ? (
          <p className={styles.none}>{t('feedback.forms.none')}</p>
        ) : (
          <div className={styles.list}>
            {forms.data.map((form) => (
              <CardLink key={form.id} to="/my-feedback/$formId" params={{ formId: form.id }}>
                <ListRow
                  icon={<PersonFeedbackRegular />}
                  title={form.title}
                  subtitle={t('feedback.formAt', { hospital: form.hospital })}
                />
              </CardLink>
            ))}
          </div>
        )}
      </ReportCard>

      <ReportCard title={t('feedback.history')}>
        {mine.isPending ? (
          <LoadingCards count={2} />
        ) : mine.isError ? (
          <ErrorBlock error={mine.error} onRetry={() => void mine.refetch()} />
        ) : mine.data.length === 0 ? (
          <p className={styles.none}>{t('feedback.history.none')}</p>
        ) : (
          <div className={styles.list}>
            {mine.data.map((item) => {
              const complaint = item.complaintStatus ? COMPLAINT_STATUS[item.complaintStatus] : null;
              return (
                <RowCard key={item.id}>
                  <ListRow
                    icon={<CommentRegular />}
                    tone="neutral"
                    title={item.formTitle}
                    subtitle={f.date(item.submittedAt)}
                    meta={
                      [
                        item.averageRating !== null
                          ? t('feedback.history.rating', { value: f.number(Math.round(item.averageRating * 10) / 10) })
                          : null,
                        item.nps !== null ? t('feedback.history.nps', { value: f.number(item.nps) }) : null,
                        item.complaintNumber ? t('feedback.history.complaint', { number: item.complaintNumber }) : null,
                      ]
                        .filter(Boolean)
                        .join(' · ') || undefined
                    }
                    trailing={complaint ? <StatusPill tone={complaint.tone}>{t(complaint.key)}</StatusPill> : undefined}
                    chevron={false}
                  />
                </RowCard>
              );
            })}
          </div>
        )}
      </ReportCard>

      <CardLink to="/profile/preferences">
        <ListRow
          icon={<MegaphoneRegular />}
          title={t('feedback.preferences')}
          subtitle={t('feedback.preferences.hint')}
        />
      </CardLink>
    </PortalPage>
  );
}
