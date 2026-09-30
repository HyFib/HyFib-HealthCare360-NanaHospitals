import { Button, makeStyles, tokens } from '@fluentui/react-components';
import { EditRegular, PersonAddRegular, PersonDeleteRegular, PersonHeartRegular } from '@fluentui/react-icons';
import { EmptyState } from '@hc360/ui';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute } from '@tanstack/react-router';
import { useState } from 'react';
import { ConfirmDialog } from '@/components/ConfirmDialog';
import { InfoList } from '@/components/InfoList';
import { BackLink } from '@/components/links';
import { PortalPage } from '@/components/PortalPage';
import { ErrorBlock, LoadingCards } from '@/components/States';
import { useT } from '@/i18n';
import { useFormat } from '@/i18n/useFormat';
import { useToast } from '@/lib/toast';
import { privacyApi, privacyQueries } from '@/modules/privacy/api';
import { formatPhone } from '@/modules/privacy/model';
import { NomineeSheet } from '@/modules/privacy/NomineeSheet';
import { ReportCard } from '@/modules/reports/ReportParts';
import { useActiveProfile } from '@/shell/profileContext';

/** The patient's nominee (DPDP Act s14): who can use their data rights if they die or cannot act. */
export const Route = createFileRoute('/_auth/my-privacy/nominee')({
  component: NomineePage,
});

const useStyles = makeStyles({
  text: { margin: 0, fontSize: tokens.fontSizeBase400, lineHeight: tokens.lineHeightBase400 },
  muted: { margin: 0, color: tokens.colorNeutralForeground2 },
  meta: { margin: 0, color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200 },
  actions: {
    display: 'flex',
    flexDirection: 'column',
    gap: '10px',
    '@media (min-width: 600px)': { flexDirection: 'row', flexWrap: 'wrap' },
  },
});

function NomineePage() {
  const styles = useStyles();
  const t = useT();
  const f = useFormat();
  const toast = useToast();
  const queryClient = useQueryClient();
  const { profile, profiles } = useActiveProfile();
  const nominee = useQuery(privacyQueries.nominee(profile.id));
  const [sheetOpen, setSheetOpen] = useState(false);
  const [removeOpen, setRemoveOpen] = useState(false);
  const [removing, setRemoving] = useState(false);
  const current = nominee.data?.nominee ?? null;

  const remove = async () => {
    setRemoving(true);
    try {
      await privacyApi.removeNominee(profile.id);
      toast.success(t('myPrivacy.nominee.removed'));
      setRemoveOpen(false);
      await queryClient.invalidateQueries({ queryKey: privacyQueries.all(profile.id) });
    } catch (error) {
      toast.failure(error, t);
    } finally {
      setRemoving(false);
    }
  };

  return (
    <PortalPage
      title={t('myPrivacy.hub.nominee')}
      subtitle={profiles.length > 1 ? t('common.forPerson', { name: profile.fullName }) : undefined}
      back={<BackLink to="/my-privacy">{t('nav.myPrivacy')}</BackLink>}
      width="narrow"
    >
      <ReportCard>
        <p className={styles.text}>{t('myPrivacy.nominee.lead')}</p>
        <p className={styles.muted}>{t('myPrivacy.nominee.access')}</p>
      </ReportCard>

      {nominee.isPending ? (
        <LoadingCards count={1} />
      ) : nominee.isError ? (
        <ErrorBlock error={nominee.error} onRetry={() => void nominee.refetch()} />
      ) : current ? (
        <ReportCard title={current.name}>
          <InfoList
            items={[
              { label: t('myPrivacy.nominee.relationshipLabel'), value: current.relationship },
              { label: t('profile.phone'), value: formatPhone(current.phone), hideEmpty: true },
              { label: t('myPrivacy.field.email'), value: current.email, hideEmpty: true },
            ]}
          />
          <p className={styles.meta}>
            {t(current.recordedVia === 'Portal' ? 'myPrivacy.nominee.viaPortal' : 'myPrivacy.nominee.viaHospital', {
              date: f.date(current.updatedAt),
            })}
          </p>
          <div className={styles.actions}>
            <Button appearance="primary" size="large" icon={<EditRegular />} onClick={() => setSheetOpen(true)}>
              {t('myPrivacy.nominee.change')}
            </Button>
            <Button appearance="subtle" size="large" icon={<PersonDeleteRegular />} onClick={() => setRemoveOpen(true)}>
              {t('myPrivacy.nominee.remove')}
            </Button>
          </div>
        </ReportCard>
      ) : (
        <EmptyState
          icon={<PersonHeartRegular />}
          title={t('myPrivacy.nominee.none.title')}
          description={t('myPrivacy.nominee.none.body')}
          action={
            <Button appearance="primary" size="large" icon={<PersonAddRegular />} onClick={() => setSheetOpen(true)}>
              {t('myPrivacy.nominee.add')}
            </Button>
          }
        />
      )}

      {nominee.isSuccess && (
        // Keyed by the saved nominee, so the form always starts from what is on record now.
        <NomineeSheet
          key={current?.updatedAt ?? 'none'}
          open={sheetOpen}
          onClose={() => setSheetOpen(false)}
          patientId={profile.id}
          nominee={current}
        />
      )}

      <ConfirmDialog
        open={removeOpen && current !== null}
        title={t('myPrivacy.nominee.remove.title', { name: current?.name ?? '' })}
        confirmLabel={t('myPrivacy.nominee.remove')}
        dismissLabel={t('myPrivacy.nominee.remove.keep')}
        destructive
        busy={removing}
        onConfirm={() => void remove()}
        onDismiss={() => setRemoveOpen(false)}
      >
        <p style={{ margin: 0 }}>{t('myPrivacy.nominee.remove.body')}</p>
      </ConfirmDialog>
    </PortalPage>
  );
}
