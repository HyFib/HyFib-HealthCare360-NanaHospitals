import { ShieldErrorRegular } from '@fluentui/react-icons';
import { Page, PageHeader, useNotify } from '@hc360/ui';
import { useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { RequirePermission } from '@/modules/hr/shared/NoAccess';
import { privacyKeys } from '@/modules/privacy/api';
import { BreachForm } from '@/modules/privacy/BreachForm';
import { PRIVACY_PERMISSIONS as P } from '@/modules/privacy/labels';

export const Route = createFileRoute('/_app/privacy/breaches/new')({
  component: () => (
    <RequirePermission permission={P.breachesManage} what="the breach register">
      <NewBreachPage />
    </RequirePermission>
  ),
});

function NewBreachPage() {
  const navigate = useNavigate();
  const notify = useNotify();
  const queryClient = useQueryClient();

  return (
    <Page maxWidth={1100}>
      <PageHeader
        title="Record a breach"
        subtitle="Record what is known now; you can add to it later. The detailed report to the Data Protection Board is due within 72 hours of the hospital becoming aware."
        icon={<ShieldErrorRegular />}
        breadcrumbs={[{ label: 'Data breaches', onClick: () => void navigate({ to: '/privacy/breaches' }) }]}
      />
      <BreachForm
        mode={{ kind: 'new' }}
        onCancel={() => void navigate({ to: '/privacy/breaches' })}
        onSaved={({ id: breachId, breachNumber }) => {
          notify.success(
            `Breach ${breachNumber} recorded`,
            'Everyone who manages breaches has been alerted. Tell the Data Protection Board without delay.',
          );
          void queryClient.invalidateQueries({ queryKey: privacyKeys.breaches });
          void queryClient.invalidateQueries({ queryKey: privacyKeys.overview });
          void navigate({ to: '/privacy/breaches/$breachId', params: { breachId } });
        }}
      />
    </Page>
  );
}
