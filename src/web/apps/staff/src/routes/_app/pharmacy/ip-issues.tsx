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
  DrawerBody,
  DrawerFooter,
  DrawerHeader,
  DrawerHeaderTitle,
  Field,
  Input,
  makeStyles,
  MessageBar,
  MessageBarBody,
  OverlayDrawer,
  SearchBox,
  Spinner,
  Subtitle2,
  Text,
  tokens,
} from '@fluentui/react-components';
import { ArrowUndoRegular, DismissRegular, PrintRegular } from '@fluentui/react-icons';
import { formatDateTime, formatInr, isApiError, todayIso, useDebouncedValue } from '@hc360/core';
import { DataTable, ErrorState, FilterBar, LoadingState, Page, PageHeader, Section, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useEffect, useState } from 'react';
import { useLetterhead } from '@/lib/letterhead';
import { usePermission } from '@/lib/permissions';
import { dec, sum, toNumber } from '@/modules/inventory/decimal';
import { formatQty, parseNumber } from '@/modules/inventory/format';
import { Detail, DetailGrid, ExpiryText } from '@/modules/inventory/StockBits';
import { PatientPicker, type PickedPatient } from '@/modules/patients/PatientPicker';
import {
  pharmacyApi,
  pharmacyKeys,
  pharmacyQueries,
  type IpIssueDetail,
  type IpIssueListItem,
} from '@/modules/pharmacy/api';
import { PHARMACY_PERMISSIONS as P } from '@/modules/pharmacy/permissions';
import { ipIssueAmount } from '@/modules/pharmacy/pricing';
import { printIpIssueSlip } from '@/modules/pharmacy/print';

interface IssueSearch {
  q?: string;
  patientId?: string;
  admissionId?: string;
  page?: number;
  id?: string;
}

export const Route = createFileRoute('/_app/pharmacy/ip-issues')({
  validateSearch: (s: Record<string, unknown>): IssueSearch => ({
    q: typeof s.q === 'string' && s.q ? s.q : undefined,
    patientId: typeof s.patientId === 'string' && s.patientId ? s.patientId : undefined,
    admissionId: typeof s.admissionId === 'string' && s.admissionId ? s.admissionId : undefined,
    page: typeof s.page === 'number' && s.page > 1 ? s.page : undefined,
    id: typeof s.id === 'string' && s.id ? s.id : undefined,
  }),
  component: IpIssuesPage,
});

const useStyles = makeStyles({
  stack: { display: 'flex', flexDirection: 'column', gap: '16px' },
  muted: { color: tokens.colorNeutralForeground3 },
  returnRow: {
    display: 'grid',
    gridTemplateColumns: 'minmax(0, 1fr) 110px 130px 100px',
    gap: '8px',
    alignItems: 'center',
    padding: '6px 0',
    borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
  },
});

