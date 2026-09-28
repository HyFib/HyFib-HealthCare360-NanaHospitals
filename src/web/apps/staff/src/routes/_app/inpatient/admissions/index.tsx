import { Badge, Button, Caption1, Field, Input, SearchBox, Select, Switch, Text, tokens } from '@fluentui/react-components';
import { PeopleRegular, PersonAddRegular } from '@fluentui/react-icons';
import { formatDateTime, humanize, useDebouncedValue } from '@hc360/core';
import { DataTable, FilterBar, Page, PageHeader, Section, StatusBadge } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useEffect, useState } from 'react';
import { toOptions, usePractitioners } from '@/lib/lookups';
import {
  ADMISSION_STATUS_LABEL,
  ADMISSION_STATUSES,
  inpatientQueries,
  PAYER_LABEL,
  type AdmissionFilters,
  type AdmissionStatus,
} from '@/modules/inpatient/api';
import { useInpatientPermissions, useInpatientRealtime } from '@/modules/inpatient/hooks';

const DATE = /^\d{4}-\d{2}-\d{2}$/;

interface AdmissionsSearch {
  q?: string;
  status?: AdmissionStatus;
  /** "all" shows closed stays too; the default is patients currently in hospital. */
  scope?: 'all';
  wardId?: string;
  consultantId?: string;
  from?: string;
  to?: string;
  page?: number;
}

export const Route = createFileRoute('/_app/inpatient/admissions/')({
  validateSearch: (s: Record<string, unknown>): AdmissionsSearch => ({
    q: typeof s.q === 'string' && s.q ? s.q : undefined,
    status: ADMISSION_STATUSES.includes(s.status as AdmissionStatus) ? (s.status as AdmissionStatus) : undefined,
    scope: s.scope === 'all' ? 'all' : undefined,
    wardId: typeof s.wardId === 'string' && s.wardId ? s.wardId : undefined,
    consultantId: typeof s.consultantId === 'string' && s.consultantId ? s.consultantId : undefined,
    from: typeof s.from === 'string' && DATE.test(s.from) ? s.from : undefined,
    to: typeof s.to === 'string' && DATE.test(s.to) ? s.to : undefined,
    page: typeof s.page === 'number' && s.page > 1 ? s.page : undefined,
  }),
  component: AdmissionsPage,
});

