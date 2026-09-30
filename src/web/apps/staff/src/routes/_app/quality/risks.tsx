import {
  Button,
  Caption1,
  Checkbox,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Field,
  SearchBox,
  Select,
  Tab,
  TabList,
  Text,
  Tooltip,
} from '@fluentui/react-components';
import { AddRegular, ArrowSyncRegular, DismissRegular, GridRegular } from '@fluentui/react-icons';
import { formatDateOnly, humanize, useDebouncedValue } from '@hc360/core';
import { DataTable, ErrorState, FilterBar, LoadingState, Page, PageHeader, Section, StatusBadge } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useEffect, useState } from 'react';
import { toOptions, useDepartments } from '@/lib/lookups';
import { FilterPad } from '@/modules/hr/shared/layout';
import { RequirePermission } from '@/modules/hr/shared/NoAccess';
import { UserName } from '@/modules/hr/shared/UserPicker';
import { qualityQueries } from '@/modules/quality/api';
import { RatingBadge } from '@/modules/quality/badges';
import {
  IMPACT_LABELS,
  LIKELIHOOD_LABELS,
  QUALITY_PERMISSIONS as Q,
  RISK_CATEGORIES,
  RISK_RATINGS,
  RISK_STATUSES,
  riskStatusLabel,
  type RiskCategory,
  type RiskRating,
  type RiskStatus,
} from '@/modules/quality/labels';
import { RiskDrawer } from '@/modules/quality/RiskDrawer';
import { RiskMatrix } from '@/modules/quality/RiskMatrix';
import { RiskReviewDialog } from '@/modules/quality/RiskReviewDialog';
import { riskRating, riskScore } from '@/modules/quality/rules';

interface RiskSearch {
  residual?: boolean;
  l?: number;
  i?: number;
  category?: RiskCategory;
  rating?: RiskRating;
  status?: RiskStatus;
  departmentId?: string;
  reviewOverdue?: boolean;
  search?: string;
  sort?: string;
  desc?: boolean;
  page?: number;
}

const SORTS = ['score', 'residualScore', 'reviewDate', 'number', 'title'];
const level = (value: unknown) => (typeof value === 'number' && value >= 1 && value <= 5 ? value : undefined);

export const Route = createFileRoute('/_app/quality/risks')({
  validateSearch: (s: Record<string, unknown>): RiskSearch => ({
    residual: s.residual === true ? true : undefined,
    l: level(s.l),
    i: level(s.i),
    category: RISK_CATEGORIES.includes(s.category as RiskCategory) ? (s.category as RiskCategory) : undefined,
    rating: RISK_RATINGS.includes(s.rating as RiskRating) ? (s.rating as RiskRating) : undefined,
    status: RISK_STATUSES.includes(s.status as RiskStatus) ? (s.status as RiskStatus) : undefined,
    departmentId: typeof s.departmentId === 'string' && s.departmentId ? s.departmentId : undefined,
    reviewOverdue: s.reviewOverdue === true ? true : undefined,
    search: typeof s.search === 'string' && s.search ? s.search : undefined,
    sort: typeof s.sort === 'string' && SORTS.includes(s.sort) ? s.sort : undefined,
    desc: s.desc === false ? false : undefined,
    page: typeof s.page === 'number' && s.page > 1 ? s.page : undefined,
  }),
  component: () => (
    <RequirePermission permission={Q.risksManage} what="the risk register">
      <RisksPage />
    </RequirePermission>
  ),
});

