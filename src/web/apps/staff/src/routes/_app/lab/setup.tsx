import { Badge, Button, Caption1, Field, makeStyles, SearchBox, Select, Switch, Tab, TabList, Text, tokens } from '@fluentui/react-components';
import { AddRegular } from '@fluentui/react-icons';
import { formatInr, useDebouncedValue } from '@hc360/core';
import { DataTable, FilterBar, Page, PageHeader, Section, StatusBadge } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useEffect, useState } from 'react';
import { LAB_SECTIONS, labQueries, type LabInstrumentDto, type LabSection, type LabTestListItem, type ReferralLabDto } from '@/modules/lab/api';
import { TubeChip } from '@/modules/lab/badges';
import { searchFlag, searchId, searchOneOf, searchPage, searchText } from '@/modules/lab/common/search';
import { resultTypeLabel, sectionLabel } from '@/modules/lab/labels';
import { InstrumentDrawer } from '@/modules/lab/setup/InstrumentDrawer';
import { ReferralLabDrawer } from '@/modules/lab/setup/ReferralLabDrawer';
import { TestDrawer } from '@/modules/lab/setup/TestDrawer';

const TABS = ['tests', 'analysers', 'referral-labs'] as const;
type SetupTab = (typeof TABS)[number];

interface SetupSearch {
  tab?: SetupTab;
  testId?: string;
  search?: string;
  section?: LabSection;
  inactive?: true;
  page?: number;
}

export const Route = createFileRoute('/_app/lab/setup')({
  validateSearch: (s: Record<string, unknown>): SetupSearch => ({
    tab: searchOneOf(s.tab, TABS),
    testId: searchId(s.testId),
    search: searchText(s.search, 60),
    section: searchOneOf(s.section, LAB_SECTIONS),
    inactive: searchFlag(s.inactive),
    page: searchPage(s.page),
  }),
  component: LabSetupPage,
});

const useStyles = makeStyles({
  muted: { color: tokens.colorNeutralForeground3 },
  cell: { display: 'flex', flexDirection: 'column', gap: '2px', minWidth: 0 },
  row: { display: 'flex', gap: '6px', alignItems: 'center', flexWrap: 'wrap' },
});

/** Laboratory masters (SRS LIS-01, LIS-08): tests with ranges and panels, analysers, reference laboratories. */
function LabSetupPage() {
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const tab = search.tab ?? 'tests';
  const [creating, setCreating] = useState<SetupTab | null>(null);

  return (
    <Page>
      <PageHeader
        title="Lab setup"
        subtitle="Tests, reference ranges, panels, analysers and reference laboratories"
        actions={
          <Button appearance="primary" icon={<AddRegular />} onClick={() => setCreating(tab)}>
            {tab === 'tests' ? 'New test' : tab === 'analysers' ? 'Add analyser' : 'Add reference laboratory'}
          </Button>
        }
      />
      <TabList selectedValue={tab} onTabSelect={(_, data) => void navigate({ search: { tab: data.value === 'tests' ? undefined : (data.value as SetupTab) } })}>
        <Tab value="tests">Tests</Tab>
        <Tab value="analysers">Analysers</Tab>
        <Tab value="referral-labs">Reference laboratories</Tab>
      </TabList>

      {tab === 'tests' && <TestsTab search={search} />}
      {tab === 'analysers' && <AnalysersTab includeInactive={Boolean(search.inactive)} creating={creating === 'analysers'} onCreated={() => setCreating(null)} />}
      {tab === 'referral-labs' && <ReferralLabsTab includeInactive={Boolean(search.inactive)} creating={creating === 'referral-labs'} onCreated={() => setCreating(null)} />}

      {creating === 'tests' && (
        <TestDrawer
          key="new-test"
          testId={null}
          onClose={() => setCreating(null)}
          onCreated={(id) => {
            setCreating(null);
            void navigate({ search: (prev) => ({ ...prev, testId: id }) });
          }}
        />
      )}
      {search.testId && <TestDrawer key={search.testId} testId={search.testId} onClose={() => void navigate({ search: (prev) => ({ ...prev, testId: undefined }) })} />}
    </Page>
  );
}

