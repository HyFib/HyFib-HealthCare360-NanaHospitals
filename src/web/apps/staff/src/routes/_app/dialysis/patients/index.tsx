import {
  Button,
  Caption1,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Field,
  SearchBox,
  Select,
  Switch,
  Text,
  tokens,
} from '@fluentui/react-components';
import { PersonAddRegular } from '@fluentui/react-icons';
import { useDebouncedValue } from '@hc360/core';
import { DataTable, FilterBar, Page, PageHeader, Section } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useEffect, useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { dialysisQueries, SERO_STATUSES, type ProfileFilters, type SeroStatus } from '@/modules/dialysis/api';
import { SeroBadge } from '@/modules/dialysis/DialysisBadges';
import { accessLabel, seroLabel, shiftLabel, weekdayShort } from '@/modules/dialysis/labels';
import { NoAccess } from '@/modules/ot/shared/feedback';
import { PatientPicker, type PickedPatient } from '@/modules/patients/PatientPicker';

export const Route = createFileRoute('/_app/dialysis/patients/')({
  validateSearch: (s: Record<string, unknown>): ProfileFilters => ({
    sero: SERO_STATUSES.includes(s.sero as SeroStatus) ? (s.sero as SeroStatus) : undefined,
    search: typeof s.search === 'string' && s.search ? s.search : undefined,
    includeInactive: s.includeInactive === true ? true : undefined,
    page: typeof s.page === 'number' && s.page > 1 ? s.page : undefined,
  }),
  component: DialysisPatientsPage,
});

function DialysisPatientsPage() {
  const filters = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const canRead = usePermission('dialysis.read');
  const canEnrol = usePermission('dialysis.patients.manage');
  const [term, setTerm] = useState(filters.search ?? '');
  const debounced = useDebouncedValue(term.trim(), 300);
  const [enrolling, setEnrolling] = useState(false);
  const [picked, setPicked] = useState<PickedPatient | null>(null);
  const profiles = useQuery({ ...dialysisQueries.profiles(filters), enabled: canRead });

  useEffect(() => {
    if ((debounced || undefined) !== filters.search) {
      void navigate({
        search: (prev) => ({ ...prev, search: debounced || undefined, page: undefined }),
        replace: true,
      });
    }
  }, [debounced, filters.search, navigate]);

  if (!canRead) {
    return (
      <Page>
        <NoAccess what="dialysis patients" />
      </Page>
    );
  }

  const set = (patch: Partial<ProfileFilters>) =>
    void navigate({ search: (prev) => ({ ...prev, ...patch, page: undefined }) });

  return (
    <Page>
      <PageHeader
        title="Dialysis patients"
        subtitle="Patients enrolled in the unit, with their sero status, weekly schedule and access."
        breadcrumbs={[
          { label: 'Dialysis board', onClick: () => void navigate({ to: '/dialysis' }) },
          { label: 'Patients' },
        ]}
        actions={
          canEnrol && (
            <Button appearance="primary" icon={<PersonAddRegular />} onClick={() => setEnrolling(true)}>
              Enrol patient
            </Button>
          )
        }
      />
      <Section flush>
        <div style={{ padding: '0 16px 12px' }}>
          <FilterBar>
            <Field label="Search" style={{ flex: '1 1 260px' }}>
              <SearchBox value={term} onChange={(_, d) => setTerm(d.value)} placeholder="Name, UHID or mobile" />
            </Field>
            <Field label="Sero status">
              <Select
                value={filters.sero ?? ''}
                onChange={(_, d) => set({ sero: (d.value || undefined) as SeroStatus | undefined })}
              >
                <option value="">All</option>
                {SERO_STATUSES.map((s) => (
                  <option key={s} value={s}>
                    {seroLabel[s]}
                  </option>
                ))}
              </Select>
            </Field>
            <Switch
              checked={Boolean(filters.includeInactive)}
              onChange={(_, d) => set({ includeInactive: d.checked || undefined })}
              label="Include inactive"
            />
          </FilterBar>
        </div>
        <DataTable
          ariaLabel="Dialysis patients"
          rows={profiles.data?.items}
          loading={profiles.isLoading}
          error={profiles.error}
          onRetry={() => void profiles.refetch()}
          getRowId={(p) => p.id}
          onRowClick={(p) => void navigate({ to: '/dialysis/patients/$patientId', params: { patientId: p.patientId } })}
          columns={[
            {
              key: 'patient',
              header: 'Patient',
              render: (p) => (
                <span>
                  <Text weight="semibold">{p.patientName}</Text>{' '}
                  <Text size={200}>
                    ({p.age ?? '—'} / {p.gender.charAt(0)})
                  </Text>
                  {!p.isActive && (
                    <Caption1 block style={{ color: tokens.colorNeutralForeground3 }}>
                      Inactive
                    </Caption1>
                  )}
                </span>
              ),
            },
            { key: 'uhid', header: 'UHID', priority: 'low', render: (p) => <Text font="monospace">{p.uhid}</Text> },
            {
              key: 'sero',
              header: 'Sero status',
              render: (p) => (
                <span>
                  <SeroBadge sero={p.seroStatus} />
                  {p.seroRescreeningDue && (
                    <Caption1 block style={{ color: tokens.colorPaletteDarkOrangeForeground1 }}>
                      Re-screening due
                    </Caption1>
                  )}
                </span>
              ),
            },
            {
              key: 'schedule',
              header: 'Schedule',
              render: (p) => `${p.scheduleDays.map((d) => weekdayShort[d]).join(', ')} · ${shiftLabel[p.shift]}`,
            },
            { key: 'access', header: 'Access', priority: 'low', render: (p) => accessLabel[p.vascularAccess] },
            { key: 'dry', header: 'Dry weight', align: 'end', priority: 'low', render: (p) => `${p.dryWeightKg} kg` },
            {
              key: 'reuse',
              header: 'Dialyzer',
              priority: 'low',
              render: (p) =>
                p.effectiveMaxDialyzerUses === 1 ? 'Single use' : `Up to ${p.effectiveMaxDialyzerUses} uses`,
            },
            { key: 'neph', header: 'Nephrologist', priority: 'low', render: (p) => p.nephrologistName ?? '—' },
          ]}
          emptyTitle={filters.search || filters.sero ? 'No enrolled patient matches' : 'No patients enrolled yet'}
          emptyDescription={canEnrol ? 'Enrol a patient with their dry weight, access and sero status.' : undefined}
          page={filters.page ?? 1}
          pageSize={25}
          totalCount={profiles.data?.totalCount}
          onPageChange={(page) => void navigate({ search: (prev) => ({ ...prev, page: page > 1 ? page : undefined }) })}
          size="small"
        />
      </Section>

      <Dialog open={enrolling} onOpenChange={(_, d) => !d.open && setEnrolling(false)}>
        <DialogSurface style={{ maxWidth: 560 }}>
          <DialogBody>
            <DialogTitle>Enrol a patient in the dialysis unit</DialogTitle>
            <DialogContent>
              <PatientPicker
                value={picked}
                onChange={setPicked}
                required
                hint="Register the patient first if they are new to the hospital."
              />
            </DialogContent>
            <DialogActions>
              <Button onClick={() => setEnrolling(false)}>Cancel</Button>
              <Button
                appearance="primary"
                disabled={!picked}
                onClick={() =>
                  picked && void navigate({ to: '/dialysis/patients/$patientId', params: { patientId: picked.id } })
                }
              >
                Continue
              </Button>
            </DialogActions>
          </DialogBody>
        </DialogSurface>
      </Dialog>
    </Page>
  );
}
