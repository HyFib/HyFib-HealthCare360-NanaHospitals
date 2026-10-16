import {
  Badge,
  Button,
  Caption1,
  Field,
  makeStyles,
  SearchBox,
  Select,
  Switch,
  Text,
  tokens,
} from '@fluentui/react-components';
import { AddRegular, GlobeRegular, VideoRegular } from '@fluentui/react-icons';
import { formatInr, useDebouncedValue } from '@hc360/core';
import { DataTable, FilterBar, Page, PageHeader, Section } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useEffect, useState } from 'react';
import { useDepartments } from '@/lib/lookups';
import { RequirePermission } from '@/modules/hr/shared/NoAccess';
import { searchFlag, searchId, searchOneOf, searchPage, searchText } from '@/modules/lab/common/search';
import { MASTERS_MANAGE, MASTERS_PAGE_SIZE, mastersQueries, type PractitionerKind } from '@/modules/masters/api';
import { PRACTITIONER_KIND_LABELS, PRACTITIONER_KINDS } from '@/modules/masters/labels';
import { InUseBadge, ListToolbar } from '@/modules/masters/parts';
import { PractitionerEditorDrawer } from '@/modules/masters/PractitionerEditor';

interface PractitionersSearch {
  q?: string;
  kind?: PractitionerKind;
  departmentId?: string;
  specialtyId?: string;
  inactive?: true;
  page?: number;
  edit?: string;
}

export const Route = createFileRoute('/_app/setup/practitioners')({
  validateSearch: (s: Record<string, unknown>): PractitionersSearch => ({
    q: searchText(s.q),
    kind: searchOneOf(s.kind, PRACTITIONER_KINDS),
    departmentId: searchId(s.departmentId),
    specialtyId: searchId(s.specialtyId),
    inactive: searchFlag(s.inactive),
    page: searchPage(s.page),
    edit: s.edit === 'new' ? 'new' : searchId(s.edit),
  }),
  component: () => (
    <RequirePermission permission={MASTERS_MANAGE} what="the doctors and clinical staff">
      <PractitionersPage />
    </RequirePermission>
  ),
});

const useStyles = makeStyles({
  stack: { display: 'flex', flexDirection: 'column', minWidth: 0 },
  muted: { color: tokens.colorNeutralForeground3 },
  badges: { display: 'inline-flex', gap: '4px', flexWrap: 'wrap' },
});

