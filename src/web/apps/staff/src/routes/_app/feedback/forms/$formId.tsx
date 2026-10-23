import { Badge } from '@fluentui/react-components';
import { FormRegular } from '@fluentui/react-icons';
import { ErrorState, LoadingState, Page, PageHeader, Section } from '@hc360/ui';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { feedbackQueries, type FeedbackFormDto } from '@/modules/feedback/api';
import { FeedbackFormEditor } from '@/modules/feedback/FeedbackFormEditor';
import { FormQrPanel } from '@/modules/feedback/FormQrPanel';
import { FEEDBACK_PERMISSIONS as F, FORM_VIEWERS, formTypeLabel } from '@/modules/feedback/labels';
import { RequirePermission } from '@/modules/hr/shared/NoAccess';

export const Route = createFileRoute('/_app/feedback/forms/$formId')({
  component: () => (
    <RequirePermission permission={FORM_VIEWERS} what="feedback forms">
      <FormPage />
    </RequirePermission>
  ),
});

function FormPage() {
  const { formId } = Route.useParams();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const canManage = usePermission(F.manage);
  // No silent refetch: the version in hand is what the manager's edits are based on.
  const form = useQuery({ ...feedbackQueries.form(formId), refetchOnWindowFocus: false });
  // The form as this manager's new QR code returned it: that changes its version too (REL-04).
  const [rotated, setRotated] = useState<FeedbackFormDto | null>(null);

  const crumbs = [
    { label: 'Feedback analytics', onClick: () => void navigate({ to: '/feedback' }) },
    { label: 'Forms and QR codes', onClick: () => void navigate({ to: '/feedback/forms' }) },
  ];

  if (!form.data) {
    return (
      <Page>
        <PageHeader title="Feedback form" icon={<FormRegular />} breadcrumbs={crumbs} />
        {form.error ? <ErrorState error={form.error} onRetry={() => void form.refetch()} /> : <LoadingState />}
      </Page>
    );
  }

  const f = form.data;
  return (
    <Page>
      <PageHeader
        title={
          <span style={{ display: 'inline-flex', gap: 8, alignItems: 'center', flexWrap: 'wrap' }}>
            {f.title}
            {!f.isActive && (
              <Badge appearance="tint" color="subtle">
                Inactive
              </Badge>
            )}
          </span>
        }
        subtitle={`${f.code} · ${formTypeLabel[f.type]}`}
        icon={<FormRegular />}
        breadcrumbs={crumbs}
      />
      <FeedbackFormEditor
        key={f.id}
        form={f}
        canManage={canManage}
        onSaved={() => undefined}
        ownChange={rotated}
        aside={
          <Section title="QR code" description="For posters at the exit, waiting areas and the feedback kiosk">
            <FormQrPanel
              form={f}
              canManage={canManage}
              onRotated={(updated) => {
                setRotated(updated);
                queryClient.setQueryData(feedbackQueries.form(formId).queryKey, updated);
              }}
            />
          </Section>
        }
      />
    </Page>
  );
}
