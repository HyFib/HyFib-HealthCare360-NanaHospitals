import { Badge, Field, Select, Text } from '@fluentui/react-components';
import { useRealtimeEvent, useSession } from '@hc360/core';
import {
  DataTable,
  FilterBar,
  formatStageDuration,
  formatStageTime,
  Page,
  PageHeader,
  Pipeline,
  Section,
} from '@hc360/ui';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { toOptions, useDepartments } from '@/lib/lookups';
import { usePermission } from '@/lib/permissions';
import {
  JOURNEY_KIND_LABEL,
  journeyKeys,
  journeyQueries,
  type InHospitalFilters,
} from '@/modules/front-office/journeys';
import { inpatientQueries } from '@/modules/inpatient/api';

const KINDS = ['Outpatient', 'Inpatient'] as const;

/** Whoever may read the ward list (the server's rule for it) gets the ward filter. */
const WARD_READERS = [
  'inpatient.admissions.read',
  'beds.manage',
  'beds.housekeeping',
  'nursing.chart',
  'inpatient.masters.manage',
];

export const Route = createFileRoute('/_app/front-office/in-hospital')({
  validateSearch: (s: Record<string, unknown>): InHospitalFilters => ({
    kind: KINDS.includes(s.kind as (typeof KINDS)[number]) ? (s.kind as (typeof KINDS)[number]) : undefined,
    departmentId: typeof s.departmentId === 'string' && s.departmentId ? s.departmentId : undefined,
    wardId: typeof s.wardId === 'string' && s.wardId ? s.wardId : undefined,
    page: typeof s.page === 'number' && s.page > 1 ? s.page : undefined,
  }),
  component: InHospitalPage,
});

/**
 * Everyone in the hospital now: today's out-patient visits still open and the patients in a bed, each with the pipeline
 * of their visit or stay and the time spent in the stage they are in. Every stage comes from the records (the token,
 * the doctor's note, the lab, the pharmacy, the bill, the ward); nobody sets one. Live: refreshed every minute and when
 * a visit changes.
 */
function InHospitalPage() {
  const filters = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const queryClient = useQueryClient();
  const facilityId = useSession((s) => s.user?.facilityId);
  const departments = useDepartments('Clinical');
  const canFilterWards = usePermission(WARD_READERS);
  const wards = useQuery({ ...inpatientQueries.wards(), enabled: canFilterWards });
  const board = useQuery(journeyQueries.inHospital(filters));

  useRealtimeEvent(facilityId ? `facility:${facilityId}` : null, 'visits.changed', () => {
    void queryClient.invalidateQueries({ queryKey: journeyKeys.all });
  });

  const set = (patch: Partial<InHospitalFilters>) =>
    void navigate({ search: (prev) => ({ ...prev, ...patch, page: undefined }) });

  return (
    <Page>
      <PageHeader
        title="Patients in hospital"
        subtitle="Today's open out-patient visits and the patients in a bed, with where each is in their journey"
      />
      <Section flush>
        <div style={{ padding: '0 16px 12px' }}>
          <FilterBar>
            <Field label="Show">
              <Select
                value={filters.kind ?? ''}
                onChange={(_, d) =>
                  set({
                    kind: (d.value || undefined) as InHospitalFilters['kind'],
                    wardId: d.value === 'Outpatient' ? undefined : filters.wardId,
                  })
                }
              >
                <option value="">Out-patients and in-patients</option>
                <option value="Outpatient">Out-patients</option>
                <option value="Inpatient">In-patients</option>
              </Select>
            </Field>
            <Field label="Department">
              <Select
                value={filters.departmentId ?? ''}
                onChange={(_, d) => set({ departmentId: d.value || undefined })}
              >
                <option value="">All</option>
                {toOptions(departments.data).map((o) => (
                  <option key={o.value} value={o.value}>
                    {o.label}
                  </option>
                ))}
              </Select>
            </Field>
            {canFilterWards && filters.kind !== 'Outpatient' && (
              <Field label="Ward">
                <Select value={filters.wardId ?? ''} onChange={(_, d) => set({ wardId: d.value || undefined })}>
                  <option value="">All</option>
                  {(wards.data ?? []).map((ward) => (
                    <option key={ward.id} value={ward.id}>
                      {ward.name}
                    </option>
                  ))}
                </Select>
              </Field>
            )}
          </FilterBar>
        </div>
        <DataTable
          ariaLabel="Patients in hospital"
          rows={board.data?.items}
          loading={board.isLoading}
          error={board.error}
          onRetry={() => void board.refetch()}
          getRowId={(row) => row.journey.id}
          onRowClick={(row) => void navigate({ to: '/patients/$patientId', params: { patientId: row.patientId } })}
          columns={[
            {
              key: 'patient',
              header: 'Patient',
              minWidth: 120,
              render: (row) => (
                <span
                  style={{ display: 'flex', flexDirection: 'column', whiteSpace: 'normal', overflowWrap: 'anywhere' }}
                >
                  <span>
                    <Text weight="semibold">{row.patientName}</Text>{' '}
                    <Text size={200}>
                      ({row.age ?? '—'} / {row.gender.charAt(0)})
                    </Text>
                    {row.isRestricted && (
                      <Badge size="small" appearance="outline" style={{ marginLeft: 6 }}>
                        Restricted
                      </Badge>
                    )}
                  </span>
                  <Text size={200}>
                    {row.uhid}
                    {row.place ? ` · ${row.place}` : ''}
                  </Text>
                </span>
              ),
            },
            {
              key: 'visit',
              header: 'Visit or stay',
              priority: 'low',
              render: (row) => (
                <span style={{ display: 'flex', flexDirection: 'column', whiteSpace: 'normal' }}>
                  <Text size={200}>
                    {JOURNEY_KIND_LABEL[row.kind]} {row.journey.number}
                  </Text>
                  <Text size={200}>{row.departmentName ?? '—'}</Text>
                </span>
              ),
            },
            {
              key: 'arrived',
              header: 'Arrived',
              width: 110,
              priority: 'low',
              render: (row) => formatStageTime(row.arrivedAt),
            },
            {
              key: 'stage',
              header: 'Where they are',
              minWidth: 140,
              render: (row) => (
                <div style={{ whiteSpace: 'normal' }}>
                  <Pipeline
                    stages={row.journey.stages}
                    label={`${JOURNEY_KIND_LABEL[row.kind]} ${row.journey.number}`}
                    compact
                  />
                </div>
              ),
            },
            {
              // The compact pipeline writes the time out too: on a phone this column gives the patient the room.
              key: 'time',
              header: 'In this stage',
              width: 100,
              align: 'end',
              priority: 'low',
              render: (row) =>
                row.journey.current?.minutesInStage == null
                  ? '—'
                  : formatStageDuration(row.journey.current.minutesInStage),
            },
          ]}
          emptyTitle="Nobody in the hospital matches"
          emptyDescription="Patients appear here as soon as they are checked in or admitted."
          page={filters.page ?? 1}
          pageSize={25}
          totalCount={board.data?.totalCount}
          onPageChange={(page) => void navigate({ search: (prev) => ({ ...prev, page: page > 1 ? page : undefined }) })}
          size="small"
        />
      </Section>
    </Page>
  );
}
