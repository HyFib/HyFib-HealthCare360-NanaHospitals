import { Button } from '@fluentui/react-components';
import { AddRegular, ClockArrowDownloadRegular } from '@fluentui/react-icons';
import { Page, PageHeader, Section } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute } from '@tanstack/react-router';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { NoAccess } from '@/modules/admin/NoAccess';
import {
  BackEntryWindows,
  IssueNumbersDialog,
  LateBackEntries,
  NumberBlockDrawer,
  NumberBlocks,
  PackPanel,
  RequestLateDialog,
} from '@/modules/downtime/Continuity';
import {
  BACK_ENTRY_PERMISSION,
  continuityQueries,
  DOWNTIME_PERMISSION,
  PACK_PERMISSION,
} from '@/modules/downtime/continuityApi';

export const Route = createFileRoute('/_app/patients/continuity')({
  component: ContinuityPage,
});

/**
 * Downtime continuity (NABH IMS): the pre-downtime pack, the visit, bill and receipt numbers each counter holds for
 * working on paper, and late back-entry of paper older than its window.
 */
function ContinuityPage() {
  const canPack = usePermission(PACK_PERMISSION);
  const canIssue = usePermission(DOWNTIME_PERMISSION);
  const canBackEnter = usePermission(BACK_ENTRY_PERMISSION);
  const [issuing, setIssuing] = useState(false);
  const [open, setOpen] = useState<string | null>(null);
  const [asking, setAsking] = useState(false);
  const windows = useQuery({ ...continuityQueries.windows(), enabled: canBackEnter });

  if (!canPack && !canIssue && !canBackEnter) {
    return (
      <Page>
        <PageHeader title="Downtime continuity" />
        <NoAccess what="downtime continuity" />
      </Page>
    );
  }

  return (
    <Page>
      <PageHeader
        title="Downtime continuity"
        subtitle="What the hospital prints and holds to keep working when HC360 cannot be reached, and how paper is entered afterwards"
        icon={<ClockArrowDownloadRegular />}
      />
      {canPack && (
        <Section title="Pre-downtime pack">
          <PackPanel />
        </Section>
      )}
      {canIssue && (
        <Section
          flush
          title="Visit, bill and receipt numbers"
          actions={
            <Button appearance="primary" icon={<AddRegular />} onClick={() => setIssuing(true)}>
              Issue numbers
            </Button>
          }
        >
          <NumberBlocks onOpen={setOpen} />
        </Section>
      )}
      {canBackEnter && (
        <>
          <Section
            flush
            title="Back-entry windows"
            description="Set by the administrator in Settings; lengthening one waits for a second administrator."
          >
            <BackEntryWindows />
          </Section>
          <Section
            flush
            title="Late back-entry"
            actions={<Button onClick={() => setAsking(true)}>Ask for a late back-entry</Button>}
          >
            <LateBackEntries />
          </Section>
        </>
      )}
      <IssueNumbersDialog
        open={issuing}
        onClose={() => setIssuing(false)}
        onIssued={(detail) => {
          setIssuing(false);
          setOpen(detail.block.id);
        }}
      />
      {open && <NumberBlockDrawer blockId={open} onClose={() => setOpen(null)} />}
      {asking && <RequestLateDialog open onClose={() => setAsking(false)} windows={windows.data ?? []} />}
    </Page>
  );
}
