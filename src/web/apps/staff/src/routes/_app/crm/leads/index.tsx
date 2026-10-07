import {
  Badge,
  Button,
  Caption1,
  Card,
  Checkbox,
  Field,
  makeStyles,
  Menu,
  MenuItem,
  MenuList,
  MenuPopover,
  MenuTrigger,
  mergeClasses,
  MessageBar,
  MessageBarBody,
  SearchBox,
  Select,
  Spinner,
  Tab,
  TabList,
  Text,
  tokens,
  useRestoreFocusTarget,
} from '@fluentui/react-components';
import {
  ArrowMoveRegular,
  BoardRegular,
  ClockAlarmRegular,
  PersonAddRegular,
  TableRegular,
  TargetArrowRegular,
} from '@fluentui/react-icons';
import { formatDateTime, formatRelative, useDebouncedValue } from '@hc360/core';
import { DataTable, ErrorState, FilterBar, Page, PageHeader, Section, StatusBadge, useNotify } from '@hc360/ui';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useEffect, useState, type DragEvent } from 'react';
import { useDepartments } from '@/lib/lookups';
import { crmApi, crmKeys, crmQueries, type LeadListItem } from '@/modules/crm/api';
import {
  CRM_PERMISSIONS as C,
  LEAD_SOURCES,
  LEAD_STATUSES,
  leadSourceLabel,
  leadStatusLabel,
  type LeadSource,
  type LeadStatus,
} from '@/modules/crm/labels';
import { LeadDrawer } from '@/modules/crm/LeadDrawer';
import { LEAD_BOARD_COLUMNS, leadMoves, leadTone } from '@/modules/crm/rules';
import { FilterPad } from '@/modules/hr/shared/layout';
import { RequirePermission } from '@/modules/hr/shared/NoAccess';
import { ReasonDialog } from '@/modules/hr/shared/ReasonDialog';
import { UserName } from '@/modules/hr/shared/UserPicker';

interface LeadsSearch {
  view?: 'list';
  status?: LeadStatus;
  all?: boolean;
  source?: LeadSource;
  mine?: boolean;
  dueOnly?: boolean;
  search?: string;
  page?: number;
}

export const Route = createFileRoute('/_app/crm/leads/')({
  validateSearch: (s: Record<string, unknown>): LeadsSearch => ({
    view: s.view === 'list' ? 'list' : undefined,
    status: LEAD_STATUSES.includes(s.status as LeadStatus) ? (s.status as LeadStatus) : undefined,
    all: s.all === true ? true : undefined,
    source: LEAD_SOURCES.includes(s.source as LeadSource) ? (s.source as LeadSource) : undefined,
    mine: s.mine === true ? true : undefined,
    dueOnly: s.dueOnly === true ? true : undefined,
    search: typeof s.search === 'string' && s.search ? s.search : undefined,
    page: typeof s.page === 'number' && s.page > 1 ? s.page : undefined,
  }),
  component: () => (
    <RequirePermission permission={C.leadsManage} what="leads">
      <LeadsPage />
    </RequirePermission>
  ),
});

/** The board shows at most this many open leads (the server's page limit); the list pages through everything. */
const BOARD_LIMIT = 200;

const useStyles = makeStyles({
  board: {
    display: 'grid',
    gridTemplateColumns: 'repeat(3, minmax(240px, 1fr))',
    gap: '12px',
    overflowX: 'auto',
    paddingBottom: '8px',
  },
  column: {
    display: 'flex',
    flexDirection: 'column',
    gap: '8px',
    minHeight: '240px',
    padding: '10px',
    borderRadius: tokens.borderRadiusLarge,
    backgroundColor: tokens.colorNeutralBackground2,
    border: `2px dashed transparent`,
  },
  dropTarget: { border: `2px dashed ${tokens.colorBrandStroke1}`, backgroundColor: tokens.colorBrandBackground2 },
  columnHead: { display: 'flex', alignItems: 'center', justifyContent: 'space-between', padding: '0 4px 4px' },
  cards: {
    display: 'flex',
    flexDirection: 'column',
    gap: '8px',
    maxHeight: '68vh',
    overflowY: 'auto',
    paddingRight: '2px',
  },
  card: { cursor: 'grab', padding: '10px 12px' },
  cardTop: { display: 'flex', justifyContent: 'space-between', gap: '6px', alignItems: 'flex-start' },
  muted: { color: tokens.colorNeutralForeground3 },
  meta: { display: 'flex', gap: '6px', flexWrap: 'wrap', alignItems: 'center', marginTop: '6px' },
});

