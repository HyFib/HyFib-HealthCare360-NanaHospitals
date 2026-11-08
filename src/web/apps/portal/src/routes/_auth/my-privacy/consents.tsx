import { Button, makeStyles, tokens } from '@fluentui/react-components';
import { PeopleCheckmarkRegular, ShieldDismissRegular } from '@fluentui/react-icons';
import { EmptyState } from '@hc360/ui';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute } from '@tanstack/react-router';
import { useState } from 'react';
import { ConfirmDialog } from '@/components/ConfirmDialog';
import { InfoList } from '@/components/InfoList';
import { BackLink } from '@/components/links';
import { PortalPage } from '@/components/PortalPage';
import { ErrorBlock, LoadingCards } from '@/components/States';
import { useT, type MessageKey } from '@/i18n';
import { useFormat } from '@/i18n/useFormat';
import type { PortalConsentArtefact } from '@/lib/types';
import { useToast } from '@/lib/toast';
import { privacyApi, privacyQueries } from '@/modules/privacy/api';
import { ReportCard } from '@/modules/reports/ReportParts';
import { useActiveProfile } from '@/shell/profileContext';

/**
 * Consents the patient gave through a consent manager registered with the Data Protection Board (DPDP Act s.6(7) to
 * (9)): what each is for, what it covers and until when, and withdrawing it here, as easily as it was given (s.6(4)).
 */
export const Route = createFileRoute('/_auth/my-privacy/consents')({
  component: ConsentsPage,
});

const PURPOSES = ['DataSharing', 'Research', 'SmsWhatsAppUpdates', 'MarketingCommunication', 'Photography'];

const useStyles = makeStyles({
  text: { margin: 0, fontSize: tokens.fontSizeBase400, lineHeight: tokens.lineHeightBase400 },
  status: { margin: 0, color: tokens.colorNeutralForeground2 },
});

export function purposeKey(purpose: string): MessageKey | null {
  return PURPOSES.includes(purpose) ? (`myPrivacy.consents.purpose.${purpose}` as MessageKey) : null;
}

function ConsentsPage() {
  const styles = useStyles();
  const t = useT();
  const f = useFormat();
  const toast = useToast();
  const queryClient = useQueryClient();
  const { profile, profiles } = useActiveProfile();
  const artefacts = useQuery(privacyQueries.consentArtefacts(profile.id));
  const [withdrawing, setWithdrawing] = useState<PortalConsentArtefact | null>(null);
  const [busy, setBusy] = useState(false);
  const purpose = (artefact: PortalConsentArtefact) => {
    const key = purposeKey(artefact.purpose);
    return key ? t(key) : artefact.purpose;
  };

  const withdraw = async () => {
    if (!withdrawing) {
      return;
    }
    setBusy(true);
    try {
      await privacyApi.withdrawConsentArtefact(withdrawing.id);
      toast.success(t('myPrivacy.consents.withdrawn.done'));
      setWithdrawing(null);
      await queryClient.invalidateQueries({ queryKey: privacyQueries.all(profile.id) });
    } catch (error) {
      toast.failure(error, t);
    } finally {
      setBusy(false);
    }
  };

  return (
    <PortalPage
      title={t('myPrivacy.consents.title')}
      subtitle={profiles.length > 1 ? t('common.forPerson', { name: profile.fullName }) : undefined}
      back={<BackLink to="/my-privacy">{t('nav.myPrivacy')}</BackLink>}
      width="narrow"
    >
      <ReportCard>
        <p className={styles.text}>{t('myPrivacy.consents.lead')}</p>
      </ReportCard>

      {artefacts.isPending ? (
        <LoadingCards count={2} />
      ) : artefacts.isError ? (
        <ErrorBlock error={artefacts.error} onRetry={() => void artefacts.refetch()} />
      ) : artefacts.data.length === 0 ? (
        <EmptyState
          icon={<PeopleCheckmarkRegular />}
          title={t('myPrivacy.consents.none.title')}
          description={t('myPrivacy.consents.none.body')}
        />
      ) : (
        artefacts.data.map((artefact) => (
          <ReportCard key={artefact.id} title={purpose(artefact)}>
            <InfoList
              items={[
                { label: t('myPrivacy.consents.covers'), value: artefact.dataItems.join(', ') },
                { label: t('myPrivacy.consents.from'), value: artefact.consentManagerName },
                { label: t('myPrivacy.consents.validUntil'), value: f.date(artefact.validUntil) },
              ]}
            />
            <p className={styles.status}>
              {artefact.inForce
                ? t('myPrivacy.consents.inForce')
                : artefact.status === 'Withdrawn'
                  ? t('myPrivacy.consents.withdrawn', { date: f.date(artefact.withdrawnAt ?? artefact.validUntil) })
                  : t('myPrivacy.consents.expired', { date: f.date(artefact.validUntil) })}
            </p>
            {artefact.status === 'Active' && (
              <Button
                appearance="secondary"
                size="large"
                icon={<ShieldDismissRegular />}
                onClick={() => setWithdrawing(artefact)}
              >
                {t('myPrivacy.consents.withdraw')}
              </Button>
            )}
          </ReportCard>
        ))
      )}

      <ConfirmDialog
        open={withdrawing !== null}
        title={t('myPrivacy.consents.withdraw.title', { purpose: withdrawing ? purpose(withdrawing) : '' })}
        confirmLabel={t('myPrivacy.consents.withdraw')}
        dismissLabel={t('myPrivacy.consents.withdraw.keep')}
        destructive
        busy={busy}
        onConfirm={() => void withdraw()}
        onDismiss={() => setWithdrawing(null)}
      >
        <p style={{ margin: 0 }}>{t('myPrivacy.consents.withdraw.body')}</p>
      </ConfirmDialog>
    </PortalPage>
  );
}
