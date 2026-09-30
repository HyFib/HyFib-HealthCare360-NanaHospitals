import { Caption1, Text, tokens } from '@fluentui/react-components';
import { CalendarAddRegular } from '@fluentui/react-icons';
import { ErrorState, LoadingState, Section, useNotify } from '@hc360/ui';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { hrApi, hrKeys, hrQueries } from '@/modules/hr/api';
import { applicableLeaveTypes, daysText } from '@/modules/hr/leave';
import { LeaveApplyForm } from '@/modules/hr/LeaveApplyForm';
import { SelfServicePage } from '@/modules/hr/SelfServicePage';
import { currentMonth } from '@/modules/hr/shared/dates';
import { MainAside } from '@/modules/hr/shared/layout';

export const Route = createFileRoute('/_app/hr/me/apply-leave')({
  component: ApplyLeavePage,
});

/** The "Apply for leave" quick action: a focused form with the balance alongside. */
function ApplyLeavePage() {
  return (
    <SelfServicePage
      title="Apply for leave"
      subtitle="Your reporting manager (or HR) is notified and decides the request."
      icon={<CalendarAddRegular />}
      maxWidth={1200}
    >
      {() => <ApplyLeave />}
    </SelfServicePage>
  );
}

function ApplyLeave() {
  const navigate = useNavigate();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const { year } = currentMonth();
  const types = useQuery(hrQueries.leaveTypes());
  const balances = useQuery(hrQueries.myBalances(year));

  if (types.isLoading) {
    return <LoadingState />;
  }
  if (types.error || !types.data) {
    return <ErrorState error={types.error} onRetry={() => void types.refetch()} />;
  }

  return (
    <MainAside>
      <Section title="Leave request">
        <LeaveApplyForm
          leaveTypes={applicableLeaveTypes(types.data)}
          balances={balances.data}
          submitLabel="Send for approval"
          onCancel={() => void navigate({ to: '/hr/me/leave' })}
          submit={async (request) => {
            await hrApi.myApplyLeave(request);
            notify.success('Leave request sent', 'You will be notified when it is decided.');
            void queryClient.invalidateQueries({ queryKey: hrKeys.me });
            void navigate({ to: '/hr/me/leave' });
          }}
        />
      </Section>
      <Section title={`My balance ${year}`}>
        {balances.isLoading && <LoadingState />}
        {balances.error && <ErrorState error={balances.error} onRetry={() => void balances.refetch()} />}
        {balances.data?.length === 0 && (
          <Caption1>Not credited yet for {year}. HR credits leave at the start of the year.</Caption1>
        )}
        <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
          {balances.data?.map((b) => (
            <div
              key={b.leaveTypeId}
              style={{
                display: 'flex',
                justifyContent: 'space-between',
                gap: 8,
                paddingBottom: 6,
                borderBottom: `1px solid ${tokens.colorNeutralStroke3}`,
              }}
            >
              <span>
                <Text weight="semibold">{b.leaveTypeName}</Text>
                <Caption1 block style={{ color: tokens.colorNeutralForeground3 }}>
                  {daysText(b.availed)} taken{b.pending > 0 ? ` · ${daysText(b.pending)} waiting` : ''}
                </Caption1>
              </span>
              <Text weight="semibold">{daysText(b.available)}</Text>
            </div>
          ))}
        </div>
      </Section>
    </MainAside>
  );
}
