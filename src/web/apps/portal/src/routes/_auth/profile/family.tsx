import { Button, makeStyles, tokens } from '@fluentui/react-components';
import { PeopleRegular, PersonAddRegular, PersonRegular } from '@fluentui/react-icons';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute } from '@tanstack/react-router';
import { useState } from 'react';
import { ConfirmDialog } from '@/components/ConfirmDialog';
import { BackLink } from '@/components/links';
import { ListRow, RowCard } from '@/components/ListRow';
import { PortalPage } from '@/components/PortalPage';
import { ErrorBlock, LoadingCards } from '@/components/States';
import { useT } from '@/i18n';
import { useFormat } from '@/i18n/useFormat';
import { PROFILES_QUERY_KEY } from '@/lib/session';
import { useToast } from '@/lib/toast';
import type { PortalLinkedRelative, PortalSharing } from '@/lib/types';
import { AddFamilySheet } from '@/modules/family/AddFamilySheet';
import { familyApi, familyQueries } from '@/modules/family/api';
import { RELATION_KEY } from '@/modules/family/model';
import { ReportCard } from '@/modules/reports/ReportParts';

export const Route = createFileRoute('/_auth/profile/family')({
  component: FamilyPage,
});

const useStyles = makeStyles({
  list: { listStyle: 'none', margin: 0, padding: 0, display: 'flex', flexDirection: 'column', gap: '10px' },
  hint: { margin: 0, color: tokens.colorNeutralForeground2 },
  add: { alignSelf: 'flex-start' },
});

type Ending = { kind: 'remove'; relative: PortalLinkedRelative } | { kind: 'stop'; sharing: PortalSharing };

/**
 * Family members on other mobile numbers (PP-01): the relatives this account sees, with Remove; adding one with a code
 * sent to their number; and the accounts that see the records of someone on this number, with Stop sharing.
 */
function FamilyPage() {
  const styles = useStyles();
  const t = useT();
  const f = useFormat();
  const toast = useToast();
  const queryClient = useQueryClient();
  const family = useQuery(familyQueries.list());
  const [adding, setAdding] = useState(false);
  const [ending, setEnding] = useState<Ending | null>(null);
  const [busy, setBusy] = useState(false);

  const end = async () => {
    if (!ending) {
      return;
    }
    setBusy(true);
    try {
      if (ending.kind === 'remove') {
        await familyApi.end(ending.relative.linkId);
        toast.success(t('family.removed', { name: ending.relative.fullName }));
      } else {
        await familyApi.end(ending.sharing.linkId);
        toast.success(
          t('family.stopped', { phone: ending.sharing.accountPhoneMasked, name: ending.sharing.patientName }),
        );
      }
      setEnding(null);
    } catch (err) {
      toast.failure(err, t);
    } finally {
      setBusy(false);
      // Whether it ended now or someone ended it meanwhile, the lists and the people shown change.
      void queryClient.invalidateQueries({ queryKey: familyQueries.all });
      void queryClient.invalidateQueries({ queryKey: PROFILES_QUERY_KEY });
    }
  };

  const linked = family.data?.linked ?? [];
  const sharedWith = family.data?.sharedWith ?? [];

  return (
    <PortalPage
      title={t('family.title')}
      subtitle={t('family.subtitle')}
      width="narrow"
      back={<BackLink to="/profile">{t('profile.title')}</BackLink>}
    >
      {family.isLoading ? (
        <LoadingCards count={2} />
      ) : family.error ? (
        <ErrorBlock error={family.error} onRetry={() => void family.refetch()} />
      ) : (
        <>
          <ReportCard title={t('family.linked.title')}>
            {linked.length === 0 ? (
              <p className={styles.hint}>{t('family.linked.empty')}</p>
            ) : (
              <ul className={styles.list}>
                {linked.map((relative) => (
                  <li key={relative.linkId}>
                    <RowCard>
                      <ListRow
                        icon={<PersonRegular />}
                        title={relative.fullName}
                        subtitle={t('family.linked.relation', { relation: t(RELATION_KEY[relative.relation]) })}
                        meta={`${t('common.uhid')} ${relative.uhid} · ${t('family.linked.since', { date: f.date(relative.linkedAt) })}`}
                        chevron={false}
                        trailing={
                          <Button
                            aria-label={t('family.remove.label', { name: relative.fullName })}
                            onClick={() => setEnding({ kind: 'remove', relative })}
                          >
                            {t('family.remove')}
                          </Button>
                        }
                      />
                    </RowCard>
                  </li>
                ))}
              </ul>
            )}
            <Button
              appearance="primary"
              size="large"
              icon={<PersonAddRegular />}
              className={styles.add}
              onClick={() => setAdding(true)}
            >
              {t('family.add')}
            </Button>
          </ReportCard>

          <ReportCard title={t('family.shared.title')}>
            {sharedWith.length === 0 ? (
              <p className={styles.hint}>{t('family.shared.empty')}</p>
            ) : (
              <ul className={styles.list}>
                {sharedWith.map((sharing) => (
                  <li key={sharing.linkId}>
                    <RowCard>
                      <ListRow
                        icon={<PeopleRegular />}
                        tone="neutral"
                        title={sharing.accountPhoneMasked}
                        subtitle={t('family.shared.sees', {
                          name: sharing.patientName,
                          relation: t(RELATION_KEY[sharing.relation]),
                        })}
                        meta={t('family.shared.since', { date: f.date(sharing.linkedAt) })}
                        chevron={false}
                        trailing={
                          <Button
                            aria-label={t('family.stop.label', { phone: sharing.accountPhoneMasked })}
                            onClick={() => setEnding({ kind: 'stop', sharing })}
                          >
                            {t('family.stop')}
                          </Button>
                        }
                      />
                    </RowCard>
                  </li>
                ))}
              </ul>
            )}
          </ReportCard>
        </>
      )}

      <AddFamilySheet open={adding} onClose={() => setAdding(false)} />
      <ConfirmDialog
        open={ending !== null}
        title={
          ending?.kind === 'remove'
            ? t('family.remove.title', { name: ending.relative.fullName })
            : ending?.kind === 'stop'
              ? t('family.stop.title', { phone: ending.sharing.accountPhoneMasked })
              : ''
        }
        confirmLabel={ending?.kind === 'stop' ? t('family.stop') : t('family.remove')}
        destructive
        busy={busy}
        onConfirm={() => void end()}
        onDismiss={() => setEnding(null)}
      >
        {ending?.kind === 'remove' && t('family.remove.body', { name: ending.relative.fullName })}
        {ending?.kind === 'stop' &&
          t('family.stop.body', { phone: ending.sharing.accountPhoneMasked, name: ending.sharing.patientName })}
      </ConfirmDialog>
    </PortalPage>
  );
}