/** Medicines issued to admitted patients, with ward returns that restock the batch and credit the IP bill. */
function IpIssuesPage() {
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const [term, setTerm] = useState(search.q ?? '');
  const [patient, setPatient] = useState<PickedPatient | null>(null);
  const debounced = useDebouncedValue(term.trim(), 300);

  useEffect(() => {
    if ((debounced || undefined) !== search.q) {
      void navigate({ search: (prev) => ({ ...prev, q: debounced || undefined, page: undefined }), replace: true });
    }
  }, [debounced, search.q, navigate]);

  const { id, ...filters } = search;
  const issues = useQuery(
    pharmacyQueries.ipIssues({
      search: filters.q,
      patientId: filters.patientId,
      admissionId: filters.admissionId,
      page: filters.page,
    }),
  );

  return (
    <Page>
      <PageHeader
        title="IP issues and returns"
        subtitle="Medicines sent to the wards and charged to the in-patient account. Open an issue to take back unused medicines."
      />
      <Section flush>
        <div style={{ padding: '0 16px 12px' }}>
          <FilterBar>
            <Field label="Issue number" style={{ flex: '0 1 220px' }}>
              <SearchBox value={term} onChange={(_, d) => setTerm(d.value)} placeholder="e.g. NHIPI26000123" />
            </Field>
            <div style={{ flex: '1 1 320px' }}>
              <PatientPicker
                value={patient}
                onChange={(p) => {
                  setPatient(p);
                  void navigate({
                    search: (prev) => ({ ...prev, patientId: p?.id, admissionId: undefined, page: undefined }),
                  });
                }}
                label="Patient"
              />
            </div>
            {search.admissionId && (
              <Button onClick={() => void navigate({ search: (prev) => ({ ...prev, admissionId: undefined }) })}>
                Show all admissions
              </Button>
            )}
          </FilterBar>
        </div>
        <DataTable<IpIssueListItem>
          ariaLabel="IP issues"
          rows={issues.data?.items}
          loading={issues.isLoading}
          error={issues.error}
          onRetry={() => void issues.refetch()}
          getRowId={(i) => i.id}
          onRowClick={(i) => void navigate({ search: (prev) => ({ ...prev, id: i.id }) })}
          size="small"
          columns={[
            { key: 'when', header: 'Issued', width: 170, render: (i) => formatDateTime(i.issuedAt) },
            { key: 'no', header: 'Issue', render: (i) => <Text font="monospace">{i.number}</Text> },
            {
              key: 'patient',
              header: 'Patient',
              render: (i) => (
                <span>
                  <Text weight="semibold">{i.patientName}</Text> <Caption1>{i.uhid}</Caption1>
                </span>
              ),
            },
            {
              key: 'source',
              header: 'Against',
              priority: 'low',
              render: (i) => (i.wardIndentId ? 'Ward indent' : 'Prescription or direct'),
            },
            { key: 'lines', header: 'Lines', align: 'end', width: 70, render: (i) => i.lineCount },
            { key: 'amount', header: 'Charged', align: 'end', render: (i) => formatInr(i.totalAmount) },
            { key: 'by', header: 'By', priority: 'low', render: (i) => i.issuedBy ?? '—' },
          ]}
          emptyTitle="No issues found"
          emptyDescription="Issues appear here when the IP pharmacy sends medicines to a ward."
          page={search.page ?? 1}
          pageSize={25}
          totalCount={issues.data?.totalCount}
          onPageChange={(p) => void navigate({ search: (prev) => ({ ...prev, page: p > 1 ? p : undefined }) })}
        />
      </Section>
      {id && (
        <IssueDrawer issueId={id} onClose={() => void navigate({ search: (prev) => ({ ...prev, id: undefined }) })} />
      )}
    </Page>
  );
}