function LeadsPage() {
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [term, setTerm] = useState(search.search ?? '');
  const debounced = useDebouncedValue(term.trim(), 300);
  const [adding, setAdding] = useState(false);
  const [losing, setLosing] = useState<LeadListItem | null>(null);
  const [busy, setBusy] = useState(false);
  const board = search.view !== 'list';

  useEffect(() => {
    if ((debounced || undefined) !== search.search) {
      void navigate({
        search: (prev) => ({ ...prev, search: debounced || undefined, page: undefined }),
        replace: true,
      });
    }
  }, [debounced, search.search, navigate]);

  const set = (patch: Partial<LeadsSearch>) =>
    void navigate({ search: (prev) => ({ ...prev, ...patch, page: undefined }) });

  const move = async (lead: LeadListItem, to: LeadStatus, lostReason: string | null = null) => {
    setBusy(true);
    try {
      await crmApi.changeLeadStatus(lead.id, to, lostReason);
      notify.success(`${lead.name} moved to ${leadStatusLabel[to]}`);
      setLosing(null);
      void queryClient.invalidateQueries({ queryKey: crmKeys.leads });
    } catch (error) {
      notify.error(error, 'Could not move the lead');
      void queryClient.invalidateQueries({ queryKey: crmKeys.leads });
    } finally {
      setBusy(false);
    }
  };

  const request = (lead: LeadListItem, to: LeadStatus) => (to === 'Lost' ? setLosing(lead) : void move(lead, to));

  return (
    <Page>
      <PageHeader
        title="Leads"
        subtitle="Enquiries from the website, calls, camps and referrals. Call them back, qualify them and convert them into patients and appointments."
        icon={<TargetArrowRegular />}
        breadcrumbs={[{ label: 'Patient relations', onClick: () => void navigate({ to: '/crm' }) }]}
        actions={
          <Button appearance="primary" icon={<PersonAddRegular />} onClick={() => setAdding(true)}>
            New lead
          </Button>
        }
      />
      <TabList
        selectedValue={board ? 'board' : 'list'}
        onTabSelect={(_, d) =>
          set({ view: d.value === 'list' ? 'list' : undefined, status: undefined, all: undefined })
        }
      >
        <Tab value="board" icon={<BoardRegular />}>
          Pipeline
        </Tab>
        <Tab value="list" icon={<TableRegular />}>
          List
        </Tab>
      </TabList>
      <Section flush>
        <FilterPad>
          <FilterBar>
            <Field label="Search" style={{ flex: '1 1 220px' }}>
              <SearchBox
                value={term}
                onChange={(_, d) => setTerm(d.value)}
                placeholder="Name, lead number or last 4+ digits of the mobile"
              />
            </Field>
            {!board && (
              <Field label="Stage">
                <Select
                  value={search.status ?? (search.all ? 'all' : '')}
                  onChange={(_, d) =>
                    set(
                      d.value === ''
                        ? { status: undefined, all: undefined }
                        : d.value === 'all'
                          ? { status: undefined, all: true }
                          : { status: d.value as LeadStatus, all: undefined },
                    )
                  }
                >
                  <option value="">All open</option>
                  {LEAD_STATUSES.map((s) => (
                    <option key={s} value={s}>
                      {leadStatusLabel[s]}
                    </option>
                  ))}
                  <option value="all">Everything</option>
                </Select>
              </Field>
            )}
            <Field label="Source">
              <Select
                value={search.source ?? ''}
                onChange={(_, d) => set({ source: (d.value || undefined) as LeadSource | undefined })}
              >
                <option value="">All</option>
                {LEAD_SOURCES.map((s) => (
                  <option key={s} value={s}>
                    {leadSourceLabel[s]}
                  </option>
                ))}
              </Select>
            </Field>
            <Checkbox
              style={{ alignSelf: 'flex-end' }}
              checked={Boolean(search.mine)}
              onChange={(_, d) => set({ mine: d.checked ? true : undefined })}
              label="Mine"
            />
            <Checkbox
              style={{ alignSelf: 'flex-end' }}
              checked={Boolean(search.dueOnly)}
              onChange={(_, d) => set({ dueOnly: d.checked ? true : undefined })}
              label="Call-back due"
            />
          </FilterBar>
        </FilterPad>
        {board ? <Board search={search} busy={busy} onMove={request} /> : <List search={search} />}
      </Section>

      {adding && <LeadDrawer lead={null} onClose={() => setAdding(false)} />}
      <ReasonDialog
        open={losing !== null}
        title={`Mark ${losing?.name ?? 'the lead'} as lost`}
        description="The reason helps improve campaigns and follow-up. A lost lead can be reopened later."
        label="Why was it lost?"
        hint="For example: chose another hospital, too expensive, not reachable after 3 calls"
        maxLength={300}
        confirmLabel="Mark as lost"
        destructive
        busy={busy}
        onConfirm={(reason) => losing && void move(losing, 'Lost', reason)}
        onCancel={() => setLosing(null)}
      />
    </Page>
  );
}

