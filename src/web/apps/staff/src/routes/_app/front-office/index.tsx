import { Button, makeStyles } from '@fluentui/react-components';
import { PeopleQueueRegular, PersonAddRegular } from '@fluentui/react-icons';
import { Page, PageHeader } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { Can } from '@/lib/permissions';
import { frontOfficeQueries } from '@/modules/front-office/api';
import { DepartmentQueues, FrontOfficeKpis, VisitsByType } from '@/modules/front-office/widgets';

export const Route = createFileRoute('/_app/front-office/')({
  component: FrontOfficeOverview,
});

const useStyles = makeStyles({
  grid: {
    display: 'grid',
    gap: '16px',
    gridTemplateColumns: 'minmax(0, 2fr) minmax(0, 1fr)',
    '@media (max-width: 1100px)': { gridTemplateColumns: 'minmax(0, 1fr)' },
  },
});

function FrontOfficeOverview() {
  const styles = useStyles();
  const navigate = useNavigate();
  const dashboard = useQuery(frontOfficeQueries.dashboard());

  return (
    <Page>
      <PageHeader
        title="Front office"
        subtitle="Registrations, check-ins and queues for today. Updates as it happens."
        actions={
          <>
            <Can permission="patients.register">
              <Button
                appearance="primary"
                icon={<PersonAddRegular />}
                onClick={() => void navigate({ to: '/patients/register' })}
              >
                Register patient
              </Button>
            </Can>
            <Can permission="queue.manage">
              <Button icon={<PeopleQueueRegular />} onClick={() => void navigate({ to: '/front-office/queue' })}>
                Open queue
              </Button>
            </Can>
          </>
        }
      />
      <FrontOfficeKpis />
      {/* When the dashboard cannot be read the KPIs say so, with a retry; empty queues would read as a quiet day. */}
      {!dashboard.error && (
        <div className={styles.grid}>
          <DepartmentQueues dashboard={dashboard.data} />
          <VisitsByType dashboard={dashboard.data} />
        </div>
      )}
    </Page>
  );
}
