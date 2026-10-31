import {
  Badge,
  Button,
  Caption1,
  Field,
  Input,
  makeStyles,
  Menu,
  MenuItem,
  MenuList,
  MenuPopover,
  MenuTrigger,
  Select,
  Text,
  tokens,
  useRestoreFocusTarget,
} from '@fluentui/react-components';
import {
  DeleteRegular,
  DocumentBulletListRegular,
  LinkRegular,
  MoreHorizontalRegular,
  SearchRegular,
} from '@fluentui/react-icons';
import { formatDateTime } from '@hc360/core';
import { DataTable, FilterBar, Page, PageHeader, Section, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import {
  LAB_PERMISSIONS,
  labApi,
  labKeys,
  labQueries,
  UNMATCHED_STATUSES,
  type UnmatchedResultDto,
  type UnmatchedResultStatus,
} from '@/modules/lab/api';
import { AssignUnmatchedDialog } from '@/modules/lab/AssignUnmatchedDialog';
import { ReasonDialog } from '@/modules/lab/common/ReasonDialog';
import { searchOneOf, searchPage, searchText } from '@/modules/lab/common/search';
import { unmatchedReasonLabel, unmatchedStatusLabel } from '@/modules/lab/labels';
import { analyserReading } from '@/modules/lab/unmatched';
import { useLabLive } from '@/modules/lab/useLabLive';

interface UnmatchedSearch {
  status?: UnmatchedResultStatus;
  search?: string;
  page?: number;
}

export const Route = createFileRoute('/_app/lab/unmatched')({
  validateSearch: (s: Record<string, unknown>): UnmatchedSearch => ({
    status: searchOneOf(s.status, UNMATCHED_STATUSES),
    search: searchText(s.search, 64),
    page: searchPage(s.page),
  }),
  component: UnmatchedResultsPage,
});

const useStyles = makeStyles({
  muted: { color: tokens.colorNeutralForeground3 },
  mono: { fontFamily: tokens.fontFamilyMonospace },
  cell: { display: 'flex', flexDirection: 'column', gap: '2px', minWidth: 0 },
  search: { flex: '1 1 240px' },
});

/**
 * Analyser results that could not go on a sample's test (INT-LAB): no sample id or one no tube has, a sample not in
 * the lab, a value the test cannot hold, another value for a verified result. The analyser was answered so it carries
 * on; each result waits here until someone who enters results puts it on the right sample's test, where a person
 * verifies it, or discards it with the reason. Both are in the audit trail.
 */
function UnmatchedResultsPage() {
  const styles = useStyles();
  // Closing a dialog opened from a row's menu returns focus to that row's menu button.
  const restoreFocusTarget = useRestoreFocusTarget();
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const notify = useNotify();
  const queryClient = useQueryClient();
  const canResolve = usePermission(LAB_PERMISSIONS.enter);
  const [term, setTerm] = useState(search.search ?? '');
  const [assigning, setAssigning] = useState<UnmatchedResultDto | null>(null);
  const [discarding, setDiscarding] = useState<UnmatchedResultDto | null>(null);
  const status = search.status ?? 'Open';
  useLabLive();

  const list = useQuery(labQueries.unmatchedResults({ status, search: search.search, page: search.page }));

  const discard = useMutation({
    mutationFn: ({ id, reason }: { id: string; reason: string }) => labApi.discardUnmatched(id, reason),
    onSuccess: () => {
      setDiscarding(null);
      notify.success('Result discarded', 'The reason is in the audit trail.');
      void queryClient.invalidateQueries({ queryKey: labKeys.unmatched });
    },
    onError: (error) => notify.error(error, 'Could not discard the result'),
  });

  const set = (patch: Partial<UnmatchedSearch>) =>
    void navigate({ search: (prev) => ({ ...prev, ...patch, page: undefined }), replace: true });

  return (
    <Page>
      <PageHeader
        title="Unmatched analyser results"
        subtitle="Results an analyser sent that could not go on a sample's test. Assign each to the right test, or discard it with the reason."
      />
      <Section flush>
        <div style={{ padding: '0 16px 12px' }}>
          <FilterBar>
            <Field label="Results">
              <Select
                value={status}
                onChange={(_, data) =>
                  set({ status: data.value === 'Open' ? undefined : (data.value as UnmatchedResultStatus) })
                }
              >
                {UNMATCHED_STATUSES.map((value) => (
                  <option key={value} value={value}>
                    {unmatchedStatusLabel[value]}
                  </option>
                ))}
              </Select>
            </Field>
            <Field label="Search" className={styles.search} hint="Sample barcode, analyser or channel, then Enter">
              <Input
                value={term}
                onChange={(_, data) => setTerm(data.value.slice(0, 64))}
                onKeyDown={(event) => {
                  if (event.key === 'Enter') {
                    event.preventDefault();
                    set({ search: term.trim() || undefined });
                  }
                }}
                contentBefore={<SearchRegular />}
                autoComplete="off"
              />
            </Field>
          </FilterBar>
        </div>
        <DataTable
          ariaLabel="Unmatched analyser results"
          rows={list.data?.items}
          loading={list.isLoading}
          error={list.error}
          onRetry={() => void list.refetch()}
          getRowId={(row) => row.id}
          size="small"
          columns={[
            {
              key: 'result',
              header: 'Analyser result',
              render: (row: UnmatchedResultDto) => (
                <span className={styles.cell}>
                  <Text weight="semibold">{analyserReading(row)}</Text>
                  <Caption1 className={styles.muted}>
                    {row.instrumentCode} · channel {row.channelCode} · {formatDateTime(row.receivedAt)}
                  </Caption1>
                  {row.instrumentComment && <Caption1>Comment: {row.instrumentComment}</Caption1>}
                </span>
              ),
            },
            {
              key: 'sample',
              header: 'Sample',
              render: (row: UnmatchedResultDto) => (
                <span className={styles.cell}>
                  <Text className={styles.mono}>{row.sampleBarcode ?? 'Not sent'}</Text>
                  {row.patientName && (
                    <Caption1 className={styles.muted}>
                      {row.patientName} · {row.uhid}
                    </Caption1>
                  )}
                  {row.testName && <Caption1>{row.testName}</Caption1>}
                </span>
              ),
            },
            {
              key: 'why',
              header: 'Why it was kept aside',
              render: (row: UnmatchedResultDto) => (
                <span className={styles.cell}>
                  <span>
                    <Badge appearance="tint" color="warning" shape="rounded">
                      {unmatchedReasonLabel[row.reason]}
                    </Badge>
                  </span>
                  <Caption1>{row.detail}</Caption1>
                </span>
              ),
            },
            ...(status === 'Open'
              ? []
              : [
                  {
                    key: 'resolved',
                    header: status === 'Assigned' ? 'Assigned' : 'Discarded',
                    priority: 'low' as const,
                    render: (row: UnmatchedResultDto) => (
                      <span className={styles.cell}>
                        <Caption1>
                          {row.resolvedBy ?? '—'}
                          {row.resolvedAt ? ` · ${formatDateTime(row.resolvedAt)}` : ''}
                        </Caption1>
                        {row.discardReason && <Caption1 className={styles.muted}>{row.discardReason}</Caption1>}
                      </span>
                    ),
                  },
                ]),
          ]}
          rowActions={(row: UnmatchedResultDto) => (
            <Menu>
              <MenuTrigger disableButtonEnhancement>
                <Button
                  size="small"
                  appearance="subtle"
                  icon={<MoreHorizontalRegular />}
                  aria-label={`Actions for the ${row.channelCode} result from ${row.instrumentCode}`}
                  {...restoreFocusTarget}
                />
              </MenuTrigger>
              <MenuPopover>
                <MenuList>
                  {canResolve && row.status === 'Open' && (
                    <MenuItem icon={<LinkRegular />} onClick={() => setAssigning(row)}>
                      Assign to a test
                    </MenuItem>
                  )}
                  {canResolve && row.status === 'Open' && (
                    <MenuItem icon={<DeleteRegular />} onClick={() => setDiscarding(row)}>
                      Discard
                    </MenuItem>
                  )}
                  {row.sampleId && (
                    <MenuItem
                      icon={<DocumentBulletListRegular />}
                      onClick={() => void navigate({ to: '/lab/worklist', search: { sampleId: row.sampleId! } })}
                    >
                      Open the sample
                    </MenuItem>
                  )}
                  {!(canResolve && row.status === 'Open') && !row.sampleId && <MenuItem disabled>No actions</MenuItem>}
                </MenuList>
              </MenuPopover>
            </Menu>
          )}
          emptyTitle={
            status === 'Open'
              ? 'No analyser results waiting'
              : `No ${unmatchedStatusLabel[status].toLowerCase()} results`
          }
          emptyDescription={
            status === 'Open'
              ? 'Results an analyser sends for a sample HC360 cannot match appear here, so none is lost.'
              : 'Change the filter to see the results waiting.'
          }
          page={search.page ?? 1}
          pageSize={25}
          totalCount={list.data?.totalCount}
          onPageChange={(page) => void navigate({ search: (prev) => ({ ...prev, page: page > 1 ? page : undefined }) })}
        />
      </Section>

      {assigning && <AssignUnmatchedDialog result={assigning} onClose={() => setAssigning(null)} />}
      {discarding && (
        <ReasonDialog
          key={discarding.id}
          open
          title={`Discard the ${discarding.channelCode} result from ${discarding.instrumentCode}?`}
          description={
            <Text>
              {analyserReading(discarding)}
              {discarding.sampleBarcode ? ` for sample ${discarding.sampleBarcode}` : ', sent without a sample id'}. It
              goes on no patient&apos;s record; the reason is kept in the audit trail.
            </Text>
          }
          minLength={5}
          suggestions={[
            'Control or calibrator run',
            'Duplicate of a result already on the test',
            'Sample rerun on its own tube',
            'Test not ordered for this patient',
          ]}
          confirmLabel="Discard result"
          destructive
          busy={discard.isPending}
          onCancel={() => setDiscarding(null)}
          onConfirm={(reason) => discard.mutate({ id: discarding.id, reason })}
        />
      )}
    </Page>
  );
}
