import { FormNewRegular } from '@fluentui/react-icons';
import { Page, PageHeader } from '@hc360/ui';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { FeedbackFormEditor } from '@/modules/feedback/FeedbackFormEditor';
import { FEEDBACK_PERMISSIONS as F } from '@/modules/feedback/labels';
import { RequirePermission } from '@/modules/hr/shared/NoAccess';

export const Route = createFileRoute('/_app/feedback/forms/new')({
  component: () => (
    <RequirePermission permission={F.manage} what="creating feedback forms">
      <NewFormPage />
    </RequirePermission>
  ),
});

function NewFormPage() {
  const navigate = useNavigate();
  return (
    <Page>
      <PageHeader
        title="New feedback form"
        subtitle="Choose what patients rate and whether to ask for a recommendation score and a comment. The preview shows what they see."
        icon={<FormNewRegular />}
        breadcrumbs={[
          { label: 'Feedback analytics', onClick: () => void navigate({ to: '/feedback' }) },
          { label: 'Forms and QR codes', onClick: () => void navigate({ to: '/feedback/forms' }) },
        ]}
      />
      <FeedbackFormEditor form={null} canManage onSaved={(formId) => void navigate({ to: '/feedback/forms/$formId', params: { formId }, replace: true })} />
    </Page>
  );
}
