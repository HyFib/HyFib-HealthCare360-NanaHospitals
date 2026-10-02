import { Page, PageHeader } from '@hc360/ui';
import { FeatureOffState } from '@/lib/FeatureOffState';

/** The video consultation screens while an administrator has switched them off: a plain explanation, no errors. */
export function VideoOffPage() {
  return (
    <Page maxWidth={900}>
      <PageHeader title="Video consultations" />
      <FeatureOffState feature="telehealth" />
    </Page>
  );
}
