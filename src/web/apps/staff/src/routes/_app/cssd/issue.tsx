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
  makeStyles,
  MessageBar,
  MessageBarBody,
  Select,
  Spinner,
  Text,
  tokens,
} from '@fluentui/react-components';
import {
  AddRegular,
  BarcodeScannerRegular,
  BoxArrowUpRegular,
  CheckmarkCircleRegular,
  DeleteRegular,
  PrintRegular,
} from '@fluentui/react-icons';
import { formatDateOnly, isApiError, todayIso } from '@hc360/core';
import { DataTable, EmptyState, Page, PageHeader, Section, useLeaveGuard, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState, type FormEvent } from 'react';
import { useLetterhead } from '@/lib/letterhead';
import { toOptions, useDepartments } from '@/lib/lookups';
import { usePermission } from '@/lib/permissions';
import { cssdApi, cssdKeys, cssdQueries, type SetIssueDto, type SterilePackDto } from '@/modules/cssd/api';
import { printIssueSlip } from '@/modules/cssd/print';
import { daysToExpiry } from '@/modules/cssd/rules';

interface IssueSearch {
  requestId?: string;
  departmentId?: string;
}

export const Route = createFileRoute('/_app/cssd/issue')({
  validateSearch: (s: Record<string, unknown>): IssueSearch => ({
    requestId: typeof s.requestId === 'string' && s.requestId ? s.requestId : undefined,
    departmentId: typeof s.departmentId === 'string' && s.departmentId ? s.departmentId : undefined,
  }),
  component: IssuePacksPage,
});

