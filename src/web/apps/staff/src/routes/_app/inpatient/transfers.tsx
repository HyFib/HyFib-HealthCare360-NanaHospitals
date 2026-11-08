import { Field, Select } from '@fluentui/react-components';
import { ArrowSwapRegular } from '@fluentui/react-icons';
import { Page, PageHeader } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { usePermission } from '@/lib/permissions';
import { inpatientQueries } from '@/modules/inpatient/api';
import { TransferRequestList } from '@/modules/inpatient/TransferRequests';

export const Route = createFileRoute('/_app/inpatient/transfers')({
  validateSearch: (s: Record<string, unknown>): { wardId?: string } => ({
    wardId: typeof s.wardId === 'string' && s.wardId ? s.wardId : undefined,
  }),
  component: TransfersPage,
});

/** Transfer requests between wards, for the receiving ward to accept and the sending ward to follow. */
function TransfersPage() {
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const canSend = usePermission('inpatient.admissions.transfer');
  const wards = useQuery(inpatientQueries.wards());
  return (
    <Page>
      <PageHeader
        title="Transfer requests"
        icon={<ArrowSwapRegular />}
        subtitle="Moves between wards: the receiving ward's nurse in charge or a doctor accepts; urgent moves to or from critical care are accepted within the hour."
        actions={
          <Field label="Ward" orientation="horizontal">
            <Select
              value={search.wardId ?? ''}
              onChange={(_, d) => void navigate({ search: { wardId: d.value || undefined }, replace: true })}
              aria-label="Ward"
            >
              <option value="">All wards</option>
              {(wards.data ?? []).map((w) => (
                <option key={w.id} value={w.id}>
                  {w.name}
                </option>
              ))}
            </Select>
          </Field>
        }
      />
      <TransferRequestList wardId={search.wardId ?? null} canSend={canSend} />
    </Page>
  );
}
