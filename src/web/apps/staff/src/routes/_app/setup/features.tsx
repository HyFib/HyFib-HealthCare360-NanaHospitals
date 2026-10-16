import { isApiError } from '@hc360/core';
import { ConfirmDialog, ErrorState, LoadingState, Page, PageHeader, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute } from '@tanstack/react-router';
import { useState } from 'react';
import { PUBLIC_SETTINGS_KEY } from '@/lib/lookups';
import { usePermission } from '@/lib/permissions';
import { NoAccess } from '@/modules/admin/NoAccess';
import {
  changedMessage,
  featureKeys,
  featureQueries,
  featuresApi,
  turnOffQuestion,
  type FeatureDto,
} from '@/modules/features/api';
import { FeatureList } from '@/modules/features/FeatureList';

export const Route = createFileRoute('/_app/setup/features')({
  component: FeaturesPage,
});

/**
 * Hospital setup > Features: the parts of HealthCare360 an administrator can switch off for the whole hospital (video
 * consultations, the patient portal). The server refuses a switched-off feature at once; menus follow.
 */
function FeaturesPage() {
  const allowed = usePermission('admin.settings.manage');
  const notify = useNotify();
  const queryClient = useQueryClient();
  const features = useQuery({ ...featureQueries.list(), enabled: allowed });
  const [turningOff, setTurningOff] = useState<FeatureDto | null>(null);

  const save = useMutation({
    mutationFn: ({ feature, enabled }: { feature: FeatureDto; enabled: boolean }) =>
      featuresApi.set(feature.key, enabled, feature.version),
    onSuccess: (saved) => {
      setTurningOff(null);
      notify.success(changedMessage(saved, saved.enabled));
      queryClient.setQueryData<FeatureDto[]>(featureKeys.all, (list) =>
        list?.map((feature) => (feature.key === saved.key ? saved : feature)),
      );
      void queryClient.invalidateQueries({ queryKey: featureKeys.all });
      // The menu, the home screen and the command palette hide or show the feature's screens.
      void queryClient.invalidateQueries({ queryKey: PUBLIC_SETTINGS_KEY });
    },
    onError: (error) => {
      setTurningOff(null);
      notify.error(error, 'Could not change the feature');
      if (isApiError(error) && error.isConflict) {
        // Another administrator changed it: show the switch as it is now.
        void queryClient.invalidateQueries({ queryKey: featureKeys.all });
      }
    },
  });

  if (!allowed) {
    return (
      <Page>
        <PageHeader title="Features" />
        <NoAccess what="feature switches" />
      </Page>
    );
  }

  const change = (feature: FeatureDto, enabled: boolean) => {
    if (enabled) {
      save.mutate({ feature, enabled });
    } else {
      setTurningOff(feature);
    }
  };
  const question = turningOff ? turnOffQuestion(turningOff) : null;

  return (
    <Page maxWidth={900}>
      <PageHeader
        title="Features"
        subtitle="Turn parts of HealthCare360 on or off for the whole hospital. A change applies at once; other computers update their menus within a few minutes."
      />
      {features.isLoading ? (
        <LoadingState label="Loading features…" />
      ) : features.error || !features.data ? (
        <ErrorState
          error={features.error}
          onRetry={() => void features.refetch()}
          title="Could not load the features"
        />
      ) : (
        <FeatureList features={features.data} saving={save.isPending} onChange={change} />
      )}
      <ConfirmDialog
        open={turningOff !== null}
        title={question?.title ?? ''}
        confirmLabel={question?.confirmLabel ?? 'Turn off'}
        destructive
        busy={save.isPending}
        onConfirm={() => turningOff && save.mutate({ feature: turningOff, enabled: false })}
        onCancel={() => setTurningOff(null)}
      >
        {question?.body}
      </ConfirmDialog>
    </Page>
  );
}
