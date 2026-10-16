import { makeStyles, tokens } from '@fluentui/react-components';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, redirect } from '@tanstack/react-router';
import { useEffect, useMemo, useState } from 'react';
import { CheckingBrowser } from '@/components/CheckingBrowser';
import { PortalPage } from '@/components/PortalPage';
import { ErrorBlock, Notice, PageSpinner } from '@/components/States';
import { useT, type MessageKey } from '@/i18n';
import { forgetLinkToken, keepLinkToken, linkTokenFor, TOKEN_PLACEHOLDER } from '@/lib/linkToken';
import { prepareProof } from '@/lib/proofOfWork';
import type { FeedbackReceipt } from '@/lib/types';
import { publicFeedbackApi, publicFeedbackQueries } from '@/modules/feedback/api';
import { FeedbackForm } from '@/modules/feedback/FeedbackForm';
import { FeedbackThanks } from '@/modules/feedback/FeedbackThanks';
import { linkProblem, type LinkProblem } from '@/modules/feedback/model';
import { PortalFrame } from '@/shell/PortalFrame';

/**
 * The personal feedback link sent by SMS after a visit or a hospital stay: {portal}/portal/feedback/{token}. No
 * sign-in. The token (single use, 14 days) is taken out of the address bar before anything is shown.
 */
export const Route = createFileRoute('/feedback/$token')({
  beforeLoad: ({ params }) => {
    if (params.token !== TOKEN_PLACEHOLDER && keepLinkToken('feedback', params.token)) {
      throw redirect({ to: '/feedback/$token', params: { token: TOKEN_PLACEHOLDER }, replace: true });
    }
  },
  component: FeedbackLinkPage,
});

const PROBLEM_TEXT: Record<LinkProblem, { title: MessageKey; body: MessageKey; intent: 'success' | 'warning' }> = {
  used: { title: 'feedback.link.used.title', body: 'feedback.link.used.body', intent: 'success' },
  expired: { title: 'feedback.link.expired.title', body: 'feedback.link.expired.body', intent: 'warning' },
  invalid: { title: 'feedback.link.invalid.title', body: 'feedback.link.invalid.body', intent: 'warning' },
};

const useStyles = makeStyles({
  hospital: { margin: 0, color: tokens.colorNeutralForeground2, fontSize: tokens.fontSizeBase400 },
});

function FeedbackLinkPage() {
  const styles = useStyles();
  const { token: param } = Route.useParams();
  const t = useT();
  const token = useMemo(() => linkTokenFor('feedback', param), [param]);
  const invitation = useQuery({ ...publicFeedbackQueries.invitation(token ?? ''), enabled: token !== null });
  const [receipt, setReceipt] = useState<FeedbackReceipt | null>(null);
  const [lateProblem, setLateProblem] = useState<LinkProblem | null>(null);
  const problem: LinkProblem | null = token === null ? 'invalid' : (lateProblem ?? linkProblem(invitation.error));
  const [checking, setChecking] = useState(false);
  const ready = Boolean(invitation.data) && problem === null && receipt === null;
  // Solved while the patient fills the form in, so sending does not wait for it (SRS SEC-06).
  useEffect(() => {
    if (ready) {
      prepareProof('feedback');
    }
  }, [ready]);

  let content;
  if (receipt) {
    content = <FeedbackThanks receipt={receipt} />;
  } else if (problem) {
    const text = PROBLEM_TEXT[problem];
    content = (
      <Notice intent={text.intent} title={t(text.title)}>
        {t(text.body)}
      </Notice>
    );
  } else if (invitation.isPending) {
    content = <PageSpinner />;
  } else if (!invitation.data) {
    content = <ErrorBlock error={invitation.error} onRetry={() => void invitation.refetch()} />;
  } else {
    const form = invitation.data;
    content = (
      <>
        <p className={styles.hospital}>{t('feedback.link.for', { hospital: form.hospital })}</p>
        <FeedbackForm
          questions={form}
          intro={form.introText}
          privacyNote={t('feedback.personal')}
          status={<CheckingBrowser active={checking} />}
          onSubmit={async (body) => {
            if (!token) {
              return;
            }
            const result = await publicFeedbackApi.submitInvitation(token, body, setChecking);
            forgetLinkToken('feedback');
            setReceipt(result);
          }}
          onError={(error) => {
            const late = linkProblem(error);
            if (late) {
              forgetLinkToken('feedback');
              setLateProblem(late);
            }
            return late !== null;
          }}
        />
      </>
    );
  }

  return (
    <PortalFrame navigation={false} brandTarget="/sign-in">
      <PortalPage title={invitation.data?.title ?? t('feedback.title')} width="narrow">
        {content}
      </PortalPage>
    </PortalFrame>
  );
}