function TestsTab({ search }: { search: SetupSearch }) {
  const styles = useStyles();
  const navigate = useNavigate({ from: Route.fullPath });
  const [term, setTerm] = useState(search.search ?? '');
  const debounced = useDebouncedValue(term.trim(), 300);

  useEffect(() => {
    if ((debounced || undefined) !== search.search) {
      void navigate({ search: (prev) => ({ ...prev, search: debounced || undefined, page: undefined }), replace: true });
    }
  }, [debounced, search.search, navigate]);

  const tests = useQuery(labQueries.tests({ search: search.search, section: search.section, includeInactive: search.inactive, page: search.page }));
  const set = (patch: Partial<SetupSearch>) => void navigate({ search: (prev) => ({ ...prev, ...patch, page: undefined }) });

  return (
    <Section flush>
      <div style={{ padding: '0 16px 12px' }}>
        <FilterBar>
          <Field label="Search" style={{ flex: '1 1 260px' }}>
            <SearchBox value={term} onChange={(_, data) => setTerm(data.value)} placeholder="Test name or code" autoFocus />
          </Field>
          <Field label="Bench">
            <Select value={search.section ?? ''} onChange={(_, data) => set({ section: (data.value || undefined) as LabSection | undefined })}>
              <option value="">All</option>
              {LAB_SECTIONS.map((value) => (
                <option key={value} value={value}>
                  {sectionLabel[value]}
                </option>
              ))}
            </Select>
          </Field>
          <Switch checked={Boolean(search.inactive)} onChange={(_, data) => set({ inactive: data.checked ? true : undefined })} label="Show tests not in use" />
        </FilterBar>
      </div>
      <DataTable
        ariaLabel="Lab tests"
        rows={tests.data?.items}
        loading={tests.isLoading}
        error={tests.error}
        onRetry={() => void tests.refetch()}
        getRowId={(test) => test.id}
        onRowClick={(test) => void navigate({ search: (prev) => ({ ...prev, testId: test.id }) })}
        size="small"
        columns={[
          {
            key: 'test',
            header: 'Test',
            render: (test: LabTestListItem) => (
              <span className={styles.cell}>
                <span className={styles.row}>
                  <Text weight="semibold">{test.name}</Text>
                  {test.isProvisional && (
                    <Badge color="warning" appearance="tint" shape="rounded">
                      Provisional
                    </Badge>
                  )}
                  {!test.isActive && (
                    <Badge appearance="outline" shape="rounded">
                      Not in use
                    </Badge>
                  )}
                </span>
                <Caption1 className={styles.muted}>
                  {test.code}
                  {test.isPanel ? ` · panel of ${test.componentCount}` : ` · ${resultTypeLabel[test.resultType]}${test.unit ? ` (${test.unit})` : ''}`}
                </Caption1>
              </span>
            ),
          },
          { key: 'section', header: 'Bench', render: (test: LabTestListItem) => sectionLabel[test.section] },
          { key: 'tube', header: 'Tube', priority: 'low', render: (test: LabTestListItem) => <TubeChip container={test.container} /> },
          { key: 'tat', header: 'Target', width: 80, priority: 'low', render: (test: LabTestListItem) => `${test.tatHours} h` },
          {
            key: 'price',
            header: 'Price',
            width: 100,
            align: 'end',
            render: (test: LabTestListItem) => (test.isOrderable ? (test.price !== null ? formatInr(test.price, { whole: true }) : 'Not linked') : <Caption1 className={styles.muted}>Analyte</Caption1>),
          },
        ]}
        emptyTitle={search.search ? 'No test matches' : 'No tests yet'}
        emptyDescription="Add tests with New test. The seeded catalogue covers the common tests of an Indian hospital lab."
        page={search.page ?? 1}
        pageSize={25}
        totalCount={tests.data?.totalCount}
        onPageChange={(page) => void navigate({ search: (prev) => ({ ...prev, page: page > 1 ? page : undefined }) })}
      />
    </Section>
  );
}

function InactiveSwitch({ includeInactive }: { includeInactive: boolean }) {
  const navigate = useNavigate({ from: Route.fullPath });
  return (
    <Switch
      checked={includeInactive}
      onChange={(_, data) => void navigate({ search: (prev) => ({ ...prev, inactive: data.checked ? true : undefined }) })}
      label="Show those not in use"
    />
  );
}

