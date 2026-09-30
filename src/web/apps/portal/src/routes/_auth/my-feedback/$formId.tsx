import { useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute } from '@tanstack/react-router';
import { useState } from 'react';
import { BackLink, ButtonLink } from '@/components/links';
import { PortalPage } from '@/components/PortalPage';
import { ErrorBlock, Notice, PageSpinner } from '@/components/States';
import { useT } from '@/i18n';
import type { FeedbackReceipt } from '@/lib/types';
import { feedbackApi, feedbackQueries } from '@/modules/feedback/api';
import { FeedbackForm } from '@/modules/feedback/FeedbackForm';
import { FeedbackThanks } from '@/modules/feedback/FeedbackThanks';
import { useActiveProfile } from '@/shell/profileContext';

export const Route = createFileRoute('/_auth/my-feedback/$formId')({
  component: FeedbackFormPage,
});

function FeedbackFormPage() {
  const { formId } = Route.useParams();
  const t = useT();
  const queryClient = useQueryClient();
  const { profile } = useActiveProfile();
  const forms = useQuery(feedbackQueries.forms(profile.id));
  const [receipt, setReceipt] = useState<FeedbackReceipt | null>(null);
  const back = <BackLink to="/my-feedback">{t('feedback.title')}</BackLink>;
  const form = forms.data?.find((candidate) => candidate.id === formId);

  if (forms.isPending) {
    return <PageSpinner />;
  }
  if (!forms.data) {
    return (
      <PortalPage title={t('feedback.title')} back={back} width="narrow">
        <ErrorBlock error={forms.error} onRetry={() => void forms.refetch()} />
      </PortalPage>
    );
  }
  if (!form) {
    return (
      <PortalPage title={t('feedback.title')} back={back} width="narrow">
        <Notice intent="warning">{t('errors.notFound')}</Notice>
      </PortalPage>
    );
  }

  return (
    <PortalPage
      title={form.title}
      subtitle={t('feedback.formAt', { hospital: form.hospital })}
      back={back}
      width="narrow"
    >
      {receipt ? (
        <FeedbackThanks
          receipt={receipt}
          action={
            <ButtonLink to="/my-feedback" appearance="primary">
              {t('feedback.done.more')}
            </ButtonLink>
          }
        />
      ) : (
        <FeedbackForm
          questions={form}
          intro={form.introText}
          privacyNote={t('feedback.portalNote')}
          defaultPhone={profile.phone}
          onSubmit={async (body) => {
            const result = await feedbackApi.submit({ patientId: profile.id, formId: form.id, visitId: null, ...body });
            setReceipt(result);
            void queryClient.invalidateQueries({ queryKey: feedbackQueries.mine(profile.id).queryKey });
          }}
        />
      )}
    </PortalPage>
  );
}
