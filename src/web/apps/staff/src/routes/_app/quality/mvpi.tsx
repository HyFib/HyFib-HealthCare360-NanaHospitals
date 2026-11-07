import { Button } from '@fluentui/react-components';
import { AddRegular, ShieldErrorRegular } from '@fluentui/react-icons';
import { Page, PageHeader, Section } from '@hc360/ui';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { isIsoDate } from '@/modules/hr/shared/dates';
import { RequirePermission } from '@/modules/hr/shared/NoAccess';
import { QUALITY_PERMISSIONS as Q } from '@/modules/quality/labels';
import type { MvpiFilters, MvpiReportStatus } from '@/modules/quality/mvpi';
import { MvpiRaiseDialog, MvpiRegister, MvpiReportDialog } from '@/modules/quality/MvpiReports';

interface MvpiSearch extends MvpiFilters {
  /** Raise a report from this incident (the incident's "Report to MvPI"). */
  incidentId?: string;
  /** The report to open. */
  report?: string;
}

const STATES: MvpiReportStatus[] = ['Draft', 'Submitted'];
const isId = (value: unknown): value is string => typeof value === 'string' && /^[0-9a-f-]{36}$/i.test(value);

export const Route = createFileRoute('/_app/quality/mvpi')({
  validateSearch: (s: Record<string, unknown>): MvpiSearch => ({
    status: STATES.includes(s.status as MvpiReportStatus) ? (s.status as MvpiReportStatus) : undefined,
    from: isIsoDate(s.from) ? s.from : undefined,
    to: isIsoDate(s.to) ? s.to : undefined,
    incidentId: isId(s.incidentId) ? s.incidentId : undefined,
    report: isId(s.report) ? s.report : undefined,
  }),
  component: () => (
    <RequirePermission permission={Q.incidentsManage} what="device adverse event reports">
      <MvpiPage />
    </RequirePermission>
  ),
});

/**
 * Adverse events with medical devices, reported to the Materiovigilance Programme of India (MvPI) on its MDAE form: raised
 * from an incident of the register or for a patient, kept as drafts until MvPI's questions are answered, and recorded as
 * sent with MvPI's reference.
 */
function MvpiPage() {
  const navigate = useNavigate({ from: Route.fullPath });
  const search = Route.useSearch();
  const [raising, setRaising] = useState(false);
  const { incidentId, report, ...filters } = search;
  const set = (change: Partial<MvpiSearch>) => void navigate({ search: { ...search, ...change }, replace: true });

  return (
    <Page>
      <PageHeader
        title="Device adverse events (MvPI)"
        subtitle="Adverse events with medical devices, reported to the Materiovigilance Programme of India on its MDAE form. Raise one from an equipment failure, or from an incident that involved a device."
        icon={<ShieldErrorRegular />}
        breadcrumbs={[
          { label: 'Incidents', onClick: () => void navigate({ to: '/quality/incidents' }) },
          { label: 'MvPI' },
        ]}
        actions={
          <Button appearance="primary" icon={<AddRegular />} onClick={() => setRaising(true)}>
            Report a device adverse event
          </Button>
        }
      />
      <Section flush>
        <MvpiRegister
          filters={filters}
          onFiltersChange={(next) => set({ status: next.status, from: next.from, to: next.to })}
          onOpen={(id) => set({ report: id })}
        />
      </Section>
      {(raising || incidentId) && (
        <MvpiRaiseDialog
          incidentId={incidentId ?? null}
          onClose={() => {
            setRaising(false);
            set({ incidentId: undefined });
          }}
          onSaved={(saved) => {
            setRaising(false);
            set({ incidentId: undefined, report: saved.id });
          }}
        />
      )}
      {report && (
        <MvpiReportDialog
          key={report}
          id={report}
          onClose={() => set({ report: undefined })}
          onOpen={(id) => set({ report: id })}
        />
      )}
    </Page>
  );
}
