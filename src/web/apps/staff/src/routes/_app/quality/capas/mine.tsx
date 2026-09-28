import { ClipboardTaskRegular } from '@fluentui/react-icons';
import { Page, PageHeader, Section } from '@hc360/ui';
import { createFileRoute } from '@tanstack/react-router';
import { RequirePermission } from '@/modules/hr/shared/NoAccess';
import { QUALITY_PERMISSIONS as Q } from '@/modules/quality/labels';
import { MyActionsTable } from '@/modules/quality/MyActions';

export const Route = createFileRoute('/_app/quality/capas/mine')({
  component: () => (
    <RequirePermission permission={Q.incidentsReport} what="your corrective actions">
      <Page maxWidth={1200}>
        <PageHeader title="My actions" subtitle="Corrective and preventive actions you own, across all hospitals of the group" icon={<ClipboardTaskRegular />} />
        <Section flush>
          <MyActionsTable />
        </Section>
      </Page>
    </RequirePermission>
  ),
});
