import { Button, Field, Select } from '@fluentui/react-components';
import { AddRegular, BugRegular } from '@fluentui/react-icons';
import { Page, PageHeader, Section } from '@hc360/ui';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { RequirePermission } from '@/modules/hr/shared/NoAccess';
import {
  HAI_DEVICES,
  HAI_MANAGE,
  STATUS_LABELS,
  type DeviceEpisode,
  type HaiCaseStatus,
} from '@/modules/quality/haiApi';
import {
  DevicesTable,
  HaiCaseDialog,
  HaiCasesTable,
  HaiRatesTable,
  InsertDeviceDialog,
  RemoveDeviceDialog,
} from '@/modules/quality/HaiSurveillance';

export const Route = createFileRoute('/_app/quality/hai')({
  validateSearch: (s: Record<string, unknown>): { case?: string; status?: HaiCaseStatus } => ({
    case: typeof s.case === 'string' && s.case ? s.case : undefined,
    status: s.status === 'Suspected' || s.status === 'Confirmed' || s.status === 'RuledOut' ? s.status : undefined,
  }),
  component: HaiPage,
});

/**
 * Hospital-acquired infection surveillance (NABH HIC): the cases to review against the NHSN definitions and the monthly
 * rates for the infection control team, and the devices in place, which ward and ICU nurses record.
 */
function HaiPage() {
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const manage = usePermission(HAI_MANAGE);
  const devices = usePermission(HAI_DEVICES);
  const [inserting, setInserting] = useState(false);
  const [removing, setRemoving] = useState<DeviceEpisode | null>(null);
  const status = search.status ?? (search.case ? undefined : 'Suspected');

  return (
    <Page>
      <PageHeader
        title="Infection surveillance"
        subtitle="CLABSI, CAUTI, VAP and surgical site infections against the NHSN definitions, with device days"
        icon={<BugRegular />}
        actions={
          (manage || devices) && (
            <Button appearance="primary" icon={<AddRegular />} onClick={() => setInserting(true)}>
              Record a device
            </Button>
          )
        }
      />
      <RequirePermission permission={[HAI_MANAGE, HAI_DEVICES]} what="infection surveillance">
        {manage && (
          <>
            <Section
              flush
              title="Cases"
              actions={
                <Field label="Show" orientation="horizontal">
                  <Select
                    value={search.status ?? 'Suspected'}
                    onChange={(_, d) =>
                      void navigate({
                        search: (prev) => ({
                          ...prev,
                          status: d.value === 'all' ? undefined : (d.value as HaiCaseStatus),
                        }),
                        replace: true,
                      })
                    }
                  >
                    {(['Suspected', 'Confirmed', 'RuledOut'] as const).map((s) => (
                      <option key={s} value={s}>
                        {STATUS_LABELS[s]}
                      </option>
                    ))}
                    <option value="all">All</option>
                  </Select>
                </Field>
              }
            >
              <HaiCasesTable
                status={status}
                onOpen={(id) => void navigate({ search: (prev) => ({ ...prev, case: id }), replace: true })}
              />
            </Section>
            <Section flush title="Rates, last 12 months">
              <HaiRatesTable />
            </Section>
          </>
        )}
        <Section flush title="Devices in place">
          <DevicesTable inPlace onRemove={setRemoving} />
        </Section>
        {inserting && <InsertDeviceDialog onClose={() => setInserting(false)} />}
        {removing && <RemoveDeviceDialog device={removing} onClose={() => setRemoving(null)} />}
        {manage && search.case && (
          <HaiCaseDialog
            caseId={search.case}
            onClose={() => void navigate({ search: (prev) => ({ ...prev, case: undefined }), replace: true })}
          />
        )}
      </RequirePermission>
    </Page>
  );
}