const useStyles = makeStyles({
  layout: {
    display: 'grid',
    gap: '16px',
    gridTemplateColumns: 'minmax(0, 3fr) minmax(0, 2fr)',
    alignItems: 'start',
    '@media (max-width: 1100px)': { gridTemplateColumns: 'minmax(0, 1fr)' },
  },
  stack: { display: 'flex', flexDirection: 'column', gap: '12px' },
  two: {
    display: 'grid',
    gap: '12px',
    gridTemplateColumns: 'repeat(2, minmax(0, 1fr))',
    '@media (max-width: 640px)': { gridTemplateColumns: 'minmax(0, 1fr)' },
  },
  scan: { display: 'flex', gap: '8px', alignItems: 'flex-end' },
  line: {
    display: 'flex',
    gap: '8px',
    alignItems: 'center',
    justifyContent: 'space-between',
    flexWrap: 'wrap',
    padding: '6px 0',
    borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  muted: { color: tokens.colorNeutralForeground3 },
  done: { display: 'flex', flexDirection: 'column', alignItems: 'center', gap: '6px', textAlign: 'center' },
});

/** Issue sterile packs to a department (CSSD-04): scan labels or pick first-expiring packs; expired or recalled packs are refused. */
function IssuePacksPage() {
  const styles = useStyles();
  const search = Route.useSearch();
  const navigate = useNavigate();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const letterhead = useLetterhead();
  const canIssue = usePermission('cssd.process');
  const departments = useDepartments();
  const [departmentId, setDepartmentId] = useState(search.departmentId ?? '');
  const [requestId, setRequestId] = useState(search.requestId ?? '');
  const [issuedTo, setIssuedTo] = useState('');
  const [scan, setScan] = useState('');
  const [scanError, setScanError] = useState<string | null>(null);
  const [selected, setSelected] = useState<SterilePackDto[]>([]);
  const [issued, setIssued] = useState<SetIssueDto[] | null>(null);
  const available = useQuery(cssdQueries.packs({}));
  const requests = useQuery({
    ...cssdQueries.requests({ departmentId, openOnly: true }),
    enabled: Boolean(departmentId),
  });
  const request = requests.data?.find((r) => r.id === requestId) ?? null;
  const today = todayIso();
  const usable = (available.data ?? []).filter((p) => !p.isExpired && !selected.some((s) => s.id === p.id));

  const add = (pack: SterilePackDto) => {
    setSelected((current) => (current.some((p) => p.id === pack.id) ? current : [...current, pack]));
    setScanError(null);
  };

  const onScan = (event: FormEvent) => {
    event.preventDefault();
    const code = scan.trim().toUpperCase();
    if (!code) {
      return;
    }
    const pack = (available.data ?? []).find((p) => p.packNumber.toUpperCase() === code);
    if (!pack) {
      setScanError(`Pack ${code} is not on the shelf (not released, already issued or recalled).`);
    } else if (pack.isExpired) {
      setScanError(
        `Pack ${code} expired on ${pack.sterileUntil ? formatDateOnly(pack.sterileUntil) : '—'}. Send it for reprocessing.`,
      );
    } else if (selected.some((p) => p.id === pack.id)) {
      setScanError(`Pack ${code} is already in this issue.`);
    } else {
      add(pack);
    }
    setScan('');
  };

  /** First-expiry-first-out: adds the soonest-expiring packs of a set (the list comes sorted by expiry). */
  const addSoonest = (setId: string, count: number) => {
    const picks = usable.filter((p) => p.instrumentSetId === setId).slice(0, count);
    setSelected((current) => [...current, ...picks]);
    if (picks.length < count) {
      notify.warning('Not enough sterile packs', `Only ${picks.length} of ${count} are on the shelf.`);
    }
  };

  const issue = useMutation({
    mutationFn: () =>
      cssdApi.issue({
        departmentId,
        cssdRequestId: requestId || null,
        issuedToName: issuedTo.trim() || null,
        packIds: selected.map((p) => p.id),
      }),
    onSuccess: (result) => {
      setIssued(result);
      void queryClient.invalidateQueries({ queryKey: cssdKeys.all });
    },
    onError: (error) => {
      if (isApiError(error) && error.isConflict) {
        void queryClient.invalidateQueries({ queryKey: cssdKeys.all });
      }
    },
  });

  const reset = () => {
    setIssued(null);
    setSelected([]);
    setRequestId('');
    setIssuedTo('');
    issue.reset();
    void navigate({ to: '/cssd/issue', search: {} });
  };

  // Leaving the screen or closing the window with unsaved changes asks first (SRS UX-08).
  useLeaveGuard({ dirty: issued === null && (selected.length > 0 || issuedTo !== ''), busy: issue.isPending });

  if (!canIssue) {
    return (
      <Page maxWidth={720}>
        <EmptyState
          title="Only CSSD staff issue packs"
          description="Request sterile sets from the Requests page; CSSD issues them to your department."
        />
      </Page>
    );
  }

  const bySet = new Map<string, { name: string; count: number }>();
  for (const p of selected) {
    const current = bySet.get(p.instrumentSetId) ?? { name: p.setName ?? 'Set', count: 0 };
    current.count += 1;
    bySet.set(p.instrumentSetId, current);
  }
  const valid = Boolean(departmentId) && selected.length > 0 && selected.length <= 100;

  return (
    <Page>
      <PageHeader
        title="Issue sterile packs"
        icon={<BoxArrowUpRegular />}
        subtitle="Scan each pack label, or pick the soonest-expiring packs. Expired and recalled packs cannot be issued."
        breadcrumbs={[{ label: 'CSSD', onClick: () => void navigate({ to: '/cssd' }) }, { label: 'Issue' }]}
      />
      <div className={styles.layout}>
        <div className={styles.stack}>
          <Section title="To">
            <div className={styles.two}>
              <Field label="Department" required>
                <Select
                  value={departmentId}
                  onChange={(_, d) => {
                    setDepartmentId(d.value);
                    setRequestId('');
                  }}
                >
                  <option value="">Choose department</option>
                  {toOptions(departments.data).map((o) => (
                    <option key={o.value} value={o.value}>
                      {o.label}
                    </option>
                  ))}
                </Select>
              </Field>
              <Field label="Against request">
                <Select value={requestId} onChange={(_, d) => setRequestId(d.value)} disabled={!departmentId}>
                  <option value="">No request</option>
                  {requests.data?.map((r) => (
                    <option key={r.id} value={r.id}>
                      {r.requestNumber}
                      {r.priority === 'Urgent' ? ' (urgent)' : ''}
                    </option>
                  ))}
                </Select>
              </Field>
              <Field label="Received by">
                <Input
                  value={issuedTo}
                  onChange={(_, d) => setIssuedTo(d.value)}
                  maxLength={200}
                  placeholder="Name of the person collecting"
                />
              </Field>
            </div>
          </Section>

          {request && (
            <Section title={`Request ${request.requestNumber}`} description={request.notes ?? undefined}>
              {request.lines.map((line) => {
                const outstanding = Math.max(
                  0,
                  line.quantity - line.issuedQuantity - (bySet.get(line.instrumentSetId)?.count ?? 0),
                );
                return (
                  <div key={line.instrumentSetId} className={styles.line}>
                    <Text>
                      <strong>{line.setName ?? 'Set'}</strong> · asked {line.quantity}, issued {line.issuedQuantity}
                    </Text>
                    <Button
                      size="small"
                      icon={<AddRegular />}
                      disabled={outstanding === 0}
                      onClick={() => addSoonest(line.instrumentSetId, outstanding)}
                    >
                      {outstanding ? `Add ${outstanding} soonest-expiring` : 'Covered'}
                    </Button>
                  </div>
                );
              })}
            </Section>
          )}

          <Section title="Scan packs">
            <form className={styles.scan} onSubmit={onScan}>
              <Field
                label="Pack number"
                style={{ flex: 1 }}
                validationMessage={scanError ?? undefined}
                validationState={scanError ? 'error' : 'none'}
              >
                <Input
                  value={scan}
                  onChange={(_, d) => setScan(d.value)}
                  placeholder="Scan the label or type the pack number, then Enter"
                  contentBefore={<BarcodeScannerRegular />}
                  autoFocus
                  autoComplete="off"
                />
              </Field>
              <Button type="submit" disabled={!scan.trim()}>
                Add
              </Button>
            </form>
          </Section>

          <Section title="Or pick from the shelf" description="Soonest expiry first" flush>
            <DataTable
              ariaLabel="Sterile packs on the shelf"
              rows={usable}
              loading={available.isLoading}
              error={available.error}
              onRetry={() => void available.refetch()}
              getRowId={(p) => p.id}
              onRowClick={add}
              size="small"
              columns={[
                {
                  key: 'number',
                  header: 'Pack',
                  width: 190,
                  render: (p) => <Text font="monospace">{p.packNumber}</Text>,
                },
                { key: 'set', header: 'Set', render: (p) => p.setName ?? '—' },
                {
                  key: 'expiry',
                  header: 'Use by',
                  width: 150,
                  render: (p) => {
                    const days = daysToExpiry(p.sterileUntil, today);
                    return (
                      <span>
                        {p.sterileUntil ? formatDateOnly(p.sterileUntil) : '—'}{' '}
                        {days !== null && days <= 2 && (
                          <Badge size="small" appearance="tint" color="warning">
                            {days === 0 ? 'Last day' : `${days} d`}
                          </Badge>
                        )}
                      </span>
                    );
                  },
                },
              ]}
              rowActions={(p) => (
                <Button size="small" icon={<AddRegular />} aria-label={`Add ${p.packNumber}`} onClick={() => add(p)} />
              )}
              emptyTitle="No sterile packs on the shelf"
              emptyDescription="Release a sterilizer load to put packs on the shelf."
            />
          </Section>
        </div>

        <Section title={`Issue (${selected.length} packs)`}>
          <div className={styles.stack}>
            {selected.length === 0 ? (
              <Caption1 className={styles.muted}>Scanned packs appear here.</Caption1>
            ) : (
              selected.map((p) => (
                <div key={p.id} className={styles.line}>
                  <span>
                    <Text font="monospace" weight="semibold">
                      {p.packNumber}
                    </Text>
                    <Caption1 block>
                      {p.setName} · use by {p.sterileUntil ? formatDateOnly(p.sterileUntil) : '—'}
                    </Caption1>
                  </span>
                  <Button
                    size="small"
                    appearance="subtle"
                    icon={<DeleteRegular />}
                    aria-label={`Remove ${p.packNumber}`}
                    onClick={() => setSelected(selected.filter((s) => s.id !== p.id))}
                  />
                </div>
              ))
            )}
            {[...bySet.values()].length > 1 && (
              <Caption1 className={styles.muted}>
                {[...bySet.values()].map((s) => `${s.name} × ${s.count}`).join(' · ')}
              </Caption1>
            )}
            {issue.error && (
              <MessageBar intent="error" layout="multiline">
                <MessageBarBody>
                  {isApiError(issue.error) ? issue.error.userMessage : 'The packs could not be issued.'}
                </MessageBarBody>
              </MessageBar>
            )}
            <Button
              appearance="primary"
              size="large"
              icon={issue.isPending ? <Spinner size="tiny" /> : <BoxArrowUpRegular />}
              disabled={!valid || issue.isPending}
              onClick={() => issue.mutate()}
            >
              Issue {selected.length || ''} packs
            </Button>
            {!departmentId && <Caption1 className={styles.muted}>Choose the department first.</Caption1>}
          </div>
        </Section>
      </div>

      <Dialog open={issued !== null} onOpenChange={(_, d) => !d.open && reset()}>
        <DialogSurface style={{ maxWidth: 480 }}>
          <DialogBody>
            <DialogTitle>Packs issued</DialogTitle>
            <DialogContent>
              <div className={styles.done} role="status">
                <CheckmarkCircleRegular fontSize={40} color={tokens.colorPaletteGreenForeground1} />
                <Text>
                  {issued?.length} packs issued to {issued?.[0]?.departmentName ?? 'the department'}.
                </Text>
              </div>
            </DialogContent>
            <DialogActions>
              <Button
                icon={<PrintRegular />}
                onClick={() =>
                  issued &&
                  void printIssueSlip(issued, letterhead).catch((error: unknown) =>
                    notify.error(error, 'Printing failed'),
                  )
                }
              >
                Print issue slip
              </Button>
              <Button appearance="primary" onClick={reset} autoFocus>
                Issue more
              </Button>
            </DialogActions>
          </DialogBody>
        </DialogSurface>
      </Dialog>
    </Page>
  );
}
