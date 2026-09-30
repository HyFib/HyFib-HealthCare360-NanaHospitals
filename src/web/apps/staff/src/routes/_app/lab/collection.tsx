import {
  Badge,
  Button,
  Caption1,
  Field,
  Input,
  makeStyles,
  mergeClasses,
  Select,
  Spinner,
  Text,
  tokens,
} from '@fluentui/react-components';
import { BarcodeScannerRegular, DropRegular, InfoRegular } from '@fluentui/react-icons';
import { formatRelative, useHotkey } from '@hc360/core';
import { EmptyState, ErrorState, FilterBar, Page, PageHeader, Pagination, Section } from '@hc360/ui';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useRef, useState } from 'react';
import { LAB_SECTIONS, labQueries, type LabSection, type PendingCollectionItem } from '@/modules/lab/api';
import { PatientClassBadge, PriorityBadge, TubeChip } from '@/modules/lab/badges';
import { CollectDialog, type CollectTarget } from '@/modules/lab/CollectDialog';
import { searchOneOf, searchPage, searchText } from '@/modules/lab/common/search';
import { ORDER_PRIORITIES, PATIENT_CLASSES, type OrderPriority, type PatientClass } from '@/modules/lab/common/types';
import { patientClassLabel, priorityLabel, sectionLabel, specimenLabel } from '@/modules/lab/labels';
import { normalizeScan } from '@/modules/lab/scan';
import { useLabLive } from '@/modules/lab/useLabLive';

interface CollectionSearch {
  search?: string;
  section?: LabSection;
  priority?: OrderPriority;
  patientClass?: PatientClass;
  page?: number;
}

export const Route = createFileRoute('/_app/lab/collection')({
  validateSearch: (s: Record<string, unknown>): CollectionSearch => ({
    search: searchText(s.search, 60),
    section: searchOneOf(s.section, LAB_SECTIONS),
    priority: searchOneOf(s.priority, ORDER_PRIORITIES),
    patientClass: searchOneOf(s.patientClass, PATIENT_CLASSES),
    page: searchPage(s.page),
  }),
  component: CollectionPage,
});

const PAGE_SIZE = 100;

const useStyles = makeStyles({
  scan: { flex: '1 1 300px' },
  groups: { display: 'flex', flexDirection: 'column', gap: '12px' },
  card: {
    display: 'grid',
    gridTemplateColumns: 'minmax(0, 1fr) auto',
    gap: '10px 16px',
    padding: '14px 16px',
    borderRadius: tokens.borderRadiusLarge,
    backgroundColor: tokens.colorNeutralBackground1,
    boxShadow: tokens.shadow4,
    '@media (max-width: 720px)': { gridTemplateColumns: 'minmax(0, 1fr)' },
  },
  stat: { boxShadow: `inset 4px 0 0 ${tokens.colorPaletteRedBorder2}, ${tokens.shadow4}` },
  head: { display: 'flex', gap: '8px', alignItems: 'center', flexWrap: 'wrap' },
  muted: { color: tokens.colorNeutralForeground3 },
  mono: { fontFamily: tokens.fontFamilyMonospace },
  tubes: { display: 'flex', flexDirection: 'column', gap: '6px', gridColumn: '1 / -1' },
  tube: { display: 'flex', gap: '10px', alignItems: 'center', flexWrap: 'wrap' },
  prep: {
    display: 'flex',
    gap: '6px',
    alignItems: 'center',
    color: tokens.colorPaletteMarigoldForeground1,
    gridColumn: '1 / -1',
  },
  actions: { display: 'flex', gap: '8px', alignItems: 'flex-start', flexWrap: 'wrap', justifyContent: 'flex-end' },
});

interface OrderGroup {
  orderId: string;
  first: PendingCollectionItem;
  tubes: PendingCollectionItem[];
}

function groupByOrder(items: readonly PendingCollectionItem[]): OrderGroup[] {
  const groups: OrderGroup[] = [];
  for (const item of items) {
    const group = groups.find((g) => g.orderId === item.labOrderId);
    if (group) {
      group.tubes.push(item);
    } else {
      groups.push({ orderId: item.labOrderId, first: item, tubes: [item] });
    }
  }
  return groups;
}

