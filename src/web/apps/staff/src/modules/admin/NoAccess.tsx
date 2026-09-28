import { ShieldKeyholeRegular } from '@fluentui/react-icons';
import { EmptyState } from '@hc360/ui';

/** Shown instead of a screen the user's role does not include (the server refuses the calls anyway). */
export function NoAccess({ what }: { what: string }) {
  return (
    <EmptyState
      icon={<ShieldKeyholeRegular />}
      title={`You don't have access to ${what}`}
      description="Ask your administrator to add the permission to your role if you need it for your work."
    />
  );
}
