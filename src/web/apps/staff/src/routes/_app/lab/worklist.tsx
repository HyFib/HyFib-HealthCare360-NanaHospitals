import {
  Badge,
  Button,
  Caption1,
  Checkbox,
  Field,
  Input,
  makeStyles,
  MessageBar,
  MessageBarActions,
  MessageBarBody,
  MessageBarTitle,
  Select,
  Text,
  tokens,
} from '@fluentui/react-components';
import {
  AlertUrgentRegular,
  BarcodeScannerRegular,
  CheckmarkCircleRegular,
  ChevronRightRegular,
  LinkDismissRegular,
  ShieldErrorRegular,
} from '@fluentui/react-icons';
import { formatTime, isApiError, useHotkey } from '@hc360/core';
import { DataTable, EmptyState, FilterBar, Page, PageHeader, Section } from '@hc360/ui';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useRef, useState } from 'react';
import { usePermission } from '@/lib/permissions';
import {
  LAB_PERMISSIONS,
  LAB_SECTIONS,
  labQueries,
  type LabSection,
  type SampleStatus,
  type WorklistItem,
} from '@/modules/lab/api';
import { PriorityBadge } from '@/modules/lab/badges';
import { BulkVerifyDialog } from '@/modules/lab/BulkVerifyDialog';
import { searchId, searchOneOf, searchPage, searchText } from '@/modules/lab/common/search';
import { ORDER_PRIORITIES, type OrderPriority } from '@/modules/lab/common/types';
import { priorityLabel, sectionLabel } from '@/modules/lab/labels';
import { summarizeQc } from '@/modules/lab/qc';
import { ResultSheetPanel } from '@/modules/lab/ResultSheet';
import { classifyScan, normalizeScan } from '@/modules/lab/scan';
import { useLabLive } from '@/modules/lab/useLabLive';
import { tubeProgress } from '@/modules/lab/worklist';

const WORKLIST_STATUSES = ['Received', 'InProcess', 'Completed'] as const satisfies readonly SampleStatus[];

interface WorklistSearch {
  section?: LabSection;
  status?: (typeof WORKLIST_STATUSES)[number];
  priority?: OrderPriority;
  search?: string;
  page?: number;
  sampleId?: string;
}

export const Route = createFileRoute('/_app/lab/worklist')({
  validateSearch: (s: Record<string, unknown>): WorklistSearch => ({
    section: searchOneOf(s.section, LAB_SECTIONS),
    status: searchOneOf(s.status, WORKLIST_STATUSES),
    priority: searchOneOf(s.priority, ORDER_PRIORITIES),
    search: searchText(s.search, 40),
    page: searchPage(s.page),
    sampleId: searchId(s.sampleId),
  }),
  component: WorklistPage,
});

const SECTION_KEY = 'hc360:lab-bench';

function rememberedSection(): LabSection | undefined {
  try {
    return searchOneOf(localStorage.getItem(SECTION_KEY), LAB_SECTIONS);
  } catch {
    return undefined;
  }
}

const useStyles = makeStyles({
  layout: {
    display: 'grid',
    gridTemplateColumns: 'minmax(340px, 440px) minmax(0, 1fr)',
    gap: '16px',
    alignItems: 'start',
    '@media (max-width: 1180px)': { gridTemplateColumns: 'minmax(0, 1fr)' },
  },
  scan: { flex: '1 1 260px' },
  muted: { color: tokens.colorNeutralForeground3 },
  mono: { fontFamily: tokens.fontFamilyMonospace },
  cell: { display: 'flex', flexDirection: 'column', gap: '2px', minWidth: 0 },
  row: { display: 'flex', gap: '6px', alignItems: 'center', flexWrap: 'wrap' },
  open: { color: tokens.colorBrandForeground1 },
  listActions: { display: 'flex', gap: '8px', alignItems: 'center', padding: '0 16px 8px', flexWrap: 'wrap' },
});

/**
 * Bench worklist and result entry (SRS LIS-02/04/05/06). Scan a tube (or pick it from the list) and the result sheet
 * opens next to the list; STAT tubes and tubes with unverified critical values come first. Press F2 to go back to
 * the scan box.
 */
