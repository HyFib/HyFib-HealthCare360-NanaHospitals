import {
  Button,
  Caption1,
  Checkbox,
  Field,
  Menu,
  MenuItem,
  MenuList,
  MenuPopover,
  MenuTrigger,
  SearchBox,
  Tab,
  TabList,
  Text,
  tokens,
} from '@fluentui/react-components';
import {
  AddRegular,
  LinkRegular,
  MoreHorizontalRegular,
  PersonAddRegular,
  StethoscopeRegular,
} from '@fluentui/react-icons';
import { formatDate, formatDateTime, todayIso, useDebouncedValue } from '@hc360/core';
import { DataTable, FilterBar, Page, PageHeader, Section } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useEffect, useState } from 'react';
import { crmQueries } from '@/modules/crm/api';
import { CRM_PERMISSIONS as C } from '@/modules/crm/labels';
import { LinkSpellingDialog } from '@/modules/crm/LinkSpellingDialog';
import { ReferringDoctorDrawer } from '@/modules/crm/ReferringDoctorDrawer';
import { PeriodFields } from '@/modules/feedback/PeriodFields';
import { PERIOD_PRESETS, resolvePeriod, type PeriodPreset } from '@/modules/feedback/rules';
import { isIsoDate } from '@/modules/hr/shared/dates';
import { FilterPad } from '@/modules/hr/shared/layout';
import { RequirePermission } from '@/modules/hr/shared/NoAccess';

interface ReferralsSearch {
  tab?: 'unmatched';
  preset?: PeriodPreset;
  from?: string;
  to?: string;
  search?: string;
  all?: boolean;
  page?: number;
}

export const Route = createFileRoute('/_app/crm/referrals/')({
  validateSearch: (s: Record<string, unknown>): ReferralsSearch => ({
    tab: s.tab === 'unmatched' ? 'unmatched' : undefined,
    preset: PERIOD_PRESETS.includes(s.preset as PeriodPreset) ? (s.preset as PeriodPreset) : undefined,
    from: isIsoDate(s.from) ? s.from : undefined,
    to: isIsoDate(s.to) ? s.to : undefined,
    search: typeof s.search === 'string' && s.search ? s.search : undefined,
    all: s.all === true ? true : undefined,
    page: typeof s.page === 'number' && s.page > 1 ? s.page : undefined,
  }),
  component: () => (
    <RequirePermission permission={C.referralsManage} what="referring doctors">
      <ReferralsPage />
    </RequirePermission>
  ),
});

