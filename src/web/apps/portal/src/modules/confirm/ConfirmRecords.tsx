import { Button } from '@fluentui/react-components';
import { PersonQuestionMarkRegular, ShieldCheckmarkRegular, SignOutRegular } from '@fluentui/react-icons';
import { EmptyState } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { PortalPage } from '@/components/PortalPage';
import { Sheet } from '@/components/Sheet';
import { Notice, PageSpinner } from '@/components/States';
import { useT } from '@/i18n';
import { confirmQueries } from './api';
import { ConfirmRecordsForm } from './ConfirmRecordsForm';

/**
 * On the home page: some records on this number wait to be confirmed (ASVS V6.6.1), and "Confirm details" opens the form.
 * Nothing while none wait, and nothing if the server cannot say: the records the account sees are shown as they are.
 */
export function ConfirmRecordsBanner() {
  const t = useT();
  const status = useQuery(confirmQueries.status());
  const [open, setOpen] = useState(false);
  if (!status.data || status.data.unconfirmed === 0) {
    return null;
  }

  return (
    <>
      <Notice
        intent="warning"
        title={t('confirm.banner.title')}
        action={
          <Button appearance="primary" icon={<ShieldCheckmarkRegular />} onClick={() => setOpen(true)}>
            {t('confirm.banner.action')}
          </Button>
        }
      >
        {t('confirm.banner.body')}
      </Notice>
      <Sheet open={open} title={t('confirm.title')} onClose={() => setOpen(false)}>
        <ConfirmRecordsForm status={status.data} onConfirmed={() => setOpen(false)} />
      </Sheet>
    </>
  );
}

/**
 * The page for an account that sees nobody's records yet. When records on its number wait to be confirmed, it asks for
 * the patient's date of birth or UHID; otherwise it says the number is on no registration yet, as before.
 */
export function NoRecordsYet({ onSignOut }: { onSignOut: () => void }) {
  const t = useT();
  const status = useQuery(confirmQueries.status());
  if (status.isPending) {
    return <PageSpinner />;
  }

  const signOut = (
    <Button size="large" icon={<SignOutRegular />} onClick={onSignOut}>
      {t('common.signOut')}
    </Button>
  );
  if (status.data && status.data.unconfirmed > 0) {
    return (
      <PortalPage title={t('confirm.title')} width="narrow">
        <ConfirmRecordsForm status={status.data} />
        <div>{signOut}</div>
      </PortalPage>
    );
  }

  return (
    <PortalPage title={t('shell.noProfiles.title')} width="narrow">
      <EmptyState
        icon={<PersonQuestionMarkRegular />}
        title={t('shell.noProfiles.title')}
        description={t('shell.noProfiles.body')}
        action={
          <Button appearance="primary" size="large" icon={<SignOutRegular />} onClick={onSignOut}>
            {t('common.signOut')}
          </Button>
        }
      />
    </PortalPage>
  );
}