function AnalysersTab({ includeInactive, creating, onCreated }: { includeInactive: boolean; creating: boolean; onCreated: () => void }) {
  const styles = useStyles();
  const instruments = useQuery(labQueries.instruments(includeInactive));
  const [editing, setEditing] = useState<LabInstrumentDto | null>(null);

  return (
    <Section title="Analysers" description="Connected through the equipment gateway; results arrive on the worklist by barcode" actions={<InactiveSwitch includeInactive={includeInactive} />} flush>
      <DataTable
        ariaLabel="Analysers"
        rows={instruments.data}
        loading={instruments.isLoading}
        error={instruments.error}
        onRetry={() => void instruments.refetch()}
        getRowId={(instrument) => instrument.id}
        onRowClick={setEditing}
        size="small"
        columns={[
          {
            key: 'name',
            header: 'Analyser',
            render: (instrument: LabInstrumentDto) => (
              <span className={styles.cell}>
                <Text weight="semibold">{instrument.name}</Text>
                <Caption1 className={styles.muted}>
                  {instrument.code}
                  {instrument.manufacturer ? ` · ${instrument.manufacturer}` : ''}
                  {instrument.model ? ` ${instrument.model}` : ''}
                </Caption1>
              </span>
            ),
          },
          { key: 'section', header: 'Bench', render: (instrument: LabInstrumentDto) => sectionLabel[instrument.section] },
          {
            key: 'interface',
            header: 'Interface',
            priority: 'low',
            render: (instrument: LabInstrumentDto) => `${instrument.protocol === 'Hl7' ? 'HL7' : 'ASTM'}${instrument.isBidirectional ? ', bidirectional' : ''}`,
          },
          { key: 'auto', header: 'Auto-verify', priority: 'low', render: (instrument: LabInstrumentDto) => (instrument.autoValidationEnabled ? 'On' : 'Off') },
          { key: 'status', header: 'Status', width: 110, render: (instrument: LabInstrumentDto) => <StatusBadge status={instrument.isActive ? 'Active' : 'Inactive'} label={instrument.isActive ? 'In use' : 'Not in use'} /> },
        ]}
        emptyTitle="No analysers"
        emptyDescription="Add each analyser with the code configured in the equipment gateway."
      />
      {(editing || creating) && (
        <InstrumentDrawer
          key={editing?.id ?? 'new'}
          instrument={editing}
          onClose={() => {
            setEditing(null);
            onCreated();
          }}
        />
      )}
    </Section>
  );
}

function ReferralLabsTab({ includeInactive, creating, onCreated }: { includeInactive: boolean; creating: boolean; onCreated: () => void }) {
  const styles = useStyles();
  const labs = useQuery(labQueries.referralLabs(includeInactive));
  const [editing, setEditing] = useState<ReferralLabDto | null>(null);

  return (
    <Section title="Reference laboratories" description="Where outsourced tests are sent; the agreed turnaround sets each due date" actions={<InactiveSwitch includeInactive={includeInactive} />} flush>
      <DataTable
        ariaLabel="Reference laboratories"
        rows={labs.data}
        loading={labs.isLoading}
        error={labs.error}
        onRetry={() => void labs.refetch()}
        getRowId={(lab) => lab.id}
        onRowClick={setEditing}
        size="small"
        columns={[
          {
            key: 'name',
            header: 'Laboratory',
            render: (lab: ReferralLabDto) => (
              <span className={styles.cell}>
                <Text weight="semibold">{lab.name}</Text>
                <Caption1 className={styles.muted}>
                  {lab.code}
                  {lab.accreditationNumber ? ` · NABL ${lab.accreditationNumber}` : ''}
                </Caption1>
              </span>
            ),
          },
          { key: 'contact', header: 'Contact', priority: 'low', render: (lab: ReferralLabDto) => [lab.contactPerson, lab.phone].filter(Boolean).join(' · ') || '—' },
          { key: 'tat', header: 'Turnaround', width: 110, render: (lab: ReferralLabDto) => `${lab.tatDays} ${lab.tatDays === 1 ? 'day' : 'days'}` },
          { key: 'status', header: 'Status', width: 110, render: (lab: ReferralLabDto) => <StatusBadge status={lab.isActive ? 'Active' : 'Inactive'} label={lab.isActive ? 'In use' : 'Not in use'} /> },
        ]}
        emptyTitle="No reference laboratories"
        emptyDescription="Add the laboratories you send samples to."
      />
      {(editing || creating) && (
        <ReferralLabDrawer
          key={editing?.id ?? 'new'}
          lab={editing}
          onClose={() => {
            setEditing(null);
            onCreated();
          }}
        />
      )}
    </Section>
  );
}
