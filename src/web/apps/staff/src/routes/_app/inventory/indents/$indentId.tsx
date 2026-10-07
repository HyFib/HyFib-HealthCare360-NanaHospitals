import {
  Badge,
  Button,
  Caption1,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Field,
  Input,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  Spinner,
  Text,
  tokens,
} from '@fluentui/react-components';
import {
  AlertUrgentRegular,
  BoxArrowUpRegular,
  CheckmarkRegular,
  DismissCircleRegular,
  PrintRegular,
  StopRegular,
} from '@fluentui/react-icons';
import { formatDateTime, formatInr, isApiError } from '@hc360/core';
import { DataTable, ErrorState, LoadingState, Page, PageHeader, Section, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { useLetterhead } from '@/lib/letterhead';
import { usePermission } from '@/lib/permissions';
import {
  inventoryApi,
  inventoryKeys,
  inventoryQueries,
  type IndentDetail,
  type IndentIssueDto,
} from '@/modules/inventory/api';
import { dec, min, toNumber } from '@/modules/inventory/decimal';
import { formatQty, parseNumber } from '@/modules/inventory/format';
import { LineTable } from '@/modules/inventory/LineTable';
import { SUPPLY_PERMISSIONS as S } from '@/modules/inventory/permissions';
import { printIndentIssue } from '@/modules/inventory/print';
import { ReasonDialog } from '@/modules/inventory/ReasonDialog';
import { Detail, DetailGrid, DocStatus } from '@/modules/inventory/StockBits';

export const Route = createFileRoute('/_app/inventory/indents/$indentId')({
  component: IndentDetailPage,
});

type DialogKind = 'approve' | 'issue' | 'reject' | 'cancel' | 'close';

/** One indent: approve (reserves stock FEFO), issue (transfers to the sub-store or books consumption), reject, cancel or short-close. */
function IndentDetailPage() {
  const { indentId } = Route.useParams();
  const navigate = useNavigate();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const letterhead = useLetterhead();
  const canIssue = usePermission(S.indentsIssue);
  const canRaise = usePermission(S.indentsRaise);
  const indent = useQuery(inventoryQueries.indent(indentId));
  const issues = useQuery(inventoryQueries.indentIssues(indentId));
  const [dialog, setDialog] = useState<DialogKind | null>(null);

  const refresh = () => {
    void queryClient.invalidateQueries({ queryKey: inventoryKeys.indents });
    void queryClient.invalidateQueries({ queryKey: inventoryKeys.stock });
    void queryClient.invalidateQueries({ queryKey: inventoryKeys.batches });
    void queryClient.invalidateQueries({ queryKey: inventoryKeys.reports });
  };

  const reasonAction = useMutation({
    mutationFn: ({ kind, reason }: { kind: 'reject' | 'cancel' | 'close'; reason: string }) =>
      kind === 'reject'
        ? inventoryApi.rejectIndent(indentId, reason)
        : kind === 'cancel'
          ? inventoryApi.cancelIndent(indentId, reason)
          : inventoryApi.closeIndent(indentId, reason),
    onSuccess: (_, { kind }) => {
      notify.success(
        kind === 'reject' ? 'Indent rejected' : kind === 'cancel' ? 'Indent cancelled' : 'Indent closed',
        'Any stock set aside for it was released.',
      );
      setDialog(null);
      refresh();
    },
  });

  if (indent.isLoading) {
    return <LoadingState />;
  }
  if (!indent.data) {
    return (
      <Page maxWidth={900}>
        <ErrorState error={indent.error} onRetry={() => void indent.refetch()} />
      </Page>
    );
  }
  const d = indent.data;
  const isOpen = d.status === 'Pending' || d.status === 'Approved' || d.status === 'PartiallyIssued';
  const canIssueNow = canIssue && isOpen && d.lines.some((l) => l.pendingQuantity > 0);
  const canCancel = (canRaise || canIssue) && (d.status === 'Pending' || d.status === 'Approved');

  return (
    <Page>
      <PageHeader
        title={`Indent ${d.number}`}
        subtitle={`${d.fromStoreName} → ${d.toStoreName ?? d.departmentName ?? '—'}`}
        breadcrumbs={[
          { label: 'Indents', onClick: () => void navigate({ to: '/inventory/indents' }) },
          { label: d.number },
        ]}
        actions={
          <>
            {canCancel && (
              <Button icon={<DismissCircleRegular />} onClick={() => setDialog('cancel')}>
                Cancel
              </Button>
            )}
            {canIssue && (d.status === 'Pending' || d.status === 'Approved') && (
              <Button icon={<StopRegular />} onClick={() => setDialog('reject')}>
                Reject
              </Button>
            )}
            {canIssue && d.status === 'PartiallyIssued' && (
              <Button icon={<StopRegular />} onClick={() => setDialog('close')}>
                Short-close
              </Button>
            )}
            {canIssue && d.status === 'Pending' && (
              <Button icon={<CheckmarkRegular />} onClick={() => setDialog('approve')}>
                Approve
              </Button>
            )}
            {canIssueNow && (
              <Button appearance="primary" icon={<BoxArrowUpRegular />} onClick={() => setDialog('issue')}>
                Issue items
              </Button>
            )}
          </>
        }
      />

      <Section>
        <DetailGrid>
          <Detail label="Status">
            <DocStatus status={d.status} />
          </Detail>
          <Detail label="Priority">
            {d.priority === 'Urgent' ? (
              <Badge color="danger" icon={<AlertUrgentRegular />}>
                Urgent
              </Badge>
            ) : (
              'Routine'
            )}
          </Detail>
          <Detail label="Requested">
            {formatDateTime(d.requestedAt)}
            {d.requestedBy ? ` by ${d.requestedBy}` : ''}
          </Detail>
          <Detail label="Issuing store">{d.fromStoreName}</Detail>
          <Detail label={d.toStoreName ? 'Receiving store' : 'Department'}>{d.toStoreName ?? d.departmentName}</Detail>
          {d.decidedAt && <Detail label="Decided">{formatDateTime(d.decidedAt)}</Detail>}
          {d.remarks && <Detail label="Remarks">{d.remarks}</Detail>}
          {d.decisionNote && <Detail label="Decision note">{d.decisionNote}</Detail>}
        </DetailGrid>
      </Section>

      <Section title="Items" flush>
        <DataTable
          ariaLabel="Indent lines"
          rows={d.lines}
          getRowId={(l) => l.id}
          size="small"
          columns={[
            {
              key: 'item',
              header: 'Item',
              render: (l) => (
                <span>
                  <Text weight="semibold">{l.itemName}</Text>
                  {l.remarks && <Caption1 block>{l.remarks}</Caption1>}
                </span>
              ),
            },
            { key: 'req', header: 'Requested', align: 'end', render: (l) => formatQty(l.requestedQuantity) },
            {
              key: 'appr',
              header: 'Approved',
              align: 'end',
              render: (l) => (l.approvedQuantity === null ? '—' : formatQty(l.approvedQuantity)),
            },
            { key: 'issued', header: 'Issued', align: 'end', render: (l) => formatQty(l.issuedQuantity) },
            {
              key: 'pending',
              header: 'Still to issue',
              align: 'end',
              render: (l) => <Text weight="semibold">{formatQty(l.pendingQuantity)}</Text>,
            },
            {
              key: 'reserved',
              header: 'Set aside',
              align: 'end',
              priority: 'low',
              render: (l) => (l.reserved ? formatQty(l.reserved) : '—'),
            },
            {
              key: 'stock',
              header: 'Free in store',
              align: 'end',
              priority: 'low',
              render: (l) => (
                <Text
                  style={
                    l.pendingQuantity > l.availableInStore + l.reserved
                      ? { color: tokens.colorPaletteRedForeground1 }
                      : undefined
                  }
                >
                  {formatQty(l.availableInStore)}
                </Text>
              ),
            },
          ]}
        />
      </Section>

      {(issues.data?.length ?? 0) > 0 && (
        <Section title="Issue slips" flush>
          <DataTable<IndentIssueDto>
            ariaLabel="Issue slips"
            rows={issues.data}
            getRowId={(i) => i.id}
            size="small"
            columns={[
              { key: 'no', header: 'Slip', render: (i) => <Text font="monospace">{i.number}</Text> },
              { key: 'at', header: 'Issued', render: (i) => formatDateTime(i.issuedAt) },
              { key: 'by', header: 'By', priority: 'low', render: (i) => i.issuedBy ?? '—' },
              { key: 'to', header: 'Received by', priority: 'low', render: (i) => i.receivedByName ?? '—' },
              {
                key: 'items',
                header: 'Items',
                render: (i) => i.lines.map((l) => `${l.itemName} × ${formatQty(l.quantity)}`).join(', '),
              },
              { key: 'value', header: 'Value', align: 'end', render: (i) => formatInr(i.totalValue) },
            ]}
            rowActions={(i) => (
              <Button
                size="small"
                appearance="subtle"
                icon={<PrintRegular />}
                aria-label={`Print slip ${i.number}`}
                onClick={() =>
                  void printIndentIssue(i, d, letterhead).catch((error: unknown) =>
                    notify.error(error, 'Printing failed'),
                  )
                }
              />
            )}
          />
        </Section>
      )}

      {dialog === 'approve' && <ApproveDialog indent={d} onClose={() => setDialog(null)} onDone={refresh} />}
      {dialog === 'issue' && (
        <IssueDialog
          indent={d}
          onClose={() => setDialog(null)}
          onDone={(slip) => {
            refresh();
            void queryClient
              .fetchQuery(inventoryQueries.indentIssues(indentId))
              .then((all) => {
                const printed = all.find((i) => i.id === slip.id);
                if (printed) {
                  return printIndentIssue(printed, d, letterhead);
                }
                return undefined;
              })
              .catch((error: unknown) => notify.error(error, 'The issue slip did not print'));
          }}
        />
      )}
      {(dialog === 'reject' || dialog === 'cancel' || dialog === 'close') && (
        <ReasonDialog
          title={
            dialog === 'reject'
              ? `Reject indent ${d.number}?`
              : dialog === 'cancel'
                ? `Cancel indent ${d.number}?`
                : `Short-close indent ${d.number}?`
          }
          confirmLabel={dialog === 'reject' ? 'Reject' : dialog === 'cancel' ? 'Cancel indent' : 'Short-close'}
          destructive
          busy={reasonAction.isPending}
          error={reasonAction.error}
          onSubmit={(reason) => reasonAction.mutate({ kind: dialog, reason })}
          onClose={() => {
            reasonAction.reset();
            setDialog(null);
          }}
        >
          {dialog === 'close'
            ? 'What was issued stays issued. The balance will not be supplied and any stock set aside for it is released.'
            : 'Any stock set aside for this indent is released for other requests.'}
        </ReasonDialog>
      )}
    </Page>
  );
}

function ApproveDialog({ indent, onClose, onDone }: { indent: IndentDetail; onClose: () => void; onDone: () => void }) {
  const notify = useNotify();
  const [approved, setApproved] = useState<Record<string, string>>({});
  const values = indent.lines.map((l) => ({
    line: l,
    value: parseNumber(approved[l.id] ?? String(l.requestedQuantity)),
  }));
  const invalid = values.some((v) => v.value === null || v.value < 0 || v.value > v.line.requestedQuantity);
  const nothing = values.every((v) => (v.value ?? 0) === 0);
  const approve = useMutation({
    mutationFn: () =>
      inventoryApi.approveIndent(
        indent.id,
        values.map((v) => ({ lineId: v.line.id, approvedQuantity: v.value ?? 0 })),
      ),
    onSuccess: () => {
      notify.success(
        `Indent ${indent.number} approved`,
        'Stock is set aside earliest-expiry first until it is issued.',
      );
      onDone();
      onClose();
    },
  });
  return (
    <Dialog open onOpenChange={(_, d) => !d.open && !approve.isPending && onClose()}>
      <DialogSurface style={{ maxWidth: 720, width: '95vw' }}>
        <DialogBody>
          <DialogTitle>Approve indent {indent.number}</DialogTitle>
          <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
            <Caption1>
              Lower a quantity to approve less, or 0 to leave a line out. Approval sets the stock aside so no one else
              takes it.
            </Caption1>
            <LineTable
              ariaLabel="Approve quantities"
              rows={values}
              getRowId={(v) => v.line.id}
              columns={[
                { key: 'item', header: 'Item', render: (v) => <Text weight="semibold">{v.line.itemName}</Text> },
                { key: 'req', header: 'Requested', align: 'end', render: (v) => formatQty(v.line.requestedQuantity) },
                {
                  key: 'free',
                  header: 'Free in store',
                  align: 'end',
                  render: (v) => formatQty(v.line.availableInStore),
                },
                {
                  key: 'approve',
                  header: 'Approve',
                  width: 130,
                  render: (v) => (
                    <Input
                      type="number"
                      min={0}
                      max={v.line.requestedQuantity}
                      aria-label={`Approved quantity of ${v.line.itemName}`}
                      value={approved[v.line.id] ?? String(v.line.requestedQuantity)}
                      onChange={(_, d) => setApproved((prev) => ({ ...prev, [v.line.id]: d.value }))}
                    />
                  ),
                },
              ]}
            />
            {invalid && (
              <MessageBar intent="warning">
                <MessageBarBody>Approve between 0 and the requested quantity on every line.</MessageBarBody>
              </MessageBar>
            )}
            {approve.error && (
              <MessageBar intent="error">
                <MessageBarBody>
                  {isApiError(approve.error) ? approve.error.userMessage : 'Not approved.'}
                </MessageBarBody>
              </MessageBar>
            )}
          </DialogContent>
          <DialogActions>
            <Button onClick={onClose} disabled={approve.isPending}>
              Cancel
            </Button>
            <Button
              appearance="primary"
              disabled={invalid || nothing || approve.isPending}
              icon={approve.isPending ? <Spinner size="tiny" /> : undefined}
              onClick={() => approve.mutate()}
            >
              Approve
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}

function IssueDialog({
  indent,
  onClose,
  onDone,
}: {
  indent: IndentDetail;
  onClose: () => void;
  onDone: (slip: { id: string; number: string }) => void;
}) {
  const notify = useNotify();
  const [quantities, setQuantities] = useState<Record<string, string>>({});
  const [receivedBy, setReceivedBy] = useState('');
  const [remarks, setRemarks] = useState('');
  const open = indent.lines.filter((l) => l.pendingQuantity > 0);
  const values = open.map((l) => {
    const issuable = toNumber(min(dec(l.pendingQuantity), dec(l.availableInStore) + dec(l.reserved)));
    const text = quantities[l.id] ?? String(issuable);
    const value = parseNumber(text) ?? 0;
    return { line: l, issuable, text, value, invalid: value < 0 || value > l.pendingQuantity };
  });
  const chosen = values.filter((v) => v.value > 0 && !v.invalid);
  const invalid = values.some((v) => v.invalid);

  const issue = useMutation({
    mutationFn: () =>
      inventoryApi.issueIndent(indent.id, {
        lines: chosen.map((v) => ({ lineId: v.line.id, quantity: v.value, batchId: null })),
        receivedByName: receivedBy.trim() || null,
        remarks: remarks.trim() || null,
      }),
    onSuccess: (slip) => {
      notify.success(
        `Issued on slip ${slip.number}`,
        indent.toStoreName
          ? `Stock moved to ${indent.toStoreName}.`
          : `Booked as used by ${indent.departmentName ?? 'the department'}.`,
      );
      onDone(slip);
      onClose();
    },
  });

  return (
    <Dialog open onOpenChange={(_, d) => !d.open && !issue.isPending && onClose()}>
      <DialogSurface style={{ maxWidth: 760, width: '95vw' }}>
        <DialogBody>
          <DialogTitle>Issue items for {indent.number}</DialogTitle>
          <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
            <Caption1>
              Stock set aside for this indent is used first, then the earliest-expiring batches. An issue slip prints
              for the person collecting.
            </Caption1>
            <LineTable
              ariaLabel="Issue quantities"
              rows={values}
              getRowId={(v) => v.line.id}
              isFlagged={(v) => v.invalid}
              columns={[
                { key: 'item', header: 'Item', render: (v) => <Text weight="semibold">{v.line.itemName}</Text> },
                { key: 'pending', header: 'To issue', align: 'end', render: (v) => formatQty(v.line.pendingQuantity) },
                {
                  key: 'stock',
                  header: 'Can issue',
                  align: 'end',
                  render: (v) => (
                    <Text
                      style={
                        v.issuable < v.line.pendingQuantity
                          ? { color: tokens.colorPaletteMarigoldForeground1 }
                          : undefined
                      }
                    >
                      {formatQty(v.issuable)}
                    </Text>
                  ),
                },
                {
                  key: 'issue',
                  header: 'Issue now',
                  width: 130,
                  render: (v) => (
                    <Input
                      type="number"
                      min={0}
                      aria-label={`Quantity of ${v.line.itemName} to issue`}
                      value={v.text}
                      onChange={(_, d) => setQuantities((prev) => ({ ...prev, [v.line.id]: d.value }))}
                    />
                  ),
                },
              ]}
            />
            <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 12 }}>
              <Field label="Collected by">
                <Input
                  value={receivedBy}
                  maxLength={120}
                  onChange={(_, d) => setReceivedBy(d.value)}
                  placeholder="Name of the person taking the goods"
                />
              </Field>
              <Field label="Remarks">
                <Input value={remarks} maxLength={500} onChange={(_, d) => setRemarks(d.value)} />
              </Field>
            </div>
            {issue.error && (
              <MessageBar intent="error">
                <MessageBarBody>
                  <MessageBarTitle>Nothing was issued</MessageBarTitle>
                  {isApiError(issue.error) ? issue.error.userMessage : 'Try again.'}
                </MessageBarBody>
              </MessageBar>
            )}
          </DialogContent>
          <DialogActions>
            <Button onClick={onClose} disabled={issue.isPending}>
              Cancel
            </Button>
            <Button
              appearance="primary"
              disabled={chosen.length === 0 || invalid || issue.isPending}
              icon={issue.isPending ? <Spinner size="tiny" /> : <PrintRegular />}
              onClick={() => issue.mutate()}
            >
              Issue and print slip
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}
