import { makeStyles, Spinner, Switch, tokens } from '@fluentui/react-components';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute } from '@tanstack/react-router';
import { useState } from 'react';
import { BackLink } from '@/components/links';
import { PortalPage } from '@/components/PortalPage';
import { ErrorBlock, LoadingCards, Notice } from '@/components/States';
import { useT, type MessageKey } from '@/i18n';
import { useFormat } from '@/i18n/useFormat';
import { describeError } from '@/lib/errors';
import { useToast } from '@/lib/toast';
import type { CommunicationPreferences } from '@/lib/types';
import { feedbackApi, feedbackQueries } from '@/modules/feedback/api';
import { useActiveProfile } from '@/shell/profileContext';

export const Route = createFileRoute('/_auth/profile/preferences')({
  component: PreferencesPage,
});

type Choice = 'updates' | 'marketing';

const CHOICES: { key: Choice; title: MessageKey; hint: MessageKey; changedAt: keyof CommunicationPreferences }[] = [
  { key: 'updates', title: 'preferences.updates', hint: 'preferences.updates.hint', changedAt: 'updatesChangedAt' },
  {
    key: 'marketing',
    title: 'preferences.marketing',
    hint: 'preferences.marketing.hint',
    changedAt: 'marketingChangedAt',
  },
];

const useStyles = makeStyles({
  lead: {
    margin: 0,
    color: tokens.colorNeutralForeground2,
    fontSize: tokens.fontSizeBase400,
    lineHeight: tokens.lineHeightBase400,
  },
  list: { display: 'flex', flexDirection: 'column', gap: '10px' },
  row: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: '12px',
    minHeight: '72px',
    padding: '12px 14px',
    boxSizing: 'border-box',
    borderRadius: tokens.borderRadiusXLarge,
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  text: { display: 'flex', flexDirection: 'column', gap: '2px', minWidth: 0 },
  title: { fontSize: tokens.fontSizeBase400, fontWeight: tokens.fontWeightSemibold },
  hint: { color: tokens.colorNeutralForeground2 },
  changed: { color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200 },
  control: { display: 'flex', alignItems: 'center', gap: '6px', flexShrink: 0 },
  note: { margin: 0, color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200 },
});

function PreferencesPage() {
  const styles = useStyles();
  const t = useT();
  const f = useFormat();
  const toast = useToast();
  const queryClient = useQueryClient();
  const { profile } = useActiveProfile();
  const query = feedbackQueries.preferences(profile.id);
  const preferences = useQuery(query);
  const [saving, setSaving] = useState<Choice | null>(null);
  const [error, setError] = useState<string | null>(null);

  // Each switch saves at once (no separate Save button to forget); the other choice is sent unchanged.
  const change = async (choice: Choice, value: boolean) => {
    const current = preferences.data;
    if (!current || saving) {
      return;
    }
    setSaving(choice);
    setError(null);
    const next = { ...current, [choice]: value };
    try {
      await feedbackApi.savePreferences({ patientId: profile.id, marketing: next.marketing, updates: next.updates });
      await queryClient.invalidateQueries({ queryKey: query.queryKey });
      toast.success(t('preferences.saved'));
    } catch (err) {
      setError(describeError(err, t, 'errors.saveTitle').message);
    } finally {
      setSaving(null);
    }
  };

  return (
    <PortalPage
      title={t('preferences.title')}
      back={<BackLink to="/profile">{t('profile.title')}</BackLink>}
      width="narrow"
    >
      <p className={styles.lead}>{t('preferences.lead', { name: profile.fullName })}</p>
      {preferences.isPending ? (
        <LoadingCards count={2} />
      ) : preferences.isError ? (
        <ErrorBlock error={preferences.error} onRetry={() => void preferences.refetch()} />
      ) : (
        <div className={styles.list}>
          {CHOICES.map((choice) => {
            const changedAt = preferences.data[choice.changedAt] as string | null;
            const titleId = `pref-${choice.key}`;
            return (
              <div key={choice.key} className={styles.row}>
                <span className={styles.text}>
                  <span id={titleId} className={styles.title}>
                    {t(choice.title)}
                  </span>
                  <span className={styles.hint}>{t(choice.hint)}</span>
                  {changedAt && (
                    <span className={styles.changed}>{t('preferences.changed', { date: f.date(changedAt) })}</span>
                  )}
                </span>
                <span className={styles.control}>
                  {saving === choice.key && <Spinner size="tiny" aria-hidden />}
                  <Switch
                    aria-labelledby={titleId}
                    checked={preferences.data[choice.key]}
                    disabled={saving !== null}
                    onChange={(_, data) => void change(choice.key, data.checked)}
                  />
                </span>
              </div>
            );
          })}
        </div>
      )}
      {error && <Notice intent="error">{error}</Notice>}
      <p className={styles.note}>{t('preferences.otp')}</p>
    </PortalPage>
  );
}
