import { Button } from '@fluentui/react-components';
import { HomeRegular } from '@fluentui/react-icons';
import { EmptyState } from '@hc360/ui';
import { useNavigate, useRouter, type ErrorComponentProps } from '@tanstack/react-router';
import { PortalPage } from '@/components/PortalPage';
import { ErrorBlock } from '@/components/States';
import { useT } from '@/i18n';

/** A page failed to render or load: friendly words, "Try again", and the way home. Nothing is logged (no PHI). */
export function RouteError({ error, reset }: ErrorComponentProps) {
  const router = useRouter();
  const t = useT();
  return (
    <PortalPage title={t('shell.routeError.title')} width="narrow">
      <ErrorBlock
        error={error}
        onRetry={() => {
          reset();
          void router.invalidate();
        }}
      />
    </PortalPage>
  );
}

export function NotFound() {
  const t = useT();
  const navigate = useNavigate();
  return (
    <PortalPage title={t('shell.notFound.title')} width="narrow">
      <EmptyState
        title={t('shell.notFound.title')}
        description={t('shell.notFound.body')}
        action={
          <Button appearance="primary" size="large" icon={<HomeRegular />} onClick={() => void navigate({ to: '/' })}>
            {t('common.goHome')}
          </Button>
        }
      />
    </PortalPage>
  );
}
