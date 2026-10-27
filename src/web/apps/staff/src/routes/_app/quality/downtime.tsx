import { Button } from '@fluentui/react-components';
import { AddRegular, PlugDisconnectedRegular } from '@fluentui/react-icons';
import { Page, PageHeader, Section } from '@hc360/ui';
import { createFileRoute } from '@tanstack/react-router';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { RequirePermission } from '@/modules/hr/shared/NoAccess';
import {
  DowntimeFigures,
  DowntimeStepDialog,
  DowntimeTable,
  RecordDowntimeDialog,
} from '@/modules/quality/DowntimeLog';
import { DOWNTIME_PERMISSION, type DowntimeEvent } from '@/modules/quality/downtimeApi';

export const Route = createFileRoute('/_app/quality/downtime')({
  component: DowntimePage,
});

/**
 * The downtime log (NABH IMS: downtime monitored and its procedure rehearsed): each spell of working without HC360,
 * and each drill, from start to the sign-off of its paper records' back-entry, with the last 12 months' figures.
 */
function DowntimePage() {
  const allowed = usePermission(DOWNTIME_PERMISSION);
  const [recording, setRecording] = useState(false);
  const [open, setOpen] = useState<DowntimeEvent | null>(null);
  return (
    <Page>
      <PageHeader
        title="Downtime"
        subtitle="When the hospital worked without HC360, and drills of the downtime procedure"
        icon={<PlugDisconnectedRegular />}
        actions={
          allowed && (
            <Button appearance="primary" icon={<AddRegular />} onClick={() => setRecording(true)}>
              Record downtime
            </Button>
          )
        }
      />
      <RequirePermission permission={DOWNTIME_PERMISSION} what="the downtime log">
        <Section title="Last 12 months">
          <DowntimeFigures />
        </Section>
        <Section flush title="Downtime">
          <DowntimeTable onOpen={setOpen} />
        </Section>
        <RecordDowntimeDialog open={recording} onClose={() => setRecording(false)} />
        {open && <DowntimeStepDialog downtime={open} onClose={() => setOpen(null)} />}
      </RequirePermission>
    </Page>
  );
}
