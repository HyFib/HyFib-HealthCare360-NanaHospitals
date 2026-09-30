import { Button } from '@fluentui/react-components';
import { LockClosedRegular } from '@fluentui/react-icons';
import { EmptyState, Page, Section } from '@hc360/ui';
import { useNavigate } from '@tanstack/react-router';
import type { ReactNode } from 'react';
import { usePermission } from '@/lib/permissions';

/** Friendly "no access" state for a screen opened from a bookmark or an old link. */
export function NoAccess({ what, description }: { what: string; description?: string }) {
  const navigate = useNavigate();
  return (
    <Page maxWidth={760}>
      <Section>
        <EmptyState
          icon={<LockClosedRegular />}
          title={`You do not have access to ${what}`}
          description={
            description ?? 'Ask your hospital administrator to give your role access if you need it for your work.'
          }
          action={<Button onClick={() => void navigate({ to: '/' })}>Go to the home page</Button>}
        />
      </Section>
    </Page>
  );
}

/** Renders the screen only for users holding the permission (any of them, for a list); the server checks again. */
export function RequirePermission({
  permission,
  what,
  children,
}: {
  permission: string | readonly string[];
  what: string;
  children: ReactNode;
}) {
  return usePermission(permission) ? children : <NoAccess what={what} />;
}
