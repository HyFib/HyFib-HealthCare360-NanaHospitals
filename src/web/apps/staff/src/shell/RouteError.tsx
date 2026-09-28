import { Button } from '@fluentui/react-components';
import { ArrowLeftRegular } from '@fluentui/react-icons';
import { EmptyState, ErrorState, Page } from '@hc360/ui';
import { Link, useRouter, type ErrorComponentProps } from '@tanstack/react-router';

/** Error boundary for a route: friendly message, retry, and the server reference ID for IT. */
export function RouteError({ error, reset }: ErrorComponentProps) {
  const router = useRouter();
  return (
    <Page maxWidth={720}>
      <ErrorState
        error={error}
        title="This page could not be shown"
        onRetry={() => {
          reset();
          void router.invalidate();
        }}
      />
    </Page>
  );
}

export function NotFound() {
  return (
    <Page maxWidth={720}>
      <EmptyState
        title="Page not found"
        description="The link may be out of date, or this screen is not enabled for your role."
        action={
          <Link to="/">
            <Button appearance="primary" icon={<ArrowLeftRegular />}>
              Back to home
            </Button>
          </Link>
        }
      />
    </Page>
  );
}
