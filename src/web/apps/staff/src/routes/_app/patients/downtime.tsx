import { Button } from '@fluentui/react-components';
import { AddRegular, PlugDisconnectedRegular } from '@fluentui/react-icons';
import { useSession } from '@hc360/core';
import { Page, PageHeader, Section } from '@hc360/ui';
import { createFileRoute } from '@tanstack/react-router';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { NoAccess } from '@/modules/admin/NoAccess';
import {
  DowntimeBlockDrawer,
  DowntimeBlocks,
  IssueDowntimeUhidsDialog,
  PaperFormsList,
} from '@/modules/patients/DowntimeUhids';
import { DOWNTIME_PERMISSION } from '@/modules/patients/downtimeApi';

export const Route = createFileRoute('/_app/patients/downtime')({
  component: DowntimeUhidsPage,
});

/**
 * Downtime UHIDs (NABH IMS, continuity when HC360 is down): blocks of UHIDs for the registration desks' downtime kits,
 * so patients registered on paper leave with their real UHID, and which of them have been entered since.
 */
function DowntimeUhidsPage() {
  const allowed = usePermission(DOWNTIME_PERMISSION);
  const facilityName = useSession((s) => s.user?.facilityName) ?? 'Nana Hospitals';
  const [issuing, setIssuing] = useState(false);
  const [open, setOpen] = useState<string | null>(null);

  if (!allowed) {
    return (
      <Page>
        <PageHeader title="Downtime UHIDs" />
        <NoAccess what="downtime UHIDs" />
      </Page>
    );
  }

  return (
    <Page>
      <PageHeader
        title="Downtime UHIDs"
        subtitle="UHIDs kept with each desk's paper forms for when HC360 cannot be reached, and which have been entered since"
        icon={<PlugDisconnectedRegular />}
        actions={
          <Button appearance="primary" icon={<AddRegular />} onClick={() => setIssuing(true)}>
            Issue UHIDs
          </Button>
        }
      />
      <Section flush title="Kits">
        <DowntimeBlocks onOpen={setOpen} />
      </Section>
      <Section
        title="Paper forms"
        description="Blank forms for the kits. Each follows HC360's own screen, so it is entered field by field when HC360 is back."
      >
        <PaperFormsList facilityName={facilityName} />
      </Section>
      <IssueDowntimeUhidsDialog
        open={issuing}
        onClose={() => setIssuing(false)}
        onIssued={(detail) => {
          setIssuing(false);
          setOpen(detail.block.id);
        }}
      />
      {open && <DowntimeBlockDrawer blockId={open} onClose={() => setOpen(null)} />}
    </Page>
  );
}