function Board({
  search,
  busy,
  onMove,
}: {
  search: LeadsSearch;
  busy: boolean;
  onMove: (lead: LeadListItem, to: LeadStatus) => void;
}) {
  const styles = useStyles();
  // Closing the dialog opened from a card's menu returns focus to that card's menu button.
  const restoreFocusTarget = useRestoreFocusTarget();
  const navigate = useNavigate();
  const departments = useDepartments('Clinical');
  const [dragging, setDragging] = useState<LeadListItem | null>(null);
  const [over, setOver] = useState<LeadStatus | null>(null);
  const leads = useQuery(
    crmQueries.leads({
      source: search.source,
      mine: search.mine,
      dueOnly: search.dueOnly,
      search: search.search,
      pageSize: BOARD_LIMIT,
    }),
  );

  if (leads.error) {
    return <ErrorState error={leads.error} onRetry={() => void leads.refetch()} />;
  }
  if (!leads.data) {
    return <Spinner label="Loading the pipeline…" style={{ padding: 24 }} />;
  }

  const items = leads.data.items;
  const departmentName = new Map((departments.data ?? []).map((d) => [d.id, d.name]));
  const drop = (event: DragEvent, to: LeadStatus) => {
    event.preventDefault();
    setOver(null);
    if (dragging && dragging.status !== to && !busy) {
      onMove(dragging, to);
    }
    setDragging(null);
  };

  return (
    <div style={{ padding: '0 16px 16px' }}>
      {leads.data.totalCount > items.length && (
        <MessageBar intent="info" style={{ marginBottom: 12 }}>
          <MessageBarBody>
            Showing the first {items.length} of {leads.data.totalCount} open leads. Use the filters or the list to see
            the rest.
          </MessageBarBody>
        </MessageBar>
      )}
      <div className={styles.board}>
        {LEAD_BOARD_COLUMNS.map((status) => {
          const column = items.filter((l) => l.status === status);
          const canDrop = dragging !== null && dragging.status !== status;
          return (
            <section
              key={status}
              aria-label={`${leadStatusLabel[status]}: ${column.length} leads`}
              className={mergeClasses(styles.column, canDrop && over === status && styles.dropTarget)}
              onDragOver={(event) => {
                if (canDrop) {
                  event.preventDefault();
                  setOver(status);
                }
              }}
              onDragLeave={() => setOver((current) => (current === status ? null : current))}
              onDrop={(event) => drop(event, status)}
            >
              <div className={styles.columnHead}>
                <Text weight="semibold">{leadStatusLabel[status]}</Text>
                <Badge appearance="tint" color={leadTone(status)}>
                  {column.length}
                </Badge>
              </div>
              <div className={styles.cards}>
                {column.length === 0 && <Caption1 className={styles.muted}>No leads here.</Caption1>}
                {column.map((lead) => (
                  <Card
                    key={lead.id}
                    className={styles.card}
                    draggable={!busy}
                    onDragStart={(event: DragEvent) => {
                      event.dataTransfer.effectAllowed = 'move';
                      event.dataTransfer.setData('text/plain', lead.id);
                      setDragging(lead);
                    }}
                    onDragEnd={() => {
                      setDragging(null);
                      setOver(null);
                    }}
                    onClick={() => void navigate({ to: '/crm/leads/$leadId', params: { leadId: lead.id } })}
                    appearance="filled-alternative"
                    aria-label={`${lead.name}, ${leadStatusLabel[lead.status]}`}
                  >
                    <div className={styles.cardTop}>
                      <span>
                        <Text weight="semibold">{lead.name}</Text>
                        <Caption1 block className={styles.muted}>
                          {lead.leadNumber} · {leadSourceLabel[lead.source]}
                        </Caption1>
                      </span>
                      <Menu>
                        <MenuTrigger disableButtonEnhancement>
                          <Button
                            size="small"
                            appearance="subtle"
                            icon={<ArrowMoveRegular />}
                            aria-label={`Move ${lead.name}`}
                            disabled={busy}
                            onClick={(event) => event.stopPropagation()}
                            {...restoreFocusTarget}
                          />
                        </MenuTrigger>
                        <MenuPopover onClick={(event) => event.stopPropagation()}>
                          <MenuList>
                            {leadMoves(lead.status).map((to) => (
                              <MenuItem key={to} onClick={() => onMove(lead, to)}>
                                {to === 'Lost' ? 'Mark as lost…' : `Move to ${leadStatusLabel[to]}`}
                              </MenuItem>
                            ))}
                          </MenuList>
                        </MenuPopover>
                      </Menu>
                    </div>
                    {(lead.interestPackage || lead.interestDepartmentId) && (
                      <Caption1 block>
                        {lead.interestPackage ??
                          departmentName.get(lead.interestDepartmentId ?? '') ??
                          'Department enquiry'}
                      </Caption1>
                    )}
                    <div className={styles.meta}>
                      {lead.isFollowUpDue ? (
                        <Badge appearance="tint" color="warning" icon={<ClockAlarmRegular />}>
                          Call back now
                        </Badge>
                      ) : lead.nextFollowUpAt ? (
                        <Caption1 className={styles.muted}>Next call {formatRelative(lead.nextFollowUpAt)}</Caption1>
                      ) : (
                        <Caption1 className={styles.muted}>Added {formatRelative(lead.createdAt)}</Caption1>
                      )}
                      {lead.ownerUserId && (
                        <Caption1 className={styles.muted}>
                          · <UserName id={lead.ownerUserId} />
                        </Caption1>
                      )}
                    </div>
                  </Card>
                ))}
              </div>
            </section>
          );
        })}
      </div>
      <Caption1 className={styles.muted}>
        Drag a card to another column, or use its move button. Converted and lost leads are in the list.
      </Caption1>
    </div>
  );
}

