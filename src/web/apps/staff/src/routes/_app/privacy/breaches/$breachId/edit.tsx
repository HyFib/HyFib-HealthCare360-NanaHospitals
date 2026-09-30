import { Button } from '@fluentui/react-components';
import { ShieldErrorRegular } from '@fluentui/react-icons';
import { EmptyState, ErrorState, LoadingState, Page, PageHeader, Section, useNotify } from '@hc360/ui';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { RequirePermission } from '@/modules/hr/shared/NoAccess';
import { privacyKeys, privacyQueries } from '@/modules/privacy/api';
import { BreachForm } from '@/modules/privacy/BreachForm';
import { PRIVACY_PERMISSIONS as P } from '@/modules/privacy/labels';

export const Route = createFileRoute('/_app/privacy/breaches/$breachId/edit')({
  component: () => (
    <RequirePermission permission={P.breachesManage} what="the breach register">
      <EditBreachPage />
    </RequirePermission>
  ),
});

function EditBreachPage() {
  const { breachId } = Route.useParams();
  const navigate = useNavigate();
  const notify = useNotify();
  const queryClient = useQueryClient();
  // Loaded once: a refresh while typing would replace the form (it is keyed by the breach's version).
  const breach = useQuery({ ...privacyQueries.breach(breachId), refetchInterval: false, refetchOnWindowFocus: false });
  const back = () => void navigate({ to: '/privacy/breaches/$breachId', params: { breachId } });
  const crumbs = [
    { label: 'Data breaches', onClick: () => void navigate({ to: '/privacy/breaches' }) },
    ...(breach.data ? [{ label: breach.data.breachNumber, onClick: back }] : []),
  ];

  if (!breach.data) {
    return (
      <Page maxWidth={1100}>
        <PageHeader title="Change the breach record" icon={<ShieldErrorRegular />} breadcrumbs={crumbs} />
        {breach.error ? <ErrorState error={breach.error} onRetry={() => void breach.refetch()} /> : <LoadingState />}
      </Page>
    );
  }

  const b = breach.data;
  return (
    <Page maxWidth={1100}>
      <PageHeader
        title="Change the breach record"
        subtitle={`${b.breachNumber} · ${b.title}`}
        icon={<ShieldErrorRegular />}
        breadcrumbs={crumbs}
      />
      {b.status === 'Closed' ? (
        <Section>
          <EmptyState
            title="A closed breach cannot be changed"
            description="Its record is kept as it was when it was closed."
            action={<Button onClick={back}>Back to the breach</Button>}
          />
        </Section>
      ) : (
        <BreachForm
          key={b.version}
          mode={{ kind: 'edit', breach: b }}
          onCancel={back}
          onConflict={() => void breach.refetch()}
          onSaved={() => {
            notify.success(`Breach ${b.breachNumber} saved`);
            void queryClient.invalidateQueries({ queryKey: privacyKeys.breaches });
            back();
          }}
        />
      )}
    </Page>
  );
}
