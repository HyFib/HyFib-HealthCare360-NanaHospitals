import { Button, makeStyles, tokens } from '@fluentui/react-components';
import { ArrowResetRegular } from '@fluentui/react-icons';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute } from '@tanstack/react-router';
import { useEffect, useState } from 'react';
import { PortalPage } from '@/components/PortalPage';
import { ErrorBlock, Notice, PageSpinner } from '@/components/States';
import { useT } from '@/i18n';
import { useNow } from '@/lib/hooks';
import { isWellFormedLinkToken } from '@/lib/linkToken';
import type { FeedbackReceipt } from '@/lib/types';
import { publicFeedbackApi, publicFeedbackQueries } from '@/modules/feedback/api';
import { FeedbackForm } from '@/modules/feedback/FeedbackForm';
import { FeedbackThanks } from '@/modules/feedback/FeedbackThanks';
import { linkProblem } from '@/modules/feedback/model';
import { PortalFrame } from '@/shell/PortalFrame';

/**
 * The anonymous feedback form behind a QR poster ({portal}/portal/feedback/form/{token}) or a kiosk tablet (the same
 * address with ?kiosk=true). The token names the form only, so it stays in the address (kiosks reload and bookmark it).
 * A kiosk starts again by itself shortly after each "thank you", ready for the next person.
 */
export const Route = createFileRoute('/feedback/form/$token')({
  validateSearch: (search: Record<string, unknown>): { kiosk?: true } => ({
    kiosk: search.kiosk === true || search.kiosk === 'true' || search.kiosk === '1' || search.kiosk === 1 ? true : undefined,
  }),
  component: FeedbackPosterPage,
});

const KIOSK_RESET_MS = 20_000;

const useStyles = makeStyles({
  hospital: { margin: 0, color: tokens.colorNeutralForeground2, fontSize: tokens.fontSizeBase400 },
  countdown: { margin: 0, color: tokens.colorNeutralForeground3 },
});

function FeedbackPosterPage() {
  const styles = useStyles();
  const { token } = Route.useParams();
  const { kiosk } = Route.useSearch();
  const t = useT();
  const valid = isWellFormedLinkToken(token);
  const form = useQuery({ ...publicFeedbackQueries.form(token), enabled: valid });
  const [receipt, setReceipt] = useState<FeedbackReceipt | null>(null);
  const [round, setRound] = useState(0);
  const [resetAt, setResetAt] = useState<number | null>(null);
  const now = useNow(1000, resetAt !== null);

  const startAgain = () => {
    setReceipt(null);
    setResetAt(null);
    setRound((value) => value + 1);
    window.scrollTo({ top: 0 });
  };

  useEffect(() => {
    if (resetAt === null) {
      return;
    }
    const timer = setTimeout(() => {
      setReceipt(null);
      setResetAt(null);
      setRound((value) => value + 1);
      window.scrollTo({ top: 0 });
    }, Math.max(0, resetAt - Date.now()));
    return () => clearTimeout(timer);
  }, [resetAt]);

  let content;
  if (!valid || linkProblem(form.error) === 'invalid') {
    content = (
      <Notice intent="warning" title={t('feedback.link.invalid.title')}>
        {t('feedback.link.invalid.body')}
      </Notice>
    );
  } else if (form.isPending) {
    content = <PageSpinner />;
  } else if (!form.data) {
    content = <ErrorBlock error={form.error} onRetry={() => void form.refetch()} />;
  } else if (receipt) {
    content = (
      <FeedbackThanks
        receipt={receipt}
        action={
          kiosk ? (
            <>
              <Button appearance="primary" size="large" icon={<ArrowResetRegular />} onClick={startAgain}>
                {t('feedback.kiosk.next')}
              </Button>
              {resetAt !== null && (
                <p className={styles.countdown} aria-live="off">
                  {t('feedback.kiosk.reset', { seconds: Math.max(0, Math.ceil((resetAt - now) / 1000)) })}
                </p>
              )}
            </>
          ) : undefined
        }
      />
    );
  } else {
    const questions = form.data;
    content = (
      <>
        <p className={styles.hospital}>{t('feedback.link.for', { hospital: questions.hospital })}</p>
        <FeedbackForm
          key={round}
          questions={questions}
          intro={questions.introText}
          privacyNote={t('feedback.anonymous')}
          onSubmit={async (body) => {
            const result = await publicFeedbackApi.submitForm(token, body, kiosk === true);
            setReceipt(result);
            if (kiosk) {
              setResetAt(Date.now() + KIOSK_RESET_MS);
            }
          }}
        />
      </>
    );
  }

  return (
    <PortalFrame navigation={false} brandTarget="/sign-in">
      <PortalPage title={form.data?.title ?? t('feedback.title')} width="narrow">
        {content}
      </PortalPage>
    </PortalFrame>
  );
}
