import { Button, makeStyles, Spinner, tokens } from '@fluentui/react-components';
import { CheckmarkCircleRegular, MailProhibitedRegular } from '@fluentui/react-icons';
import { isApiError } from '@hc360/core';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, redirect } from '@tanstack/react-router';
import { useMemo, useState } from 'react';
import { TextLink } from '@/components/links';
import { PortalPage } from '@/components/PortalPage';
import { ErrorBlock, Notice, PageSpinner } from '@/components/States';
import { useT } from '@/i18n';
import { describeError } from '@/lib/errors';
import { keepLinkToken, linkTokenFor, TOKEN_PLACEHOLDER } from '@/lib/linkToken';
import { publicFeedbackApi, publicFeedbackQueries } from '@/modules/feedback/api';
import { PortalFrame } from '@/shell/PortalFrame';

/**
 * The opt-out link in campaign messages: {portal}/portal/unsubscribe/{token}. Opening the link changes nothing (SMS
 * apps and link scanners open links by themselves); the patient confirms with one tap. The token is taken out of the
 * address bar first.
 */
export const Route = createFileRoute('/unsubscribe/$token')({
  beforeLoad: ({ params }) => {
    if (params.token !== TOKEN_PLACEHOLDER && keepLinkToken('unsubscribe', params.token)) {
      throw redirect({ to: '/unsubscribe/$token', params: { token: TOKEN_PLACEHOLDER }, replace: true });
    }
  },
  component: UnsubscribePage,
});

const useStyles = makeStyles({
  card: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'flex-start',
    gap: '16px',
    padding: '20px 16px',
    borderRadius: tokens.borderRadiusXLarge,
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  icon: { fontSize: '48px', color: tokens.colorBrandForeground1 },
  doneIcon: { fontSize: '48px', color: tokens.colorStatusSuccessForeground1 },
  title: {
    margin: 0,
    fontSize: tokens.fontSizeBase500,
    lineHeight: tokens.lineHeightBase500,
    fontWeight: tokens.fontWeightSemibold,
  },
  text: {
    margin: 0,
    fontSize: tokens.fontSizeBase400,
    lineHeight: tokens.lineHeightBase400,
    color: tokens.colorNeutralForeground2,
  },
});

function UnsubscribePage() {
  const styles = useStyles();
  const { token: param } = Route.useParams();
  const t = useT();
  const token = useMemo(() => linkTokenFor('unsubscribe', param), [param]);
  const status = useQuery({ ...publicFeedbackQueries.optOut(token ?? ''), enabled: token !== null });
  const [busy, setBusy] = useState(false);
  const [done, setDone] = useState(false);
  const [invalid, setInvalid] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const confirm = async () => {
    if (!token) {
      return;
    }
    setBusy(true);
    setError(null);
    try {
      await publicFeedbackApi.optOut(token);
      setDone(true);
    } catch (err) {
      if (isApiError(err) && err.status === 404) {
        setInvalid(true);
      } else {
        setError(describeError(err, t, 'errors.saveTitle').message);
      }
    } finally {
      setBusy(false);
    }
  };

  const linkBroken = token === null || invalid || (isApiError(status.error) && status.error.status === 404);
  let content;
  if (linkBroken) {
    content = (
      <Notice intent="warning" title={t('unsubscribe.invalid.title')}>
        {t('unsubscribe.invalid.body')}
      </Notice>
    );
  } else if (done || status.data?.alreadyOptedOut) {
    content = (
      <div className={styles.card} role="status">
        <CheckmarkCircleRegular className={styles.doneIcon} aria-hidden />
        <h2 className={styles.title}>{t('unsubscribe.done.title')}</h2>
        <p className={styles.text}>{done ? t('unsubscribe.done.body') : t('unsubscribe.already')}</p>
      </div>
    );
  } else if (status.isPending) {
    content = <PageSpinner />;
  } else if (!status.data) {
    content = <ErrorBlock error={status.error} onRetry={() => void status.refetch()} />;
  } else {
    content = (
      <div className={styles.card}>
        <MailProhibitedRegular className={styles.icon} aria-hidden />
        <p className={styles.text}>{t('unsubscribe.body')}</p>
        {error && <Notice intent="error">{error}</Notice>}
        <Button
          appearance="primary"
          size="large"
          onClick={() => void confirm()}
          disabled={busy}
          icon={busy ? <Spinner size="tiny" aria-hidden /> : undefined}
        >
          {busy ? t('common.sending') : t('unsubscribe.confirm')}
        </Button>
      </div>
    );
  }

  return (
    <PortalFrame navigation={false} brandTarget="/sign-in">
      <PortalPage title={t('unsubscribe.title')} width="narrow">
        {content}
        <TextLink to="/profile/preferences">{t('unsubscribe.manage')}</TextLink>
      </PortalPage>
    </PortalFrame>
  );
}