function toTarget(group: OrderGroup): CollectTarget {
  const { first } = group;
  return {
    orderId: group.orderId,
    accessionNumber: first.accessionNumber,
    patientName: first.patientName,
    uhid: first.uhid,
    ageSex: `${first.age ?? '—'} / ${first.gender.charAt(0)}`,
    priority: group.tubes.some((t) => t.priority === 'Stat')
      ? 'Stat'
      : group.tubes.some((t) => t.priority === 'Urgent')
        ? 'Urgent'
        : 'Routine',
    tubes: group.tubes.map((tube) => ({
      sampleId: tube.sampleId,
      barcode: tube.barcode,
      container: tube.container,
      specimenType: tube.specimenType,
      section: tube.section,
      tests: tube.tests,
      preparation: tube.preparation,
      isRecollection: tube.isRecollection,
    })),
  };
}

/**
 * Phlebotomy worklist (SRS LIS-02/03): tubes waiting to be drawn, grouped per patient order with the tube plan and
 * preparation. Scan the patient's UHID wristband or the order slip and press Enter: with one match the collection
 * dialog opens straight away. STAT first, then the oldest.
 */
function CollectionPage() {
  const styles = useStyles();
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const queryClient = useQueryClient();
  const scanRef = useRef<HTMLInputElement>(null);
  const [term, setTerm] = useState(search.search ?? '');
  const [target, setTarget] = useState<CollectTarget | null>(null);

  useLabLive();
  const filters = {
    search: search.search,
    section: search.section,
    priority: search.priority,
    patientClass: search.patientClass,
    page: search.page,
  };
  const pending = useQuery(labQueries.pendingCollection(filters, PAGE_SIZE));
  const groups = groupByOrder(pending.data?.items ?? []);

  useHotkey('f2', () => scanRef.current?.focus(), { allowInInputs: true });

  const set = (patch: Partial<CollectionSearch>) =>
    void navigate({ search: (prev) => ({ ...prev, ...patch, page: undefined }), replace: true });

  /** Scanner flow: one matching order opens the collection dialog straight away. */
  const submitSearch = async () => {
    const value = normalizeScan(term);
    setTerm(value);
    set({ search: value || undefined });
    if (!value) {
      return;
    }
    try {
      const result = await queryClient.fetchQuery(
        labQueries.pendingCollection({ ...filters, search: value, page: undefined }, PAGE_SIZE),
      );
      const found = groupByOrder(result.items);
      if (found.length === 1) {
        setTarget(toTarget(found[0]!));
      }
    } catch {
      // The list shows the error with a retry.
    }
  };

  return (
    <Page>
      <PageHeader
        title="Sample collection"
        subtitle="Scan the UHID wristband, order slip or tube barcode and press Enter. F2 returns to the scan box."
        actions={
          <Button icon={<DropRegular />} onClick={() => void navigate({ to: '/lab/orders/new' })}>
            New walk-in order
          </Button>
        }
      />

      <Section>
        <FilterBar>
          <Field label="Find patient or order" className={styles.scan}>
            <Input
              ref={scanRef}
              value={term}
              onChange={(_, data) => setTerm(data.value)}
              onKeyDown={(event) => {
                if (event.key === 'Enter') {
                  event.preventDefault();
                  void submitSearch();
                } else if (event.key === 'Escape' && term) {
                  setTerm('');
                  set({ search: undefined });
                }
              }}
              contentBefore={<BarcodeScannerRegular />}
              placeholder="UHID, name, mobile, accession or barcode"
              autoFocus
              autoComplete="off"
              aria-keyshortcuts="F2"
            />
          </Field>
          <Field label="Bench">
            <Select
              value={search.section ?? ''}
              onChange={(_, data) => set({ section: (data.value || undefined) as LabSection | undefined })}
            >
              <option value="">All</option>
              {LAB_SECTIONS.map((value) => (
                <option key={value} value={value}>
                  {sectionLabel[value]}
                </option>
              ))}
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
          <Field label="Patient type">
            <Select
              value={search.patientClass ?? ''}
              onChange={(_, data) => set({ patientClass: (data.value || undefined) as PatientClass | undefined })}
            >
              <option value="">All</option>
              {PATIENT_CLASSES.map((value) => (
                <option key={value} value={value}>
                  {patientClassLabel[value]}
                </option>
              ))}
            </Select>
          </Field>
          {search.search && (
            <Button
              appearance="subtle"
              onClick={() => {
                setTerm('');
                set({ search: undefined });
              }}
            >
              Show everyone
            </Button>
          )}
        </FilterBar>
      </Section>

      {pending.error && !pending.data ? (
        <ErrorState error={pending.error} onRetry={() => void pending.refetch()} />
      ) : pending.isLoading ? (
        <Section>
          <Spinner label="Loading the collection list…" />
        </Section>
      ) : groups.length === 0 ? (
        <Section>
          <EmptyState
            icon={<DropRegular />}
            title={search.search ? 'Nobody matches' : 'No samples to collect'}
            description={
              search.search
                ? 'Check the UHID or order number. Orders from doctors appear here as soon as they are placed.'
                : 'New lab orders appear here automatically. For a walk-in patient, create a lab order first.'
            }
          />
        </Section>
      ) : (
        <div className={styles.groups} aria-label="Patients waiting for sample collection">
          <Caption1 className={styles.muted}>
            {pending.data?.totalCount ?? 0} {pending.data?.totalCount === 1 ? 'tube' : 'tubes'} waiting ·{' '}
            {groups.length} {groups.length === 1 ? 'order' : 'orders'} on this page
          </Caption1>
          {groups.map((group) => {
            const { first } = group;
            const stat = group.tubes.some((tube) => tube.priority === 'Stat');
            const preparation = [...new Set(group.tubes.flatMap((tube) => tube.preparation))];
            return (
              <section
                key={group.orderId}
                className={mergeClasses(styles.card, stat ? styles.stat : '')}
                aria-label={`${first.patientName}, ${group.tubes.length} tubes`}
              >
                <div>
                  <div className={styles.head}>
                    <Text weight="semibold" size={400}>
                      {first.patientName}
                    </Text>
                    <Caption1>
                      {first.age ?? '—'} / {first.gender.charAt(0)}
                    </Caption1>
                    <PriorityBadge
                      priority={stat ? 'Stat' : group.tubes.some((t) => t.priority === 'Urgent') ? 'Urgent' : 'Routine'}
                    />
                    <PatientClassBadge patientClass={first.patientClass} />
                    {group.tubes.some((tube) => tube.isRecollection) && (
                      <Badge color="danger" appearance="tint" shape="rounded">
                        Repeat sample
                      </Badge>
                    )}
                  </div>
                  <Caption1 className={styles.muted}>
                    UHID <span className={styles.mono}>{first.uhid}</span> · {first.accessionNumber} · ordered{' '}
                    {formatRelative(first.orderedAt)}
                  </Caption1>
                </div>
                <div className={styles.actions}>
                  <Button appearance="primary" icon={<DropRegular />} onClick={() => setTarget(toTarget(group))}>
                    Collect {group.tubes.length} {group.tubes.length === 1 ? 'tube' : 'tubes'}
                  </Button>
                </div>
                {preparation.length > 0 && (
                  <div className={styles.prep}>
                    <InfoRegular aria-hidden />
                    <Caption1>{preparation.join(' · ')}</Caption1>
                  </div>
                )}
                <div className={styles.tubes}>
                  {group.tubes.map((tube) => (
                    <div key={tube.sampleId} className={styles.tube}>
                      <TubeChip container={tube.container} detail={specimenLabel[tube.specimenType]} />
                      <Caption1 className={styles.muted}>{sectionLabel[tube.section]}</Caption1>
                      <Text>{tube.tests.join(', ')}</Text>
                    </div>
                  ))}
                </div>
              </section>
            );
          })}
          {pending.data && pending.data.totalCount > PAGE_SIZE && (
            <Pagination
              page={search.page ?? 1}
              pageSize={PAGE_SIZE}
              totalCount={pending.data.totalCount}
              onPageChange={(page) =>
                void navigate({ search: (prev) => ({ ...prev, page: page > 1 ? page : undefined }) })
              }
            />
          )}
        </div>
      )}

      {target && <CollectDialog key={target.orderId} target={target} onClose={() => setTarget(null)} />}
    </Page>
  );
}