function WorklistPage() {
  const styles = useStyles();
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const queryClient = useQueryClient();
  const canVerify = usePermission(LAB_PERMISSIONS.verify);
  const canReceive = usePermission(LAB_PERMISSIONS.receive);
  const scanRef = useRef<HTMLInputElement>(null);
  const [term, setTerm] = useState('');
  const [scanNote, setScanNote] = useState<{ intent: 'warning' | 'error'; text: string; receive?: boolean } | null>(
    null,
  );
  const [checked, setChecked] = useState<string[]>([]);
  const [bulkOpen, setBulkOpen] = useState(false);
  const section = search.section ?? rememberedSection();

  useLabLive();
  const filters = {
    section,
    status: search.status,
    priority: search.priority,
    search: search.search,
    page: search.page,
  };
  const worklist = useQuery(labQueries.worklist(filters));
  const qc = useQuery({ ...labQueries.qcStatus(), retry: false });
  const locked = qc.data ? summarizeQc(qc.data).lockedTests : [];
  const unmatched = useQuery({ ...labQueries.unmatchedResults({}, 1), retry: false });
  const unmatchedCount = unmatched.data?.totalCount ?? 0;

  useHotkey('f2', () => scanRef.current?.focus(), { allowInInputs: true });

  const set = (patch: Partial<WorklistSearch>) =>
    void navigate({ search: (prev) => ({ ...prev, ...patch, page: undefined }), replace: true });
  const open = (sampleId: string) => void navigate({ search: (prev) => ({ ...prev, sampleId }), replace: true });

  const chooseSection = (value: LabSection | undefined) => {
    try {
      if (value) {
        localStorage.setItem(SECTION_KEY, value);
      } else {
        localStorage.removeItem(SECTION_KEY);
      }
    } catch {
      // Workstation preference only.
    }
    set({ section: value });
  };

  const handleScan = async (raw: string) => {
    const code = normalizeScan(raw);
    setTerm('');
    setScanNote(null);
    if (!code) {
      return;
    }
    const kind = classifyScan(code);
    if (kind !== 'sample' && kind !== 'labAccession') {
      setScanNote({ intent: 'warning', text: `"${code}" is not a sample barcode or lab accession number.` });
      return;
    }
    try {
      const found = await queryClient.fetchQuery(labQueries.worklist({ search: code.toUpperCase() }, 25));
      const exact = found.items.find((item) => item.barcode === code);
      if (exact) {
        open(exact.sampleId);
        return;
      }
      if (kind === 'labAccession' && found.items.length > 0) {
        void navigate({
          search: (prev) => ({
            ...prev,
            search: code.toUpperCase(),
            sampleId: found.items[0]!.sampleId,
            page: undefined,
          }),
          replace: true,
        });
        return;
      }
      if (kind === 'sample') {
        // Not in the lab list: explain why (not received yet, rejected), or open a finished tube read-only.
        const sample = await queryClient.fetchQuery(labQueries.sampleByBarcode(code));
        if (sample.status === 'Completed') {
          open(sample.id);
        } else if (sample.status === 'Collected') {
          setScanNote({
            intent: 'warning',
            text: `Sample ${code} (${sample.patientName}) has not been received in the lab yet.`,
            receive: canReceive,
          });
        } else if (sample.status === 'Pending') {
          setScanNote({
            intent: 'warning',
            text: `Sample ${code} (${sample.patientName}) has not been collected yet.`,
          });
        } else {
          setScanNote({
            intent: 'error',
            text: `Sample ${code} (${sample.patientName}) was ${sample.status === 'Rejected' ? 'rejected' : 'cancelled'}. Enter results on the new sample.`,
          });
        }
        return;
      }
      setScanNote({ intent: 'warning', text: `No sample in the lab has accession ${code}.` });
    } catch (error) {
      setScanNote({
        intent: 'error',
        text:
          isApiError(error) && error.status === 404
            ? `No sample has barcode ${code}. Check the label.`
            : isApiError(error)
              ? error.userMessage
              : 'The sample could not be found.',
      });
    }
  };

  const items = worklist.data?.items;
  const checkable = (item: WorklistItem) => canVerify && item.entered > 0;
  const visibleChecked = checked.filter((id) => items?.some((item) => item.sampleId === id && checkable(item)));

  return (
    <Page>
      <PageHeader
        title="Result entry"
        subtitle="Scan a sample or pick it from the list. F2 returns to the scan box; in the sheet, Tab or Enter moves down and Ctrl+S saves."
        actions={
          canVerify && (
            <Button
              appearance="primary"
              icon={<CheckmarkCircleRegular />}
              disabled={visibleChecked.length === 0}
              onClick={() => setBulkOpen(true)}
            >
              Verify normal results{visibleChecked.length ? ` (${visibleChecked.length} samples)` : ''}
            </Button>
          )
        }
      />

      {locked.length > 0 && (
        <MessageBar intent="warning" icon={<ShieldErrorRegular />} layout="multiline">
          <MessageBarBody>
            <MessageBarTitle>QC failed today</MessageBarTitle>
            Verification is locked for {locked.join(', ')} until QC passes or a pathologist records an override.
          </MessageBarBody>
          <MessageBarActions>
            <Button size="small" onClick={() => void navigate({ to: '/lab/qc' })}>
              Open quality control
            </Button>
          </MessageBarActions>
        </MessageBar>
      )}

      {unmatchedCount > 0 && (
        <MessageBar intent="warning" icon={<LinkDismissRegular />} layout="multiline">
          <MessageBarBody>
            <MessageBarTitle>
              {unmatchedCount === 1
                ? 'An analyser result is waiting to be matched'
                : `${unmatchedCount} analyser results are waiting to be matched`}
            </MessageBarTitle>
            HC360 could not put {unmatchedCount === 1 ? 'it' : 'them'} on a sample&apos;s test. Assign each to the right
            test, or discard it with the reason.
          </MessageBarBody>
          <MessageBarActions>
            <Button size="small" onClick={() => void navigate({ to: '/lab/unmatched' })}>
              Match results
            </Button>
          </MessageBarActions>
        </MessageBar>
      )}

      <Section>
        <FilterBar>
          <Field label="Scan sample" className={styles.scan} hint="Barcode or accession number, then Enter">
            <Input
              ref={scanRef}
              value={term}
              onChange={(_, data) => setTerm(data.value)}
              onKeyDown={(event) => {
                if (event.key === 'Enter') {
                  event.preventDefault();
                  void handleScan(term);
                }
              }}
              contentBefore={<BarcodeScannerRegular />}
              placeholder="Scan or type, then Enter"
              autoFocus
              autoComplete="off"
              aria-keyshortcuts="F2"
            />
          </Field>
          <Field label="Bench">
            <Select
              value={section ?? ''}
              onChange={(_, data) => chooseSection((data.value || undefined) as LabSection | undefined)}
            >
              <option value="">All benches</option>
              {LAB_SECTIONS.map((value) => (
                <option key={value} value={value}>
                  {sectionLabel[value]}
                </option>
              ))}
            </Select>
          </Field>
          <Field label="Samples">
            <Select
              value={search.status ?? ''}
              onChange={(_, data) => set({ status: (data.value || undefined) as WorklistSearch['status'] })}
            >
              <option value="">In the lab</option>
              <option value="Received">Received, not started</option>
              <option value="InProcess">In process</option>
              <option value="Completed">Completed</option>
            </Select>
          </Field>
          <Field label="Priority">
            <Select
              value={search.priority ?? ''}
              onChange={(_, data) => set({ priority: (data.value || undefined) as OrderPriority | undefined })}
            >
              <option value="">All</option>
              {ORDER_PRIORITIES.map((value) => (
                <option key={value} value={value}>
                  {priorityLabel[value]}
                </option>
              ))}
            </Select>
          </Field>
          {search.search && (
            <Button appearance="subtle" onClick={() => set({ search: undefined })}>
              Clear search "{search.search}"
            </Button>
          )}
        </FilterBar>
        {scanNote && (
          <MessageBar intent={scanNote.intent} style={{ marginTop: 12 }}>
            <MessageBarBody>{scanNote.text}</MessageBarBody>
            {scanNote.receive && (
              <MessageBarActions>
                <Button size="small" onClick={() => void navigate({ to: '/lab/receive' })}>
                  Receive samples
                </Button>
              </MessageBarActions>
            )}
          </MessageBar>
        )}
      </Section>

      <div className={styles.layout}>
        <Section
          title={`Samples${worklist.data ? ` (${worklist.data.totalCount})` : ''}`}
          description="STAT first, then critical values, then by receipt time"
          flush
        >
          {canVerify && items && items.some(checkable) && (
            <div className={styles.listActions}>
              <Button
                size="small"
                appearance="subtle"
                onClick={() =>
                  setChecked(visibleChecked.length ? [] : items.filter(checkable).map((item) => item.sampleId))
                }
              >
                {visibleChecked.length ? 'Clear selection' : 'Select samples with results to verify'}
              </Button>
            </div>
          )}
          <DataTable
            ariaLabel="Samples in the lab"
            rows={items}
            loading={worklist.isLoading}
            error={worklist.error}
            onRetry={() => void worklist.refetch()}
            getRowId={(item) => item.sampleId}
            onRowClick={(item) => open(item.sampleId)}
            size="small"
            columns={[
              ...(canVerify
                ? [
                    {
                      key: 'select',
                      header: '',
                      width: 36,
                      render: (item: WorklistItem) =>
                        checkable(item) ? (
                          <span
                            onClick={(event) => event.stopPropagation()}
                            onKeyDown={(event) => event.stopPropagation()}
                          >
                            <Checkbox
                              checked={visibleChecked.includes(item.sampleId)}
                              onChange={(_, data) =>
                                setChecked((current) =>
                                  data.checked
                                    ? [...current, item.sampleId]
                                    : current.filter((id) => id !== item.sampleId),
                                )
                              }
                              aria-label={`Select ${item.patientName} for bulk verification`}
                            />
                          </span>
                        ) : null,
                    },
                  ]
                : []),
              {
                key: 'sample',
                header: 'Sample',
                render: (item: WorklistItem) => (
                  <span className={styles.cell}>
                    <span className={styles.row}>
                      {item.sampleId === search.sampleId && (
                        <ChevronRightRegular className={styles.open} aria-label="Open" />
                      )}
                      <Text weight="semibold">{item.patientName}</Text>
                      <PriorityBadge priority={item.priority} />
                      {item.critical > 0 && (
                        <Badge color="danger" icon={<AlertUrgentRegular />} shape="rounded" size="small">
                          {item.critical} critical
                        </Badge>
                      )}
                    </span>
                    <Caption1 className={styles.muted}>
                      <span className={styles.mono}>{item.barcode}</span> · {item.age ?? '—'}/{item.gender.charAt(0)} ·{' '}
                      {sectionLabel[item.section]}
                    </Caption1>
                    <Caption1>{item.tests.join(', ')}</Caption1>
                  </span>
                ),
              },
              {
                key: 'progress',
                header: 'Results',
                width: 120,
                render: (item: WorklistItem) => (
                  <span className={styles.cell}>
                    <Caption1>{tubeProgress(item)}</Caption1>
                    <Caption1 className={styles.muted}>
                      {item.receivedAt ? `In at ${formatTime(item.receivedAt)}` : ''}
                    </Caption1>
                  </span>
                ),
              },
            ]}
            emptyTitle={search.search ? 'No sample in the lab matches' : 'No samples waiting'}
            emptyDescription={
              search.search
                ? 'Clear the search or check the barcode.'
                : 'Samples appear here as soon as they are received in the lab.'
            }
            page={search.page ?? 1}
            pageSize={25}
            totalCount={worklist.data?.totalCount}
            onPageChange={(page) =>
              void navigate({ search: (prev) => ({ ...prev, page: page > 1 ? page : undefined }) })
            }
          />
        </Section>

        {search.sampleId ? (
          <ResultSheetPanel sampleId={search.sampleId} onScanBarcode={(barcode) => void handleScan(barcode)} />
        ) : (
          <Section>
            <EmptyState
              icon={<BarcodeScannerRegular />}
              title="Scan a sample to enter results"
              description="Or pick a sample from the list. The result sheet shows the reference range, flags and the previous result for each test."
            />
          </Section>
        )}
      </div>

      {bulkOpen && (
        <BulkVerifyDialog
          sampleIds={visibleChecked}
          onClose={() => setBulkOpen(false)}
          onDone={() => {
            setBulkOpen(false);
            setChecked([]);
          }}
        />
      )}
    </Page>
  );
}