function List({ search }: { search: LeadsSearch }) {
  const navigate = useNavigate({ from: Route.fullPath });
  const departments = useDepartments('Clinical');
  const leads = useQuery(
    crmQueries.leads({
      status: search.status,
      includeClosed: search.all,
      source: search.source,
      mine: search.mine,
      dueOnly: search.dueOnly,
      search: search.search,
      page: search.page,
    }),
  );
  const departmentName = new Map((departments.data ?? []).map((d) => [d.id, d.name]));

  return (
    <DataTable
      ariaLabel="Leads"
      rows={leads.data?.items}
      loading={leads.isLoading}
      error={leads.error}
      onRetry={() => void leads.refetch()}
      getRowId={(l) => l.id}
      onRowClick={(l) => void navigate({ to: '/crm/leads/$leadId', params: { leadId: l.id } })}
      columns={[
        {
          key: 'name',
          header: 'Lead',
          render: (l) => (
            <span>
              <Text weight="semibold">{l.name}</Text>
              <Caption1 block style={{ color: tokens.colorNeutralForeground3 }}>
                {l.leadNumber}
                {l.phoneMasked ? ` · ${l.phoneMasked}` : ''}
              </Caption1>
            </span>
          ),
        },
        { key: 'source', header: 'Source', render: (l) => leadSourceLabel[l.source] },
        {
          key: 'interest',
          header: 'Interested in',
          priority: 'low',
          render: (l) =>
            l.interestPackage ?? (l.interestDepartmentId ? (departmentName.get(l.interestDepartmentId) ?? '—') : '—'),
        },
        {
          key: 'status',
          header: 'Stage',
          render: (l) => <StatusBadge status={l.status} tone={leadTone(l.status)} label={leadStatusLabel[l.status]} />,
        },
        {
          key: 'next',
          header: 'Next call',
          render: (l) =>
            l.nextFollowUpAt ? (
              <Text
                style={{ color: l.isFollowUpDue ? tokens.colorPaletteDarkOrangeForeground1 : undefined }}
                weight={l.isFollowUpDue ? 'semibold' : 'regular'}
              >
                {formatDateTime(l.nextFollowUpAt)}
              </Text>
            ) : (
              '—'
            ),
        },
        {
          key: 'owner',
          header: 'Owner',
          priority: 'low',
          render: (l) => (l.ownerUserId ? <UserName id={l.ownerUserId} /> : 'Not assigned'),
        },
        { key: 'created', header: 'Added', priority: 'low', render: (l) => formatRelative(l.createdAt) },
      ]}
      emptyTitle="No leads match"
      emptyDescription="Clear a filter, or add the enquiry as a new lead."
      page={search.page ?? 1}
      pageSize={25}
      totalCount={leads.data?.totalCount}
      onPageChange={(page) => void navigate({ search: (prev) => ({ ...prev, page: page > 1 ? page : undefined }) })}
    />
  );
}
