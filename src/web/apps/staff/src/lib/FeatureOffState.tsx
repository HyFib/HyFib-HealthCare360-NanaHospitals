import { Button } from '@fluentui/react-components';
import { CloudOffRegular, PlugDisconnectedRegular, VideoOffRegular } from '@fluentui/react-icons';
import { EmptyState } from '@hc360/ui';
import { useQueryClient } from '@tanstack/react-query';
import { useNavigate } from '@tanstack/react-router';
import { useEffect } from 'react';
import { FEATURE_OFF_TEXT, type FeatureKey } from './featureSwitches';
import { PUBLIC_SETTINGS_KEY } from './lookups';
import { usePermission } from './permissions';

const OFF_ICON: Record<FeatureKey, typeof VideoOffRegular> = {
  telehealth: VideoOffRegular,
  patientPortal: PlugDisconnectedRegular,
  abdm: CloudOffRegular,
};

/**
 * Shown instead of a screen whose feature is switched off, whether the menu already knew or the server just said so
 * (`feature.disabled`). It reloads the switches, so the menu catches up too. Administrators get the way back.
 */
export function FeatureOffState({ feature }: { feature: FeatureKey }) {
  const canSwitch = usePermission('admin.settings.manage');
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const text = FEATURE_OFF_TEXT[feature];
  const Icon = OFF_ICON[feature];

  useEffect(() => {
    void queryClient.invalidateQueries({ queryKey: PUBLIC_SETTINGS_KEY });
  }, [queryClient]);

  return (
    <EmptyState
      icon={<Icon />}
      title={text.title}
      description={`${text.description} ${
        canSwitch ? 'You can turn it back on under Hospital setup, Features.' : 'An administrator can turn it back on.'
      }`}
      action={
        canSwitch ? (
          <Button appearance="primary" onClick={() => void navigate({ to: '/setup/features' })}>
            Open Features
          </Button>
        ) : undefined
      }
    />
  );
}
