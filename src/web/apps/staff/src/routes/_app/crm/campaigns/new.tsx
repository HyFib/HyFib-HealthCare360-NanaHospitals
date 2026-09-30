import { MegaphoneRegular } from '@fluentui/react-icons';
import { Page, PageHeader } from '@hc360/ui';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { CampaignEditor } from '@/modules/crm/CampaignEditor';
import { CRM_PERMISSIONS as C } from '@/modules/crm/labels';
import { RequirePermission } from '@/modules/hr/shared/NoAccess';

export const Route = createFileRoute('/_app/crm/campaigns/new')({
  component: () => (
    <RequirePermission permission={C.campaignsManage} what="drafting campaigns">
      <NewCampaignPage />
    </RequirePermission>
  ),
});

function NewCampaignPage() {
  const navigate = useNavigate();
  return (
    <Page>
      <PageHeader
        title="New campaign"
        subtitle="Write the message and choose who receives it. Only patients who agreed to marketing messages are ever counted or sent to."
        icon={<MegaphoneRegular />}
        breadcrumbs={[
          { label: 'Patient relations', onClick: () => void navigate({ to: '/crm' }) },
          { label: 'Campaigns', onClick: () => void navigate({ to: '/crm/campaigns' }) },
        ]}
      />
      <CampaignEditor
        campaign={null}
        onSaved={(campaignId) =>
          void navigate({ to: '/crm/campaigns/$campaignId', params: { campaignId }, replace: true })
        }
      />
    </Page>
  );
}
