import { Button, Caption1, Field, SearchBox, Tab, TabList, Text, tokens } from '@fluentui/react-components';
import { AddRegular, CheckmarkCircleRegular, ShieldErrorRegular } from '@fluentui/react-icons';
import { formatDateTime, useDebouncedValue } from '@hc360/core';
import { DataTable, FilterBar, Page, PageHeader, Section } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useEffect, useState } from 'react';
import { FilterPad } from '@/modules/hr/shared/layout';
import { RequirePermission } from '@/modules/hr/shared/NoAccess';
import { BREACH_PAGE_SIZE, privacyQueries, type BreachListItem } from '@/modules/privacy/api';
import { BreachStatusBadge, DeadlineWords } from '@/modules/privacy/badges';
import { PRIVACY_PERMISSIONS as P } from '@/modules/privacy/labels';
import { breachListClock, breachPeopleText, shortDate } from '@/modules/privacy/rules';

interface BreachesSearch {
  view?: 'all';
  search?: string;
  page?: number;
}

export const Route = createFileRoute('/_app/privacy/breaches/')({
  validateSearch: (s: Record<string, unknown>): BreachesSearch => ({
    view: s.view === 'all' ? 'all' : undefined,
    search: typeof s.search === 'string' && s.search ? s.search : undefined,
    page: typeof s.page === 'number' && s.page > 1 ? s.page : undefined,
  }),
  component: () => (
    <RequirePermission permission={P.breachesManage} what="the breach register">
      <BreachesPage />
    </RequirePermission>
  ),
});

function ReportClock({ b, now }: { b: BreachListItem; now: number }) {
  const clock = breachListClock(b, now);
  if (clock.state === 'sent') {
    return (
      <span style={{ display: 'inline-flex', alignItems: 'center', gap: 4 }}>
        <CheckmarkCircleRegular style={{ color: tokens.colorPaletteGreenForeground1 }} aria-hidden />
        <Text>{clock.text}</Text>
      </span>
    );
  }
  if (clock.state === 'closed') {
    return <Caption1 style={{ color: tokens.colorNeutralForeground3 }}>{clock.text}</Caption1>;
  }
  return (
    <span>
      <DeadlineWords text={clock.text} overdue={clock.state === 'overdue'} warn={clock.state === 'urgent'} />
      <Caption1 block style={{ color: tokens.colorNeutralForeground3 }}>
        by {formatDateTime(b.reportDueAt)}
        {b.boardInformedAt ? ` · Board told ${shortDate(Date.parse(b.boardInformedAt), now)}` : ''}
      </Caption1>
    </span>
  );
}

/** The personal data breach register (DPDP Act s8(6)): open breaches first, with the Board's 72-hour clock. */
function BreachesPage() {
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const [term, setTerm] = useState(search.search ?? '');
  const debounced = useDebouncedValue(term.trim(), 300);
  const view = search.view ?? 'open';
  const breaches = useQuery(privacyQueries.breaches({ view, search: search.search, page: search.page }));
  // The clock is worked out against the last refresh (every minute), so it stays stable while reading.
  const now = breaches.dataUpdatedAt || 0;

  useEffect(() => {
    if ((debounced || undefined) !== search.search) {
      void navigate({
        search: (prev) => ({ ...prev, search: debounced || undefined, page: undefined }),
        replace: true,
      });
    }
  }, [debounced, search.search, navigate]);

  return (
    <Page>
      <PageHeader
        title="Data breaches"
        subtitle="Record every personal data breach as soon as you learn of it. Tell the Data Protection Board without delay, send the detailed report within 72 hours, and tell each person affected."
        icon={<ShieldErrorRegular />}
        actions={
          <Button
            appearance="primary"
            icon={<AddRegular />}
            onClick={() => void navigate({ to: '/privacy/breaches/new' })}
          >
            Record a breach
          </Button>
        }
      />
      <TabList
        selectedValue={view}
        onTabSelect={(_, d) =>
          void navigate({
            search: (prev) => ({ ...prev, view: d.value === 'all' ? 'all' : undefined, page: undefined }),
          })
        }
      >
        <Tab value="open">Open</Tab>
        <Tab value="all">All</Tab>
      </TabList>
      <Section flush>
        <FilterPad>
          <FilterBar>
            <Field label="Search" style={{ flex: '1 1 260px' }}>
              <SearchBox value={term} onChange={(_, d) => setTerm(d.value)} placeholder="Breach number or title" />
            </Field>
          </FilterBar>
        </FilterPad>
        <DataTable
          ariaLabel="Data breaches"
          rows={breaches.data?.items}
          loading={breaches.isLoading}
          error={breaches.error}
          onRetry={() => void breaches.refetch()}
          getRowId={(b) => b.id}
          onRowClick={(b) => void navigate({ to: '/privacy/breaches/$breachId', params: { breachId: b.id } })}
          columns={[
            {
              key: 'number',
              header: 'Number',
              width: 120,
              render: (b) => <Text font="monospace">{b.breachNumber}</Text>,
            },
            { key: 'title', header: 'Title', render: (b) => <Text weight="semibold">{b.title}</Text> },
            {
              key: 'aware',
              header: 'Became aware',
              width: 170,
              priority: 'low',
              render: (b) => formatDateTime(b.awareAt),
            },
            { key: 'clock', header: 'Report to the Board', render: (b) => <ReportClock b={b} now={now} /> },
            {
              key: 'certIn',
              header: 'CERT-In (6 hours)',
              priority: 'low',
              render: (b) =>
                !b.certInType ? (
                  '—'
                ) : b.certInReportedAt ? (
                  `Told ${shortDate(Date.parse(b.certInReportedAt), now)}`
                ) : b.certInReportOverdue ? (
                  <Text weight="semibold" style={{ color: tokens.colorPaletteRedForeground1 }}>
                    Overdue
                  </Text>
                ) : (
                  `Due ${shortDate(Date.parse(b.certInReportDueAt!), now)}`
                ),
            },
            {
              key: 'status',
              header: 'Status',
              render: (b) => <BreachStatusBadge status={b.status} reportOverdue={b.reportOverdue} />,
            },
            {
              key: 'people',
              header: 'People',
              priority: 'low',
              render: (b) => breachPeopleText(b),
            },
          ]}
          emptyTitle={view === 'open' ? 'No open breaches' : 'No breaches recorded'}
          emptyDescription={
            search.search
              ? 'Nothing matches the search. Try the breach number or another word from the title.'
              : 'Record a breach as soon as you learn of it: the 72-hour clock for the Board’s report starts then.'
          }
          emptyAction={
            <Button
              appearance="primary"
              icon={<AddRegular />}
              onClick={() => void navigate({ to: '/privacy/breaches/new' })}
            >
              Record a breach
            </Button>
          }
          page={search.page ?? 1}
          pageSize={BREACH_PAGE_SIZE}
          totalCount={breaches.data?.totalCount}
          onPageChange={(page) => void navigate({ search: (prev) => ({ ...prev, page: page > 1 ? page : undefined }) })}
        />
      </Section>
    </Page>
  );
}