/** Admissions list: who is in hospital now (default) or every stay, with search by UHID, name, mobile or IP number. */
function AdmissionsPage() {
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const permissions = useInpatientPermissions();
  useInpatientRealtime();
  const [term, setTerm] = useState(search.q ?? '');
  const debounced = useDebouncedValue(term.trim(), 300);
  const wards = useQuery(inpatientQueries.wards());
  const doctors = usePractitioners(null);

  useEffect(() => {
    if ((debounced || undefined) !== search.q) {
      void navigate({ search: (prev) => ({ ...prev, q: debounced || undefined, page: undefined }), replace: true });
    }
  }, [debounced, search.q, navigate]);

  const inHospitalOnly = search.scope !== 'all' && !search.status;
  const filters: AdmissionFilters = {
    search: search.q,
    status: search.status,
    activeOnly: inHospitalOnly || undefined,
    wardId: search.wardId,
    consultantId: search.consultantId,
    from: search.from,
    to: search.to,
    page: search.page,
  };
  const admissions = useQuery(inpatientQueries.admissions(filters));
  const set = (patch: Partial<AdmissionsSearch>) => void navigate({ search: (prev) => ({ ...prev, ...patch, page: undefined }) });
  const filtered = Boolean(search.q || search.status || search.wardId || search.consultantId || search.from || search.to);

  return (
    <Page>
      <PageHeader
        title="Admissions"
        icon={<PeopleRegular />}
        subtitle={inHospitalOnly ? 'Patients in hospital now, newest first' : 'All admissions, newest first'}
        actions={
          permissions.admit && (
            <Button appearance="primary" icon={<PersonAddRegular />} onClick={() => void navigate({ to: '/inpatient/admit' })}>
              Admit patient
            </Button>
          )
        }
      />
      <Section flush>
        <div style={{ padding: '0 16px 12px' }}>
          <FilterBar>
            <Field label="Search" style={{ flex: '1 1 260px' }}>
              <SearchBox value={term} onChange={(_, d) => setTerm(d.value)} placeholder="UHID, IP number, name or mobile" autoFocus aria-label="Search admissions" />
            </Field>
            <Field label="Ward">
              <Select value={search.wardId ?? ''} onChange={(_, d) => set({ wardId: d.value || undefined })}>
                <option value="">All wards</option>
                {wards.data?.map((w) => (
                  <option key={w.id} value={w.id}>
                    {w.name}
                  </option>
                ))}
              </Select>
            </Field>
            <Field label="Consultant">
              <Select value={search.consultantId ?? ''} onChange={(_, d) => set({ consultantId: d.value || undefined })}>
                <option value="">All doctors</option>
                {toOptions(doctors.data).map((o) => (
                  <option key={o.value} value={o.value}>
                    {o.label}
                  </option>
                ))}
              </Select>
            </Field>
            <Field label="Status">
              <Select value={search.status ?? ''} onChange={(_, d) => set({ status: (d.value || undefined) as AdmissionStatus | undefined })}>
                <option value="">Any</option>
                {ADMISSION_STATUSES.map((s) => (
                  <option key={s} value={s}>
                    {ADMISSION_STATUS_LABEL[s]}
                  </option>
                ))}
              </Select>
            </Field>
            <Field label="Admitted from">
              <Input type="date" value={search.from ?? ''} onChange={(_, d) => set({ from: d.value || undefined })} />
            </Field>
            <Field label="to">
              <Input type="date" value={search.to ?? ''} min={search.from} onChange={(_, d) => set({ to: d.value || undefined })} />
            </Field>
            <Switch
              checked={!inHospitalOnly}
              disabled={Boolean(search.status)}
              onChange={(_, d) => set({ scope: d.checked ? 'all' : undefined })}
              label="Include discharged"
            />
          </FilterBar>
        </div>
        <DataTable
          ariaLabel="Admissions"
          rows={admissions.data?.items}
          loading={admissions.isLoading}
          error={admissions.error}
          onRetry={() => void admissions.refetch()}
          getRowId={(a) => a.id}
          onRowClick={(a) => void navigate({ to: '/inpatient/admissions/$admissionId', params: { admissionId: a.id } })}
          columns={[
            { key: 'number', header: 'IP number', width: 150, render: (a) => <Text font="monospace">{a.admissionNumber}</Text> },
            {
              key: 'patient',
              header: 'Patient',
              render: (a) => (
                <span>
                  <Text weight="semibold">{a.patientName}</Text>{' '}
                  <Caption1>
                    ({a.age ?? '—'} / {a.gender.charAt(0)}) · {a.uhid}
                  </Caption1>
                  {a.isMlc && (
                    <Badge size="small" color="danger" style={{ marginLeft: 6 }}>
                      MLC
                    </Badge>
                  )}
                </span>
              ),
            },
            {
              key: 'bed',
              header: 'Ward · bed',
              render: (a) => (a.wardName ? `${a.wardName}${a.bedLabel ? ` · ${a.bedLabel}` : ''}` : '—'),
            },
            { key: 'doctor', header: 'Consultant', priority: 'low', render: (a) => a.consultantName ?? '—' },
            { key: 'admitted', header: 'Admitted', width: 170, priority: 'low', render: (a) => formatDateTime(a.admittedAt) },
            {
              key: 'los',
              header: 'Day',
              width: 70,
              align: 'end',
              render: (a) => <Text style={a.lengthOfStayDays > 10 ? { color: tokens.colorPaletteMarigoldForeground1, fontWeight: 600 } : undefined}>{a.lengthOfStayDays}</Text>,
            },
            { key: 'payer', header: 'Payment', priority: 'low', render: (a) => PAYER_LABEL[a.payerType] ?? humanize(a.payerType) },
            {
              key: 'status',
              header: 'Status',
              width: 170,
              render: (a) => (
                <StatusBadge
                  status={a.status}
                  label={ADMISSION_STATUS_LABEL[a.status]}
                  tone={a.status === 'Admitted' ? 'brand' : a.status === 'DischargeInitiated' ? 'warning' : a.status === 'Discharged' ? 'success' : a.status === 'Cancelled' ? 'informative' : 'danger'}
                />
              ),
            },
          ]}
          emptyTitle={filtered ? 'No admissions match' : inHospitalOnly ? 'No patients in hospital' : 'No admissions yet'}
          emptyDescription={filtered ? 'Check the UHID or IP number, or clear the filters.' : permissions.admit ? 'Admit a patient to see them here.' : undefined}
          page={search.page ?? 1}
          pageSize={25}
          totalCount={admissions.data?.totalCount}
          onPageChange={(page) => void navigate({ search: (prev) => ({ ...prev, page: page > 1 ? page : undefined }) })}
          size="small"
        />
      </Section>
    </Page>
  );
}
