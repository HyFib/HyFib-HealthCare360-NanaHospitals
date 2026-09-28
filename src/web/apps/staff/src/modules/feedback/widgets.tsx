import { Badge, Caption1, makeStyles, Spinner, Text, tokens } from '@fluentui/react-components';
import { ChevronRightRegular } from '@fluentui/react-icons';
import { todayIso } from '@hc360/core';
import { ErrorState, Section } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { Link } from '@tanstack/react-router';
import { usePermission } from '@/lib/permissions';
import { feedbackQueries } from './api';
import { FEEDBACK_PERMISSIONS } from './labels';
import { NpsGauge } from './NpsGauge';
import { presetRange } from './rules';

const useStyles = makeStyles({
  facts: { display: 'flex', gap: '8px', flexWrap: 'wrap', justifyContent: 'center', marginTop: '8px' },
  link: {
    display: 'flex',
    alignItems: 'center',
    gap: '6px',
    justifyContent: 'center',
    marginTop: '8px',
    color: tokens.colorBrandForegroundLink,
    textDecoration: 'none',
    ':hover': { textDecoration: 'underline' },
  },
  muted: { color: tokens.colorNeutralForeground3 },
});

/** "Feedback NPS this month": the net promoter score, low scores and the complaint backlog. */
export function FeedbackNpsWidget() {
  const styles = useStyles();
  const canComplaints = usePermission(FEEDBACK_PERMISSIONS.complaintsManage);
  const analytics = useQuery(feedbackQueries.analytics(presetRange('thisMonth', todayIso())));

  if (analytics.error) {
    return <ErrorState error={analytics.error} onRetry={() => void analytics.refetch()} />;
  }

  const a = analytics.data;
  return (
    <Section title="Feedback NPS this month" description="Would patients recommend us? (−100 to +100)">
      {!a ? (
        <Spinner size="tiny" label="Loading…" labelPosition="after" />
      ) : a.responses === 0 ? (
        <Text className={styles.muted}>No feedback yet this month.</Text>
      ) : (
        <>
          <NpsGauge nps={a.nps} size={200} />
          <div className={styles.facts}>
            <Badge appearance="tint" color="informative">
              {a.responses.toLocaleString('en-IN')} responses
            </Badge>
            {a.lowScores > 0 && (
              <Badge appearance="tint" color="warning">
                {a.lowScores} low score{a.lowScores === 1 ? '' : 's'}
              </Badge>
            )}
            {canComplaints && a.complaints.overdue > 0 && (
              <Badge appearance="tint" color="danger">
                {a.complaints.overdue} complaint{a.complaints.overdue === 1 ? '' : 's'} overdue
              </Badge>
            )}
          </div>
        </>
      )}
      <Link to="/feedback" className={styles.link}>
        <Caption1>Open feedback analytics</Caption1>
        <ChevronRightRegular aria-hidden />
      </Link>
    </Section>
  );
}