function ReferralsPage() {
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const [term, setTerm] = useState(search.search ?? '');
  const debounced = useDebouncedValue(term.trim(), 300);
  const [drawer, setDrawer] = useState<{ doctorId: string | null; prefillName?: string } | null>(null);
  const [linking, setLinking] = useState<string | null>(null);
  const preset = search.preset ?? 'last3Months';
  const period = resolvePeriod(preset, search.from, search.to, todayIso());
  const unmatchedTab = search.tab === 'unmatched';

  const doctors = useQuery({
    ...crmQueries.referringDoctors({
      search: search.search,
      activeOnly: search.all ? undefined : true,
      from: period.from,
      to: period.to,
      page: search.page,
    }),
    enabled: !unmatchedTab,
  });
  const unmatched = useQuery({ ...crmQueries.unmatchedReferrals(period.from, period.to), enabled: unmatchedTab });

  useEffect(() => {
    if ((debounced || undefined) !== search.search) {
      void navigate({
        search: (prev) => ({ ...prev, search: debounced || undefined, page: undefined }),
        replace: true,
      });
    }
  }, [debounced, search.search, navigate]);

  const set = (patch: Partial<ReferralsSearch>) =>
    void navigate({ search: (prev) => ({ ...prev, ...patch, page: undefined }) });

  return (
    <Page>
      <PageHeader
        title="Referring doctors"
        subtitle="Who sends patients to us, counted from the “Referred by” of each visit. Link other spellings so every referral counts."
        icon={<StethoscopeRegular />}
        breadcrumbs={[{ label: 'Patient relations', onClick: () => void navigate({ to: '/crm' }) }]}
        actions={
          <Button appearance="primary" icon={<PersonAddRegular />} onClick={() => setDrawer({ doctorId: null })}>
            Add doctor
          </Button>
        }
      />
      <TabList
        selectedValue={unmatchedTab ? 'unmatched' : 'doctors'}
        onTabSelect={(_, d) => set({ tab: d.value === 'unmatched' ? 'unmatched' : undefined })}
        style={{ marginBottom: 8 }}
      >
        <Tab value="doctors">Doctors</Tab>
        <Tab value="unmatched">Names not matched</Tab>
      </TabList>
      <Section flush>
        <FilterPad>
          <FilterBar>
            <PeriodFields preset={preset} from={period.from} to={period.to} onChange={(patch) => set(patch)} />
            {!unmatchedTab && (
              <>
                <Field label="Search" style={{ flex: '1 1 220px' }}>
                  <SearchBox value={term} onChange={(_, d) => setTerm(d.value)} placeholder="Name, clinic or area" />
                </Field>
                <Checkbox
                  style={{ alignSelf: 'flex-end' }}
                  checked={Boolean(search.all)}
                  onChange={(_, d) => set({ all: d.checked ? true : undefined })}
                  label="Include inactive"
                />
              </>
            )}
          </FilterBar>
        </FilterPad>
        {unmatchedTab ? (
          <DataTable
            ariaLabel="Referred-by names that match no doctor"
            rows={unmatched.data}
            loading={unmatched.isLoading}
            error={unmatched.error}
            onRetry={() => void unmatched.refetch()}
            getRowId={(r) => r.text}
            columns={[
              { key: 'text', header: 'Written as', render: (r) => <Text weight="semibold">{r.text}</Text> },
              { key: 'visits', header: 'Visits', align: 'end', render: (r) => r.visits.toLocaleString('en-IN') },
              { key: 'last', header: 'Last visit', render: (r) => formatDateTime(r.lastVisitAt) },
            ]}
            rowActions={(r) => (
              <Menu>
                <MenuTrigger disableButtonEnhancement>
                  <Button appearance="subtle" icon={<MoreHorizontalRegular />} aria-label={`Actions for ${r.text}`} />
                </MenuTrigger>
                <MenuPopover>
                  <MenuList>
                    <MenuItem icon={<AddRegular />} onClick={() => setDrawer({ doctorId: null, prefillName: r.text })}>
                      Add as a new doctor
                    </MenuItem>
                    <MenuItem icon={<LinkRegular />} onClick={() => setLinking(r.text)}>
                      Link to an existing doctor…
                    </MenuItem>
                  </MenuList>
                </MenuPopover>
              </Menu>
            )}
            emptyTitle="Every referral is matched"
            emptyDescription="All “Referred by” names in this period match a registered doctor or one of their spellings."
          />
        ) : (
          <DataTable
            ariaLabel="Referring doctors"
            rows={doctors.data?.items}
            loading={doctors.isLoading}
            error={doctors.error}
            onRetry={() => void doctors.refetch()}
            getRowId={(d) => d.id}
            onRowClick={(d) =>
              void navigate({
                to: '/crm/referrals/$doctorId',
                params: { doctorId: d.id },
                search: { preset, from: search.from, to: search.to },
              })
            }
            columns={[
              {
                key: 'name',
                header: 'Doctor',
                render: (d) => (
                  <span>
                    <Text weight="semibold">{d.name}</Text>
                    {!d.isActive && <Caption1> · inactive</Caption1>}
                    <Caption1 block style={{ color: tokens.colorNeutralForeground3 }}>
                      {[d.specialty, d.clinic].filter(Boolean).join(' · ')}
                    </Caption1>
                  </span>
                ),
              },
              { key: 'city', header: 'Area', priority: 'low', render: (d) => d.city ?? '—' },
              {
                key: 'referrals',
                header: 'Referrals',
                align: 'end',
                render: (d) => <Text weight="semibold">{d.referrals.toLocaleString('en-IN')}</Text>,
              },
              {
                key: 'last',
                header: 'Last referral',
                priority: 'low',
                render: (d) => (d.lastReferralAt ? formatDate(d.lastReferralAt) : '—'),
              },
            ]}
            emptyTitle={search.search ? 'No doctor matches' : 'No referring doctors yet'}
            emptyDescription="Add the doctors who refer patients, with the spellings the front desk uses."
            page={search.page ?? 1}
            pageSize={25}
            totalCount={doctors.data?.totalCount}
            onPageChange={(page) =>
              void navigate({ search: (prev) => ({ ...prev, page: page > 1 ? page : undefined }) })
            }
          />
        )}
      </Section>

      {drawer && (
        <ReferringDoctorDrawer
          doctorId={drawer.doctorId}
          prefillName={drawer.prefillName}
          onClose={() => setDrawer(null)}
        />
      )}
      <LinkSpellingDialog text={linking} onClose={() => setLinking(null)} />
    </Page>
  );
}