/** Doctors and other clinicians: who can be booked, what they charge, and the signature their documents carry. */
function PractitionersPage() {
  const styles = useStyles();
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const [term, setTerm] = useState(search.q ?? '');
  const debounced = useDebouncedValue(term.trim(), 300);
  const departments = useDepartments();
  const specialties = useQuery(mastersQueries.specialties());
  const practitioners = useQuery(
    mastersQueries.practitioners({
      search: search.q,
      kind: search.kind,
      departmentId: search.departmentId,
      specialtyId: search.specialtyId,
      includeInactive: search.inactive,
      page: search.page,
    }),
  );
  const set = (patch: Partial<PractitionersSearch>) =>
    void navigate({ search: (prev) => ({ ...prev, ...patch, page: undefined }) });

  useEffect(() => {
    if ((debounced || undefined) !== search.q) {
      void navigate({ search: (prev) => ({ ...prev, q: debounced || undefined, page: undefined }), replace: true });
    }
  }, [debounced, search.q, navigate]);

  const filtered = Boolean(search.q || search.kind || search.departmentId || search.specialtyId);

  return (
    <Page>
      <PageHeader
        title="Doctors and clinical staff"
        subtitle="Who can be booked and chosen for visits, their fees, and the registration and signature on their documents."
        actions={
          <Button
            appearance="primary"
            icon={<AddRegular />}
            onClick={() => void navigate({ search: (prev) => ({ ...prev, edit: 'new' }) })}
          >
            Add doctor or clinician
          </Button>
        }
      />
      <Section flush>
        <ListToolbar>
          <FilterBar>
            <Field label="Search" style={{ flex: '1 1 240px' }}>
              <SearchBox
                value={term}
                onChange={(_, data) => setTerm(data.value)}
                placeholder="Name, code or registration number"
              />
            </Field>
            <Field label="Role">
              <Select
                value={search.kind ?? ''}
                onChange={(_, d) => set({ kind: (d.value || undefined) as PractitionerKind | undefined })}
              >
                <option value="">All roles</option>
                {PRACTITIONER_KINDS.map((kind) => (
                  <option key={kind} value={kind}>
                    {PRACTITIONER_KIND_LABELS[kind]}
                  </option>
                ))}
              </Select>
            </Field>
            <Field label="Department">
              <Select
                value={search.departmentId ?? ''}
                onChange={(_, d) => set({ departmentId: d.value || undefined })}
              >
                <option value="">All departments</option>
                {(departments.data ?? []).map((d) => (
                  <option key={d.id} value={d.id}>
                    {d.name}
                  </option>
                ))}
              </Select>
            </Field>
            <Field label="Speciality">
              <Select value={search.specialtyId ?? ''} onChange={(_, d) => set({ specialtyId: d.value || undefined })}>
                <option value="">All specialities</option>
                {(specialties.data ?? [])
                  .filter((s) => s.isActive)
                  .map((s) => (
                    <option key={s.id} value={s.id}>
                      {s.name}
                    </option>
                  ))}
              </Select>
            </Field>
            <Switch
              checked={search.inactive === true}
              onChange={(_, d) => set({ inactive: d.checked || undefined })}
              label="Show those not in use"
            />
          </FilterBar>
        </ListToolbar>
        <DataTable
          ariaLabel="Doctors and clinical staff"
          rows={practitioners.data?.items}
          loading={practitioners.isLoading}
          error={practitioners.error}
          onRetry={() => void practitioners.refetch()}
          getRowId={(p) => p.id}
          onRowClick={(p) => void navigate({ search: (prev) => ({ ...prev, edit: p.id }) })}
          columns={[
            {
              key: 'name',
              header: 'Name',
              render: (p) => (
                <span className={styles.stack}>
                  <Text weight="semibold">{p.displayName}</Text>
                  {p.qualification && <Caption1 className={styles.muted}>{p.qualification}</Caption1>}
                </span>
              ),
            },
            { key: 'code', header: 'Code', width: 100, render: (p) => <Text font="monospace">{p.code}</Text> },
            { key: 'role', header: 'Role', width: 160, render: (p) => PRACTITIONER_KIND_LABELS[p.kind] },
            {
              key: 'where',
              header: 'Department',
              priority: 'low',
              render: (p) => (
                <span className={styles.stack}>
                  <span>{p.departmentName ?? '—'}</span>
                  {p.specialtyName && <Caption1 className={styles.muted}>{p.specialtyName}</Caption1>}
                </span>
              ),
            },
            {
              key: 'fees',
              header: 'Fee (new / follow-up)',
              width: 170,
              priority: 'low',
              render: (p) =>
                `${formatInr(p.consultationFeeNew, { whole: true })} / ${formatInr(p.consultationFeeFollowUp, { whole: true })}`,
            },
            {
              key: 'booking',
              header: 'Booking',
              width: 150,
              priority: 'low',
              render: (p) =>
                p.availableForOnlineBooking || p.availableForTelehealth ? (
                  <span className={styles.badges}>
                    {p.availableForOnlineBooking && (
                      <Badge size="small" appearance="tint" color="brand" icon={<GlobeRegular />}>
                        Online
                      </Badge>
                    )}
                    {p.availableForTelehealth && (
                      <Badge size="small" appearance="tint" color="informative" icon={<VideoRegular />}>
                        Video
                      </Badge>
                    )}
                  </span>
                ) : (
                  '—'
                ),
            },
            { key: 'status', header: 'Status', width: 120, render: (p) => <InUseBadge isActive={p.isActive} /> },
          ]}
          emptyTitle={filtered ? 'No one matches' : 'No doctors or clinicians yet'}
          emptyDescription={
            filtered
              ? 'Clear the search or the filters.'
              : 'Add the doctors who consult here, with their fees and council registration.'
          }
          emptyAction={
            <Button
              appearance="primary"
              icon={<AddRegular />}
              onClick={() => void navigate({ search: (prev) => ({ ...prev, edit: 'new' }) })}
            >
              Add doctor or clinician
            </Button>
          }
          page={search.page ?? 1}
          pageSize={MASTERS_PAGE_SIZE}
          totalCount={practitioners.data?.totalCount}
          onPageChange={(page) => void navigate({ search: (prev) => ({ ...prev, page: page > 1 ? page : undefined }) })}
        />
      </Section>
      {search.edit && (
        <PractitionerEditorDrawer
          key={search.edit}
          practitionerId={search.edit === 'new' ? null : search.edit}
          onClose={() => void navigate({ search: (prev) => ({ ...prev, edit: undefined }) })}
        />
      )}
    </Page>
  );
}
