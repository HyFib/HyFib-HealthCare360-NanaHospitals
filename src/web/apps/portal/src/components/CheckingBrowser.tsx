import { Caption1, Spinner, tokens } from '@fluentui/react-components';
import { useT } from '@/i18n';

/**
 * "Checking your browser…", while the anti-automation check (lib/proofOfWork) takes longer than usual. The status region
 * is always there, empty otherwise, so a screen reader announces the words when they appear and nothing at other times.
 */
export function CheckingBrowser({ active }: { active: boolean }) {
  const t = useT();
  return (
    <div role="status" aria-live="polite" style={{ minHeight: 0 }}>
      {active && (
        <Caption1
          style={{ display: 'inline-flex', alignItems: 'center', gap: 8, color: tokens.colorNeutralForeground2 }}
        >
          <Spinner size="extra-tiny" aria-hidden />
          {t('common.checkingBrowser')}
        </Caption1>
      )}
    </div>
  );
}