function IssueDrawer({ issueId, onClose }: { issueId: string; onClose: () => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const letterhead = useLetterhead();
  const canReturn = usePermission(P.ipIssue);
  const issue = useQuery(pharmacyQueries.ipIssue(issueId));
  const [returning, setReturning] = useState(false);
  const i = issue.data;
  const returnable = i ? i.lines.filter((l) => l.quantity - l.returnedQuantity > 0) : [];

  return (
    <OverlayDrawer open position="end" size="large" onOpenChange={(_, d) => !d.open && onClose()}>
      <DrawerHeader>
        <DrawerHeaderTitle
          action={<Button appearance="subtle" icon={<DismissRegular />} aria-label="Close" onClick={onClose} />}
        >
          {i ? `Issue ${i.number}` : 'Issue'}
        </DrawerHeaderTitle>
      </DrawerHeader>
      <DrawerBody>
        {issue.isLoading && <LoadingState />}
        {issue.error && <ErrorState error={issue.error} onRetry={() => void issue.refetch()} />}
        {i && (
          <div className={styles.stack}>
            <DetailGrid>
              <Detail label="Patient">
                {i.patient.fullName} ({i.patient.uhid})
              </Detail>
              <Detail label="Issued">{formatDateTime(i.issuedAt)}</Detail>
              <Detail label="Issued by">{i.issuedBy}</Detail>
              <Detail label="Charged to the IP account">{formatInr(i.totalAmount)}</Detail>
            </DetailGrid>
            <DataTable
              ariaLabel="Issued medicines"
              rows={i.lines}
              getRowId={(l) => l.id}
              size="small"
              columns={[
                { key: 'item', header: 'Medicine', render: (l) => <Text weight="semibold">{l.itemName}</Text> },
                { key: 'batch', header: 'Batch', render: (l) => <Text font="monospace">{l.batchNumber}</Text> },
                { key: 'exp', header: 'Expiry', priority: 'low', render: (l) => <ExpiryText date={l.expiryDate} /> },
                {
                  key: 'qty',
                  header: 'Qty',
                  align: 'end',
                  render: (l) => (
                    <span>
                      {formatQty(l.quantity)}
                      {l.returnedQuantity > 0 && (
                        <Caption1 block className={styles.muted}>
                          {formatQty(l.returnedQuantity)} returned
                        </Caption1>
                      )}
                    </span>
                  ),
                },
                { key: 'mrp', header: 'MRP', align: 'end', priority: 'low', render: (l) => formatInr(l.unitMrp) },
                { key: 'amount', header: 'Amount', align: 'end', render: (l) => formatInr(l.amount) },
              ]}
            />
            {i.returns.length > 0 && (
              <div>
                <Subtitle2 as="h3">Ward returns</Subtitle2>
                <DataTable
                  ariaLabel="Ward returns"
                  rows={i.returns}
                  getRowId={(r) => r.id}
                  size="small"
                  columns={[
                    { key: 'no', header: 'Return', render: (r) => <Text font="monospace">{r.number}</Text> },
                    { key: 'at', header: 'When', render: (r) => formatDateTime(r.returnedAt) },
                    {
                      key: 'items',
                      header: 'Items',
                      render: (r) =>
                        r.lines
                          .map(
                            (l) =>
                              `${l.itemName} × ${formatQty(l.quantity)}${l.restocked ? '' : ' (not restocked)'}${l.chargeReversal === 'Cancelled' ? ', charge cancelled' : ', credited'}`,
                          )
                          .join('; '),
                    },
                    { key: 'reason', header: 'Reason', priority: 'low', render: (r) => r.reason },
                    { key: 'credit', header: 'Credit', align: 'end', render: (r) => formatInr(r.creditAmount) },
                  ]}
                />
              </div>
            )}
          </div>
        )}
      </DrawerBody>
      <DrawerFooter>
        {i && (
          <>
            <Button
              icon={<PrintRegular />}
              onClick={() =>
                void printIpIssueSlip(i, letterhead).catch((error: unknown) =>
                  notify.error(error, 'The slip did not print'),
                )
              }
            >
              Print slip
            </Button>
            {canReturn && returnable.length > 0 && (
              <Button appearance="primary" icon={<ArrowUndoRegular />} onClick={() => setReturning(true)}>
                Take back from the ward
              </Button>
            )}
          </>
        )}
      </DrawerFooter>
      {i && returning && <WardReturnDialog issue={i} onClose={() => setReturning(false)} />}
    </OverlayDrawer>
  );
}

interface ReturnLine {
  quantity: string;
  restock: boolean;
}

function WardReturnDialog({ issue, onClose }: { issue: IpIssueDetail; onClose: () => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const today = todayIso();
  const [lines, setLines] = useState<Record<string, ReturnLine>>({});
  const [reason, setReason] = useState('');
  const items = issue.lines.filter((l) => l.quantity - l.returnedQuantity > 0);

  const planned = items.map((line) => {
    const entry = lines[line.id];
    const quantity = parseNumber(entry?.quantity ?? '') ?? 0;
    const max = toNumber(dec(line.quantity) - dec(line.returnedQuantity));
    const expired = line.expiryDate !== null && line.expiryDate < today;
    return {
      line,
      quantity,
      max,
      expired,
      restock: !expired && (entry?.restock ?? true),
      credit: quantity > 0 && quantity <= max ? ipIssueAmount(quantity, line.unitMrp) : 0,
      invalid: quantity < 0 || quantity > max,
    };
  });
  const chosen = planned.filter((p) => p.quantity > 0 && !p.invalid);
  const credit = toNumber(sum(chosen.map((p) => dec(p.credit))));
  const valid = chosen.length > 0 && !planned.some((p) => p.invalid) && reason.trim().length >= 3;

  const submit = useMutation({
    mutationFn: () =>
      pharmacyApi.wardReturn(issue.id, {
        lines: chosen.map((p) => ({ ipIssueLineId: p.line.id, quantity: p.quantity, restock: p.restock })),
        reason: reason.trim(),
      }),
    onSuccess: (result) => {
      notify.success(
        `Ward return ${result.number} recorded`,
        `${formatInr(result.creditAmount)} credited to the patient's IP account.`,
      );
      void queryClient.invalidateQueries({ queryKey: pharmacyKeys.ward });
      void queryClient.invalidateQueries({ queryKey: pharmacyKeys.stock });
      onClose();
    },
  });
  const setLine = (id: string, patch: Partial<ReturnLine>) =>
    setLines((prev) => ({
      ...prev,
      [id]: { quantity: prev[id]?.quantity ?? '', restock: prev[id]?.restock ?? true, ...patch },
    }));

  return (
    <Dialog open onOpenChange={(_, d) => !d.open && !submit.isPending && onClose()}>
      <DialogSurface style={{ maxWidth: 780, width: '95vw' }}>
        <DialogBody>
          <DialogTitle>Take back from the ward</DialogTitle>
          <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
            <Caption1>
              Sealed, in-date packs go back on the shelf. Opened or damaged ones are credited to the patient but not
              restocked. A whole line returned before billing cancels its charge; otherwise a credit line is added to
              the IP bill.
            </Caption1>
            <div>
              <div className={styles.returnRow}>
                <Caption1 className={styles.muted}>Medicine</Caption1>
                <Caption1 className={styles.muted}>Qty back</Caption1>
                <Caption1 className={styles.muted}>Back on shelf</Caption1>
                <Caption1 className={styles.muted}>Credit</Caption1>
              </div>
              {planned.map((p, index) => (
                <div key={p.line.id} className={styles.returnRow}>
                  <span>
                    <Text weight="semibold">{p.line.itemName}</Text>
                    <Caption1 block className={styles.muted}>
                      Batch {p.line.batchNumber} · up to {formatQty(p.max)}
                    </Caption1>
                  </span>
                  <Field
                    validationState={p.invalid ? 'error' : 'none'}
                    validationMessage={p.invalid ? `Max ${formatQty(p.max)}` : undefined}
                  >
                    <Input
                      type="number"
                      min={0}
                      aria-label={`Quantity of ${p.line.itemName} returned`}
                      value={lines[p.line.id]?.quantity ?? ''}
                      onChange={(_, d) => setLine(p.line.id, { quantity: d.value })}
                      autoFocus={index === 0}
                    />
                  </Field>
                  {p.expired ? (
                    <Caption1>Expired: not restocked</Caption1>
                  ) : (
                    <Checkbox
                      label="Restock"
                      checked={lines[p.line.id]?.restock ?? true}
                      onChange={(_, d) => setLine(p.line.id, { restock: d.checked === true })}
                    />
                  )}
                  <Text>{p.quantity > 0 && !p.invalid ? formatInr(p.credit) : '—'}</Text>
                </div>
              ))}
            </div>
            <Field label="Reason" required>
              <Input
                value={reason}
                maxLength={300}
                onChange={(_, d) => setReason(d.value)}
                placeholder="e.g. Patient discharged, medicine stopped"
              />
            </Field>
            {chosen.length > 0 && (
              <MessageBar intent="info">
                <MessageBarBody>{formatInr(credit)} will be credited to the in-patient account.</MessageBarBody>
              </MessageBar>
            )}
            {submit.error && (
              <MessageBar intent="error">
                <MessageBarBody>
                  {isApiError(submit.error) ? submit.error.userMessage : 'Nothing was saved.'}
                </MessageBarBody>
              </MessageBar>
            )}
          </DialogContent>
          <DialogActions>
            <Button onClick={onClose} disabled={submit.isPending}>
              Cancel
            </Button>
            <Button
              appearance="primary"
              disabled={!valid || submit.isPending}
              icon={submit.isPending ? <Spinner size="tiny" aria-hidden /> : undefined}
              onClick={() => submit.mutate()}
            >
              Record return
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}
