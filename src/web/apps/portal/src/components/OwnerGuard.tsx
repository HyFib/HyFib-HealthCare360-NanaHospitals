import { Button, Spinner } from '@fluentui/react-components';
import { PersonSwapRegular } from '@fluentui/react-icons';
import { useQueryClient } from '@tanstack/react-query';
import { useState, type ReactNode } from 'react';
import { useT } from '@/i18n';
import { firstName } from '@/i18n/format';
import { otherOwner } from '@/lib/profiles';
import { switchProfile } from '@/lib/session';
import { useActiveProfile } from '@/shell/profileContext';
import { Notice } from './States';

/**
 * Shows a record only under the family member it belongs to. A link to a relative's record (from an SMS, or opened
 * while viewing someone else) asks to switch first, so one person's details never appear under another's name.
 */
export function OwnerGuard({ ownerId, children }: { ownerId: string | null | undefined; children: ReactNode }) {
  const t = useT();
  const { profile, profiles } = useActiveProfile();
  const queryClient = useQueryClient();
  const [switching, setSwitching] = useState(false);

  if (!ownerId || ownerId === profile.id) {
    return <>{children}</>;
  }
  const owner = otherOwner(profiles, profile.id, ownerId);
  if (!owner) {
    return <Notice intent="warning">{t('shell.otherOwner.unknown')}</Notice>;
  }
  return (
    <Notice
      intent="info"
      title={t('shell.otherOwner.title', { name: owner.fullName })}
      action={
        <Button
          appearance="primary"
          icon={switching ? <Spinner size="tiny" /> : <PersonSwapRegular />}
          disabled={switching}
          onClick={() => {
            setSwitching(true);
            void switchProfile(queryClient, owner.id);
          }}
        >
          {t('shell.otherOwner.switch', { name: firstName(owner.fullName) })}
        </Button>
      }
    >
      {t('shell.otherOwner.body', { name: owner.fullName, current: profile.fullName })}
    </Notice>
  );
}
