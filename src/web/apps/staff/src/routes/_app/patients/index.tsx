import { Badge, Button, Field, makeStyles, SearchBox, Select, Text, tokens } from '@fluentui/react-components';
import { PersonAddRegular, ShieldKeyholeRegular } from '@fluentui/react-icons';
import { formatDate, formatRelative, humanize, useDebouncedValue, type Gender } from '@hc360/core';
import { DataTable, FilterBar, Page, PageHeader, Section } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useEffect, useState } from 'react';
import { Can, usePermission } from '@/lib/permissions';
import { PATIENT_CATEGORIES, patientQueries, type PatientCategory } from '@/modules/patients/api';

interface PatientsSearch {
  q?: string;
  page?: number;
  gender?: Gender;
  category?: PatientCategory;
}

const GENDERS: Gender[] = ['Male', 'Female', 'Other', 'Unknown'];

const useStyles = makeStyles({
  // The name on one line, ending in an ellipsis where the column is narrower (the whole of it in the title), and the
  // restricted and deceased marks beside it, or on a line of their own when they do not fit.
  name: { display: 'flex', flexWrap: 'wrap', alignItems: 'center', columnGap: '6px', rowGap: '2px' },
  nameText: { minWidth: 0 },
  marks: { display: 'flex', alignItems: 'center', gap: '6px' },
});

export const Route = createFileRoute('/_app/patients/')({
  validateSearch: (search: Record<string, unknown>): PatientsSearch => ({
    q: typeof search.q === 'string' && search.q ? search.q : undefined,
    page: typeof search.page === 'number' && search.page > 1 ? search.page : undefined,
    gender: GENDERS.includes(search.gender as Gender) ? (search.gender as Gender) : undefined,
    category: PATIENT_CATEGORIES.includes(search.category as PatientCategory)
      ? (search.category as PatientCategory)
      : undefined,
  }),
  component: PatientsPage,
});

function PatientsPage() {
  const styles = useStyles();
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const canRegister = usePermission('patients.register');
  const [term, setTerm] = useState(search.q ?? '');
  const debounced = useDebouncedValue(term.trim(), 300);

  useEffect(() => {
    if ((debounced || undefined) !== search.q) {
      void navigate({ search: (prev) => ({ ...prev, q: debounced || undefined, page: undefined }), replace: true });
    }
  }, [debounced, search.q, navigate]);

  const page = search.page ?? 1;
  const patients = useQuery(
    patientQueries.search({ search: search.q, page, pageSize: 25, gender: search.gender, category: search.category }),
  );

  return (
    <Page>
      <PageHeader
        title="Patients"
        subtitle="Search by UHID, mobile number, ABHA number or name"
        actions={
          <Can permission="patients.register">
            <Button
              appearance="primary"
              icon={<PersonAddRegular />}
              onClick={() => void navigate({ to: '/patients/register' })}
            >
              Register patient
            </Button>
          </Can>
        }
      />
      <Section flush>
        <div style={{ padding: '0 16px 12px' }}>
          <FilterBar>
            <Field label="Search" style={{ flex: '1 1 320px' }}>
              <SearchBox
                value={term}
                onChange={(_, data) => setTerm(data.value)}
                placeholder="UHID, 10-digit mobile, ABHA or name"
                autoFocus
                aria-label="Search patients"
              />
            </Field>
            <Field label="Gender">
              <Select
                value={search.gender ?? ''}
                onChange={(_, data) =>
                  void navigate({
                    search: (prev) => ({
                      ...prev,
                      gender: (data.value || undefined) as Gender | undefined,
                      page: undefined,
                    }),
                  })
                }
              >
                <option value="">All</option>
                {GENDERS.map((g) => (
                  <option key={g} value={g}>
                    {g}
                  </option>
                ))}
              </Select>
            </Field>
            <Field label="Category">
              <Select
                value={search.category ?? ''}
                onChange={(_, data) =>
                  void navigate({
                    search: (prev) => ({
                      ...prev,
                      category: (data.value || undefined) as PatientCategory | undefined,
                      page: undefined,
                    }),
                  })
                }
              >
                <option value="">All</option>
                {PATIENT_CATEGORIES.map((c) => (
                  <option key={c} value={c}>
                    {humanize(c)}
                  </option>
                ))}
              </Select>
            </Field>
          </FilterBar>
        </div>
        <DataTable
          ariaLabel="Patients"
          rows={patients.data?.items}
          loading={patients.isLoading}
          error={patients.error}
          onRetry={() => void patients.refetch()}
          getRowId={(p) => p.id}
          onRowClick={(p) => void navigate({ to: '/patients/$patientId', params: { patientId: p.id } })}
          // The other columns are as wide as what they hold and the name takes the rest; with less than 160 px left
          // for it, the table scrolls sideways instead.
          columns={[
            {
              key: 'uhid',
              header: 'UHID',
              width: 120,
              render: (p) => <Text font="monospace">{p.uhid}</Text>,
            },
            {
              key: 'name',
              header: 'Name',
              minWidth: 160,
              render: (p) => (
                <span className={styles.name}>
                  <Text weight="semibold" wrap={false} truncate title={p.fullName} className={styles.nameText}>
                    {p.fullName}
                  </Text>
                  {(p.isVip || p.isDeceased) && (
                    <span className={styles.marks}>
                      {p.isVip && (
                        <ShieldKeyholeRegular
                          aria-label="Restricted record"
                          color={tokens.colorPaletteMarigoldForeground1}
                        />
                      )}
                      {p.isDeceased && (
                        <Badge size="small" color="danger">
                          Deceased
                        </Badge>
                      )}
                    </span>
                  )}
                </span>
              ),
            },
            { key: 'age', header: 'Age / Sex', width: 88, render: (p) => `${p.age ?? '—'} / ${p.gender.charAt(0)}` },
            { key: 'phone', header: 'Mobile', width: 112, render: (p) => p.phoneMasked ?? '—' },
            {
              key: 'category',
              header: 'Category',
              width: 140,
              priority: 'low',
              render: (p) =>
                p.category === 'General' ? '—' : <span title={humanize(p.category)}>{humanize(p.category)}</span>,
            },
            {
              key: 'lastVisit',
              header: 'Last visit',
              width: 112,
              priority: 'low',
              render: (p) => (p.lastVisitAt ? formatRelative(p.lastVisitAt) : 'No visits'),
            },
            {
              key: 'registered',
              header: 'Registered',
              width: 112,
              priority: 'low',
              render: (p) => formatDate(p.registeredAt),
            },
          ]}
          emptyTitle={search.q ? 'No patients match your search' : 'No patients registered yet'}
          emptyDescription={search.q ? 'Check the UHID or mobile number, or register the patient as new.' : undefined}
          emptyAction={
            canRegister ? (
              <Button
                appearance="primary"
                icon={<PersonAddRegular />}
                onClick={() => void navigate({ to: '/patients/register' })}
              >
                Register patient
              </Button>
            ) : undefined
          }
          page={page}
          pageSize={25}
          totalCount={patients.data?.totalCount}
          onPageChange={(next) => void navigate({ search: (prev) => ({ ...prev, page: next > 1 ? next : undefined }) })}
        />
      </Section>
    </Page>
  );
}
