import { Tab, TabList } from '@fluentui/react-components';
import { Page, PageHeader } from '@hc360/ui';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { usePermission } from '@/lib/permissions';
import { HOUSEKEEPING_PERMISSIONS, HOUSEKEEPING_READERS } from '@/modules/housekeeping/api';
import { AreasPanel, BoardPanel, RequestsPanel } from '@/modules/housekeeping/HousekeepingPanels';
import { RequirePermission } from '@/modules/hr/shared/NoAccess';

type HousekeepingTab = 'requests' | 'board' | 'areas';
const TABS: HousekeepingTab[] = ['requests', 'board', 'areas'];

export const Route = createFileRoute('/_app/housekeeping/')({
  validateSearch: (s: Record<string, unknown>): { tab?: HousekeepingTab } => ({
    tab: TABS.includes(s.tab as HousekeepingTab) ? (s.tab as HousekeepingTab) : undefined,
  }),
  component: () => (
    <RequirePermission permission={HOUSEKEEPING_READERS} what="housekeeping">
      <HousekeepingPage />
    </RequirePermission>
  ),
});

/**
 * Housekeeping beyond the bed turnaround: cleaning asked for by the wards with response times, the day's scheduled
 * cleaning against each area's checklist, and the areas themselves. Every cleaning is verified by a supervisor who did
 * not do it.
 */
function HousekeepingPage() {
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const canSchedule = usePermission([
    HOUSEKEEPING_PERMISSIONS.work,
    HOUSEKEEPING_PERMISSIONS.verify,
    HOUSEKEEPING_PERMISSIONS.manage,
  ]);
  const tab = search.tab && (canSchedule || search.tab === 'requests') ? search.tab : 'requests';

  return (
    <Page>
      <PageHeader
        title="Housekeeping"
        subtitle="Spills are answered within 15 minutes and terminal cleaning within the hour; areas are cleaned on their schedule against their checklists, and a supervisor who did not do the cleaning verifies it."
      />
      <TabList
        selectedValue={tab}
        onTabSelect={(_, d) => void navigate({ search: { tab: d.value as HousekeepingTab } })}
      >
        <Tab value="requests">Requests</Tab>
        {canSchedule && <Tab value="board">Today&apos;s schedule</Tab>}
        {canSchedule && <Tab value="areas">Areas</Tab>}
      </TabList>
      {tab === 'requests' && <RequestsPanel />}
      {tab === 'board' && <BoardPanel />}
      {tab === 'areas' && <AreasPanel />}
    </Page>
  );
}
