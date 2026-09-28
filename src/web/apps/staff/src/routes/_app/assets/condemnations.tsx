import {
  Button,
  Caption1,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  DrawerBody,
  DrawerFooter,
  DrawerHeader,
  DrawerHeaderTitle,
  Field,
  makeStyles,
  MessageBar,
  MessageBarBody,
  OverlayDrawer,
  Select,
  Spinner,
  Text,
  Textarea,
  tokens,
} from '@fluentui/react-components';
import { AddRegular, BinRecycleRegular, CheckmarkRegular, DismissRegular, OpenRegular } from '@fluentui/react-icons';
import { formatDateOnly, formatDateTime, formatInr, isApiError, useSession, type PagedResult } from '@hc360/core';
import { DataTable, FilterBar, Page, PageHeader, Section, StatusBadge, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { api } from '@/lib/api';
import { usePermission } from '@/lib/permissions';
import { assetApi, assetKeys, assetQueries, type AssetListItem, type CondemnationDto } from '@/modules/assets/api';
import { DisposeDialog } from '@/modules/assets/AssetDialogs';
import { assetStatusLabel, assetStatusTone, CONDEMNATION_STATUSES, type CondemnationStatus } from '@/modules/assets/labels';
import { SearchPicker } from '@/modules/inventory/ItemPicker';
import { SUPPLY_PERMISSIONS as S } from '@/modules/inventory/permissions';
import { ReasonDialog } from '@/modules/inventory/ReasonDialog';
import { Detail, DetailGrid, DocStatus } from '@/modules/inventory/StockBits';

type StatusFilter = CondemnationStatus | 'all';

interface CondemnationSearch {
  /** Pending when absent. */
  status?: StatusFilter;
  id?: string;
}

export const Route = createFileRoute('/_app/assets/condemnations')({
  validateSearch: (s: Record<string, unknown>): CondemnationSearch => ({
    status: s.status === 'all' || (CONDEMNATION_STATUSES.includes(s.status as CondemnationStatus) && s.status !== 'Pending') ? (s.status as StatusFilter) : undefined,
    id: typeof s.id === 'string' && s.id ? s.id : undefined,
  }),
  component: CondemnationsPage,
});

const STATUS_TEXT: Record<CondemnationStatus, string> = { Pending: 'Awaiting decision', Approved: 'Condemned', Rejected: 'Rejected' };
const SELF_DECISION = 'condemnation.self_approval';

const useStyles = makeStyles({
  muted: { color: tokens.colorNeutralForeground3 },
  reason: { display: '-webkit-box', WebkitLineClamp: 2, WebkitBoxOrient: 'vertical', overflow: 'hidden' },
  stack: { display: 'flex', flexDirection: 'column', gap: '16px' },
});

/**
 * Condemnation of equipment beyond economical repair (AST-06): biomedical engineering requests it, a second person
 * with asset rights decides, and the approved asset is later disposed of (auction, buy-back or e-waste recycler).
 */
function CondemnationsPage() {
  const styles = useStyles();
  const search = Route.useSearch();
  const status: StatusFilter = search.status ?? 'Pending';
  const navigate = useNavigate({ from: Route.fullPath });
  const canRequest = usePermission([S.assetsMaintenance, S.assetsManage]);
  const [requesting, setRequesting] = useState(false);
  const list = useQuery(assetQueries.condemnations(status === 'all' ? undefined : status));
  const selected = search.id ? list.data?.find((c) => c.id === search.id) : undefined;
  const show = (id: string | undefined) => void navigate({ search: (prev) => ({ ...prev, id }) });

  return (
    <Page>
      <PageHeader
        title="Condemnation"
        subtitle="Equipment beyond economical repair is condemned on a second person's approval, then disposed of and taken off the register."
        actions={
          canRequest && (
            <Button appearance="primary" icon={<AddRegular />} onClick={() => setRequesting(true)}>
              Request condemnation
            </Button>
          )
        }
      />
      <Section flush>
        <div style={{ padding: '0 16px 12px' }}>
          <FilterBar>
            <Field label="Show">
              <Select value={status} onChange={(_, d) => void navigate({ search: { status: d.value === 'Pending' ? undefined : (d.value as StatusFilter) } })}>
                <option value="Pending">Awaiting decision</option>
                <option value="Approved">Condemned</option>
                <option value="Rejected">Rejected</option>
                <option value="all">All requests</option>
              </Select>
            </Field>
          </FilterBar>
        </div>
        <DataTable<CondemnationDto>
          ariaLabel="Condemnation requests"
          rows={list.data}
          loading={list.isLoading}
          error={list.error}
          onRetry={() => void list.refetch()}
          getRowId={(c) => c.id}
          onRowClick={(c) => show(c.id)}
          size="small"
          columns={[
            {
              key: 'asset',
              header: 'Equipment',
              render: (c) => (
                <span>
                  <Text weight="semibold">{c.assetName}</Text> <Caption1 className={styles.muted}>{c.assetTag}</Caption1>
                </span>
              ),
            },
            { key: 'reason', header: 'Why', render: (c) => <span className={styles.reason}>{c.reason}</span> },
            { key: 'book', header: 'Book value', align: 'end', priority: 'low', render: (c) => formatInr(c.bookValueAtRequest) },
            {
              key: 'requested',
              header: 'Requested',
              priority: 'low',
              render: (c) => (
                <span>
                  {formatDateOnly(c.requestedAt)}
                  {c.requestedBy && <Caption1 block>{c.requestedBy}</Caption1>}
                </span>
              ),
            },
            {
              key: 'status',
              header: 'Status',
              render: (c) => (
                <span>
                  <DocStatus status={c.status} label={STATUS_TEXT[c.status]} />
                  {c.decidedAt && (
                    <Caption1 block className={styles.muted}>
                      {formatDateOnly(c.decidedAt)}
                    </Caption1>
                  )}
                </span>
              ),
            },
          ]}
          emptyTitle={status === 'Pending' ? 'No request awaits a decision' : 'No requests'}
          emptyDescription={canRequest ? 'Request condemnation from here or from the asset’s page.' : undefined}
        />
      </Section>
      {selected && <CondemnationDrawer key={selected.id} request={selected} onClose={() => show(undefined)} />}
      {requesting && (
        <RequestDialog
          onClose={() => setRequesting(false)}
          onRequested={() => {
            setRequesting(false);
            void navigate({ search: {} });
          }}
        />
      )}
    </Page>
  );
}

function CondemnationDrawer({ request: c, onClose }: { request: CondemnationDto; onClose: () => void }) {
  const styles = useStyles();
  const navigate = useNavigate();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const canDecide = usePermission(S.assetsManage);
  const me = useSession((s) => s.user?.displayName);
  const asset = useQuery(assetQueries.detail(c.assetId));
  const [dialog, setDialog] = useState<'approve' | 'reject' | 'dispose' | null>(null);
  const decide = useMutation({
    mutationFn: ({ approve, remarks }: { approve: boolean; remarks: string | null }) =>
      approve ? assetApi.approveCondemnation(c.id, remarks) : assetApi.rejectCondemnation(c.id, remarks),
    onSuccess: (_, { approve }) => {
      setDialog(null);
      notify.success(approve ? `${c.assetName} condemned` : 'Request rejected', approve ? 'Record the disposal once it is auctioned, bought back or handed to the recycler.' : 'The equipment stays in service.');
      void queryClient.invalidateQueries({ queryKey: assetKeys.all });
    },
  });
  const pending = c.status === 'Pending';
  const ownRequest = Boolean(me && c.requestedBy === me);
  const selfRefused = isApiError(decide.error) && decide.error.code === SELF_DECISION;
  const blocked = ownRequest || selfRefused;
  const a = asset.data;
  const canDispose = canDecide && c.status === 'Approved' && a?.status === 'Condemned';

  return (
    <OverlayDrawer open position="end" size="medium" onOpenChange={(_, d) => !d.open && onClose()}>
      <DrawerHeader>
        <DrawerHeaderTitle action={<Button appearance="subtle" icon={<DismissRegular />} aria-label="Close" onClick={onClose} />}>{c.assetName}</DrawerHeaderTitle>
      </DrawerHeader>
      <DrawerBody>
        <div className={styles.stack}>
          <div style={{ display: 'flex', gap: 8, alignItems: 'center', flexWrap: 'wrap' }}>
            <DocStatus status={c.status} label={STATUS_TEXT[c.status]} />
            {a && <StatusBadge status={a.status} tone={assetStatusTone[a.status]} label={`Asset: ${assetStatusLabel[a.status]}`} />}
          </div>
          <div>
            <Caption1 className={styles.muted}>Why it should be condemned</Caption1>
            <Text block style={{ whiteSpace: 'pre-wrap' }}>
              {c.reason}
            </Text>
          </div>
          <DetailGrid>
            <Detail label="Asset tag">{c.assetTag}</Detail>
            <Detail label="Book value when requested">{formatInr(c.bookValueAtRequest)}</Detail>
            <Detail label="Requested">
              {formatDateTime(c.requestedAt)}
              {c.requestedBy ? ` by ${c.requestedBy}` : ''}
            </Detail>
            <Detail label="Decided">{c.decidedAt ? formatDateTime(c.decidedAt) : null}</Detail>
            {c.decisionRemarks && <Detail label="Decision remarks">{c.decisionRemarks}</Detail>}
            {a?.disposedOn && (
              <>
                <Detail label="Disposed on">{formatDateOnly(a.disposedOn)}</Detail>
                <Detail label="Amount received">{a.disposalValue !== null ? formatInr(a.disposalValue) : null}</Detail>
                <Detail label="How">{a.disposalMethod}</Detail>
              </>
            )}
          </DetailGrid>
          {pending && canDecide && blocked && (
            <MessageBar intent="warning">
              <MessageBarBody>You asked for this condemnation, so someone else with asset rights must approve or reject it.</MessageBarBody>
            </MessageBar>
          )}
          {decide.error && !selfRefused && dialog === null && (
            <MessageBar intent="error">
              <MessageBarBody>{isApiError(decide.error) ? decide.error.userMessage : 'The decision was not saved.'}</MessageBarBody>
            </MessageBar>
          )}
        </div>
      </DrawerBody>
      <DrawerFooter>
        <Button icon={<OpenRegular />} onClick={() => void navigate({ to: '/assets/$assetId', params: { assetId: c.assetId } })}>
          Asset
        </Button>
        {pending && canDecide && (
          <>
            <Button appearance="primary" icon={<CheckmarkRegular />} disabled={blocked} onClick={() => setDialog('approve')}>
              Approve
            </Button>
            <Button icon={<DismissRegular />} disabled={blocked} onClick={() => setDialog('reject')}>
              Reject
            </Button>
          </>
        )}
        {canDispose && (
          <Button appearance="primary" icon={<BinRecycleRegular />} onClick={() => setDialog('dispose')}>
            Record disposal
          </Button>
        )}
      </DrawerFooter>
      {dialog === 'approve' && (
        <ReasonDialog
          title={`Condemn ${c.assetName}?`}
          label="Committee remarks"
          placeholder="e.g. Condemnation committee meeting of 12 Sep, item 4"
          confirmLabel="Approve condemnation"
          destructive
          optional
          minLength={3}
          maxLength={500}
          busy={decide.isPending}
          error={decide.error}
          onSubmit={(remarks) => decide.mutate({ approve: true, remarks: remarks || null })}
          onClose={() => setDialog(null)}
        >
          The asset is taken out of service for good: no more repairs, maintenance or use. Book value today {a ? formatInr(a.bookValue) : formatInr(c.bookValueAtRequest)}.
        </ReasonDialog>
      )}
      {dialog === 'reject' && (
        <ReasonDialog
          title="Reject this request?"
          label="Why"
          placeholder="e.g. Repair quoted within budget; get a second opinion"
          confirmLabel="Reject request"
          minLength={5}
          maxLength={500}
          busy={decide.isPending}
          error={decide.error}
          onSubmit={(remarks) => decide.mutate({ approve: false, remarks })}
          onClose={() => setDialog(null)}
        >
          The equipment stays in service. The requester sees your reason.
        </ReasonDialog>
      )}
      {dialog === 'dispose' && a && <DisposeDialog asset={{ id: a.id, name: a.name, assetTag: a.assetTag, bookValue: a.bookValue }} onClose={() => setDialog(null)} />}
    </OverlayDrawer>
  );
}

function RequestDialog({ onClose, onRequested }: { onClose: () => void; onRequested: () => void }) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [asset, setAsset] = useState<AssetListItem | null>(null);
  const [reason, setReason] = useState('');
  const valid = asset !== null && reason.trim().length >= 10;
  const request = useMutation({
    mutationFn: () => assetApi.requestCondemnation(asset!.id, reason.trim()),
    onSuccess: () => {
      notify.success('Condemnation requested', 'Another person with asset rights must approve it.');
      void queryClient.invalidateQueries({ queryKey: assetKeys.all });
      onRequested();
    },
  });
  return (
    <Dialog open onOpenChange={(_, d) => !d.open && !request.isPending && onClose()}>
      <DialogSurface style={{ maxWidth: 600, width: '95vw' }}>
        <DialogBody>
          <DialogTitle>Request condemnation</DialogTitle>
          <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12, minHeight: 260 }}>
            <SearchPicker<AssetListItem>
              label="Equipment"
              required
              autoFocus
              placeholder="Asset tag (scan the label), name, model or serial number"
              queryKey={['assets', 'picker']}
              fetch={(term, signal) => api.get<PagedResult<AssetListItem>>('/api/v1/assets', { query: { search: term, pageSize: 10 }, signal }).then((r) => r.items)}
              getKey={(x) => x.id}
              bestMatch={(items, typed) => items.find((x) => x.assetTag.toUpperCase() === typed.trim().toUpperCase()) ?? items[0]}
              selectedText={asset ? `${asset.name} (${asset.assetTag})` : null}
              emptyText="No equipment matches. Condemned and disposed assets are not listed."
              onPick={setAsset}
              renderOption={(x) => (
                <div style={{ display: 'flex', justifyContent: 'space-between', gap: 12, width: '100%' }}>
                  <span>
                    <Text weight="semibold">{x.name}</Text>
                    <Caption1 block>
                      {x.assetTag}
                      {x.model ? ` · ${x.model}` : ''}
                      {x.location ? ` · ${x.location}` : x.departmentName ? ` · ${x.departmentName}` : ''}
                    </Caption1>
                  </span>
                  <Caption1>{assetStatusLabel[x.status]}</Caption1>
                </div>
              )}
            />
            <Field label="Why should it be condemned?" required hint="Beyond economical repair, obsolete, unsafe, parts no longer available… at least 10 characters">
              <Textarea value={reason} rows={4} maxLength={1000} onChange={(_, d) => setReason(d.value)} />
            </Field>
            {request.error && (
              <MessageBar intent="error">
                <MessageBarBody>{isApiError(request.error) ? request.error.userMessage : 'Not requested. Try again.'}</MessageBarBody>
              </MessageBar>
            )}
          </DialogContent>
          <DialogActions>
            <Button onClick={onClose} disabled={request.isPending}>
              Cancel
            </Button>
            <Button appearance="primary" disabled={!valid || request.isPending} icon={request.isPending ? <Spinner size="tiny" /> : undefined} onClick={() => request.mutate()}>
              Request condemnation
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}