function RisksPage() {
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const departments = useDepartments();
  const [term, setTerm] = useState(search.search ?? '');
  const debounced = useDebouncedValue(term.trim(), 300);
  const [editing, setEditing] = useState<string | 'new' | null>(null);
  const [reviewing, setReviewing] = useState<string | null>(null);
  const residual = Boolean(search.residual);
  const cell = search.l && search.i ? { likelihood: search.l, impact: search.i } : null;
  const cellScore = cell ? riskScore(cell.likelihood, cell.impact) : null;
  const sort = search.sort ?? (residual ? 'residualScore' : 'score');
  const desc = search.desc ?? true;
  const matrix = useQuery(qualityQueries.riskMatrix(residual));
  // A selected cell is filtered on this workstation: the list endpoint filters by rating, not by the exact cell.
  const risks = useQuery(
    qualityQueries.risks({
      category: search.category,
      rating: cell && cellScore !== null ? (residual ? undefined : riskRating(cellScore)) : search.rating,
      status: search.status,
      departmentId: search.departmentId,
      reviewOverdue: search.reviewOverdue,
      search: search.search,
      sort,
      desc,
      page: cell ? 1 : search.page,
      pageSize: cell ? 200 : 25,
    }),
  );
  const rows = cell
    ? (risks.data?.items ?? []).filter((r) =>
        residual ? r.residualScore === cellScore : r.likelihood === cell.likelihood && r.impact === cell.impact,
      )
    : risks.data?.items;

  useEffect(() => {
    if ((debounced || undefined) !== search.search) {
      void navigate({
        search: (prev) => ({ ...prev, search: debounced || undefined, page: undefined }),
        replace: true,
      });
    }
  }, [debounced, search.search, navigate]);

  const set = (patch: Partial<RiskSearch>) =>
    void navigate({ search: (prev) => ({ ...prev, ...patch, page: undefined }) });

  return (
    <Page>
      <PageHeader
        title="Risk register"
        subtitle="Score = likelihood × impact. Review each risk by its review date; residual risk is what remains after the controls."
        icon={<GridRegular />}
        actions={
          <Button appearance="primary" icon={<AddRegular />} onClick={() => setEditing('new')}>
            Add a risk
          </Button>
        }
      />
      <Section
        title="Heat map of open risks"
        description={
          cell
            ? `Showing ${LIKELIHOOD_LABELS[cell.likelihood - 1]} × ${IMPACT_LABELS[cell.impact - 1]} below. Click the cell again to clear.`
            : 'Click a cell to list its risks.'
        }
        actions={
          <TabList
            size="small"
            selectedValue={residual ? 'residual' : 'inherent'}
            onTabSelect={(_, d) =>
              set({ residual: d.value === 'residual' ? true : undefined, l: undefined, i: undefined })
            }
          >
            <Tab value="inherent">Before controls</Tab>
            <Tab value="residual">After controls</Tab>
          </TabList>
        }
      >
        {matrix.isLoading && <LoadingState />}
        {matrix.error && <ErrorState error={matrix.error} onRetry={() => void matrix.refetch()} />}
        {matrix.data && (
          <div style={{ maxWidth: 760 }}>
            <RiskMatrix
              cells={matrix.data}
              residual={residual}
              selected={cell}
              onSelect={(next) => set({ l: next?.likelihood, i: next?.impact })}
            />
          </div>
        )}
      </Section>
      <Section flush>
        <FilterPad>
          <FilterBar>
            <Field label="Search" style={{ flex: '1 1 220px' }}>
              <SearchBox value={term} onChange={(_, d) => setTerm(d.value)} placeholder="Risk number or title" />
            </Field>
            <Field label="Category">
              <Select
                value={search.category ?? ''}
                onChange={(_, d) => set({ category: (d.value || undefined) as RiskCategory | undefined })}
              >
                <option value="">All</option>
                {RISK_CATEGORIES.map((c) => (
                  <option key={c} value={c}>
                    {humanize(c)}
                  </option>
                ))}
              </Select>
            </Field>
            {!cell && (
              <Field label="Rating">
                <Select
                  value={search.rating ?? ''}
                  onChange={(_, d) => set({ rating: (d.value || undefined) as RiskRating | undefined })}
                >
                  <option value="">All</option>
                  {RISK_RATINGS.map((r) => (
                    <option key={r} value={r}>
                      {r}
                    </option>
                  ))}
                </Select>
              </Field>
            )}
            <Field label="Status">
              <Select
                value={search.status ?? ''}
                onChange={(_, d) => set({ status: (d.value || undefined) as RiskStatus | undefined })}
              >
                <option value="">All except closed</option>
                {RISK_STATUSES.map((s) => (
                  <option key={s} value={s}>
                    {riskStatusLabel[s]}
                  </option>
                ))}
              </Select>
            </Field>
            <Field label="Department">
              <Select
                value={search.departmentId ?? ''}
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
            <Checkbox
              style={{ alignSelf: 'flex-end' }}
              checked={Boolean(search.reviewOverdue)}
              onChange={(_, d) => set({ reviewOverdue: d.checked ? true : undefined })}
              label="Review overdue"
            />
            {cell && (
              <Button
                style={{ alignSelf: 'flex-end' }}
                icon={<DismissRegular />}
                onClick={() => set({ l: undefined, i: undefined })}
              >
                Clear heat-map cell
              </Button>
            )}
          </FilterBar>
        </FilterPad>
        <DataTable
          ariaLabel="Risks"
          rows={rows}
          loading={risks.isLoading}
          error={risks.error}
          onRetry={() => void risks.refetch()}
          getRowId={(r) => r.id}
          onRowClick={(r) => setEditing(r.id)}
          sort={{ key: sort, desc }}
          onSortChange={(next) => set({ sort: next.key, desc: next.desc ? undefined : false })}
          columns={[
            {
              key: 'title',
              header: 'Risk',
              sortable: true,
              render: (r) => (
                <span>
                  <Text weight="semibold">{r.title}</Text>
                  <Caption1 block>
                    {r.riskNumber} · {humanize(r.category)}
                  </Caption1>
                </span>
              ),
            },
            {
              key: 'score',
              header: 'Before controls',
              sortable: true,
              render: (r) => <RatingBadge rating={r.rating} score={r.score} />,
            },
            {
              key: 'residualScore',
              header: 'After controls',
              sortable: true,
              render: (r) =>
                r.residualRating ? (
                  <RatingBadge rating={r.residualRating} score={r.residualScore} />
                ) : (
                  <Caption1>Not assessed</Caption1>
                ),
            },
            {
              key: 'owner',
              header: 'Owner',
              priority: 'low',
              render: (r) => <UserName id={r.ownerUserId} fallback="No owner" />,
            },
            {
              key: 'reviewDate',
              header: 'Next review',
              sortable: true,
              render: (r) =>
                r.isReviewOverdue ? (
                  <StatusBadge status="Overdue" tone="danger" label={`Overdue · ${formatDateOnly(r.reviewDate)}`} />
                ) : (
                  formatDateOnly(r.reviewDate)
                ),
            },
            {
              key: 'status',
              header: 'Status',
              priority: 'low',
              render: (r) => (
                <StatusBadge
                  status={r.status}
                  label={riskStatusLabel[r.status]}
                  tone={r.status === 'Closed' ? 'success' : r.status === 'Accepted' ? 'informative' : undefined}
                />
              ),
            },
          ]}
          rowActions={(r) =>
            r.status === 'Closed' ? null : (
              <Tooltip content="Record a periodic review" relationship="label">
                <Button size="small" icon={<ArrowSyncRegular />} onClick={() => setReviewing(r.id)}>
                  Review
                </Button>
              </Tooltip>
            )
          }
          emptyTitle={cell ? 'No risks in this cell' : 'No risks match'}
          emptyDescription="Add the hospital's risks with their likelihood and impact to see them on the heat map."
          page={cell ? undefined : (search.page ?? 1)}
          pageSize={25}
          totalCount={cell ? undefined : risks.data?.totalCount}
          onPageChange={
            cell
              ? undefined
              : (page) => void navigate({ search: (prev) => ({ ...prev, page: page > 1 ? page : undefined }) })
          }
        />
      </Section>
      {editing && <RiskDrawer riskId={editing === 'new' ? null : editing} onClose={() => setEditing(null)} />}
      {reviewing && <ReviewLauncher riskId={reviewing} onClose={() => setReviewing(null)} />}
    </Page>
  );
}

/** Loads the risk (with its version) before the review dialog opens. */
function ReviewLauncher({ riskId, onClose }: { riskId: string; onClose: () => void }) {
  const risk = useQuery(qualityQueries.risk(riskId));
  if (risk.error) {
    return (
      <Dialog open onOpenChange={(_, d) => !d.open && onClose()}>
        <DialogSurface>
          <DialogBody>
            <DialogTitle>Review risk</DialogTitle>
            <DialogContent>
              <ErrorState error={risk.error} onRetry={() => void risk.refetch()} />
            </DialogContent>
            <DialogActions>
              <Button onClick={onClose}>Close</Button>
            </DialogActions>
          </DialogBody>
        </DialogSurface>
      </Dialog>
    );
  }
  return risk.data ? <RiskReviewDialog open onClose={onClose} risk={risk.data} /> : null;
}
