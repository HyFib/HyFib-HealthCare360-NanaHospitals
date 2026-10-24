import {
  Badge,
  Button,
  Caption1,
  Checkbox,
  Field,
  Input,
  makeStyles,
  mergeClasses,
  MessageBar,
  MessageBarActions,
  MessageBarBody,
  MessageBarTitle,
  Spinner,
  Text,
  tokens,
} from '@fluentui/react-components';
import {
  AddRegular,
  ArrowLeftRegular,
  BedRegular,
  DismissCircleRegular,
  DocumentRegular,
  PersonAddRegular,
  ReceiptAddRegular,
  SearchRegular,
  SettingsRegular,
} from '@fluentui/react-icons';
import { formatDate, formatDateTime, formatInr, humanize, todayIso, useHotkey, type PagedResult } from '@hc360/core';
import { EmptyState, ErrorState, LoadingState, Page, PageHeader, Section, StatusBadge, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useRef, useState } from 'react';
import { api } from '@/lib/api';
import { usePermission } from '@/lib/permissions';
import {
  billingApi,
  billingQueries,
  existingDraftId,
  type BillDetail,
  type BillListItem,
  type ChargeDto,
} from '@/modules/billing/api';
import { BillingPatientBanner } from '@/modules/billing/BillingPatientBanner';
import { AddChargeButton, CancelChargeDialog, type CancellableCharge } from '@/modules/billing/ChargeDialogs';
import { BillWorkspace } from '@/modules/billing/BillWorkspace';
import { CounterSettingsDialog } from '@/modules/billing/CounterSettingsDialog';
import { useBillingLive, useCanManageCharges } from '@/modules/billing/hooks';
import { SERVICE_CATEGORY_LABEL } from '@/modules/billing/labels';
import { sumRupees } from '@/modules/billing/money';
import { idParam } from '@/modules/billing/searchParams';
import { ShiftBar } from '@/modules/billing/ShiftControls';
import { ActionError, Amount, BillStatusBadge, KeyHint, NoAccess } from '@/modules/billing/ui';
import { patientQueries, type VisitListItem } from '@/modules/patients/api';
import { PatientPicker, type PickedPatient } from '@/modules/patients/PatientPicker';

interface OpSearch {
  patientId?: string;
  visitId?: string;
  billId?: string;
}

export const Route = createFileRoute('/_app/billing/op')({
  validateSearch: (s: Record<string, unknown>): OpSearch => ({
    patientId: idParam(s.patientId),
    visitId: idParam(s.visitId),
    billId: idParam(s.billId),
  }),
  component: OpBillingPage,
});

const useStyles = makeStyles({
  find: {
    display: 'grid',
    gridTemplateColumns: 'minmax(0, 2fr) minmax(200px, 1fr)',
    gap: '12px',
    alignItems: 'end',
    '@media (max-width: 800px)': { gridTemplateColumns: '1fr' },
  },
  cards: { display: 'flex', flexDirection: 'column', gap: '12px' },
  card: {
    display: 'flex',
    flexDirection: 'column',
    gap: '10px',
    padding: '12px 14px',
    borderRadius: tokens.borderRadiusLarge,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    backgroundColor: tokens.colorNeutralBackground1,
  },
  highlight: { border: `2px solid ${tokens.colorBrandStroke1}` },
  cardHead: {
    display: 'flex',
    justifyContent: 'space-between',
    gap: '12px',
    alignItems: 'flex-start',
    flexWrap: 'wrap',
  },
  charges: { display: 'flex', flexDirection: 'column', gap: '4px' },
  charge: { display: 'grid', gridTemplateColumns: 'minmax(0, 1fr) auto', gap: '12px', alignItems: 'center' },
  chargeWithAction: { gridTemplateColumns: 'minmax(0, 1fr) auto auto' },
  foot: { display: 'flex', justifyContent: 'space-between', alignItems: 'center', gap: '12px', flexWrap: 'wrap' },
  muted: { color: tokens.colorNeutralForeground3 },
  back: { display: 'flex', alignItems: 'center', gap: '12px', flexWrap: 'wrap' },
});

/** OP billing counter (OPB-01..07): find the patient, pick the visit's unbilled services, discount, finalise, collect, print. */
function OpBillingPage() {
  const styles = useStyles();
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const notify = useNotify();
  const canCreate = usePermission('billing.op.create');
  const pickerRef = useRef<HTMLDivElement>(null);
  const [settings, setSettings] = useState(false);
  const [lookupText, setLookupText] = useState('');
  useBillingLive();

  const patient = useQuery({ ...patientQueries.detail(search.patientId ?? ''), enabled: Boolean(search.patientId) });
  const picked: PickedPatient | null = patient.data
    ? { id: patient.data.id, uhid: patient.data.uhid, fullName: patient.data.fullName }
    : null;

  const newBill = () => {
    void navigate({ search: {} });
    setLookupText('');
    window.setTimeout(() => pickerRef.current?.querySelector('input')?.focus(), 0);
  };
  useHotkey('alt+n', newBill, { enabled: canCreate });

  const lookup = useMutation({
    mutationFn: async (text: string) => {
      const term = text.trim().toUpperCase();
      const bills = await api.get<PagedResult<BillListItem>>('/api/v1/billing/bills', {
        query: { search: term, pageSize: 5 },
      });
      const bill = bills.items.find((b) => b.billNumber === term);
      if (bill) {
        return { patientId: bill.patientId, billId: bill.id } as OpSearch;
      }
      const visits = await api.get<PagedResult<VisitListItem>>('/api/v1/visits', {
        query: { search: term, from: todayIso(-60), to: todayIso(), pageSize: 5 },
      });
      const visit = visits.items.find((v) => v.visitNumber.toUpperCase() === term);
      return visit ? ({ patientId: visit.patientId, visitId: visit.id } as OpSearch) : null;
    },
    onSuccess: (found) => {
      if (found) {
        void navigate({ search: found });
      } else {
        notify.warning(
          'No visit or bill with that number',
          'Check the number, or find the patient by UHID, mobile or name.',
        );
      }
    },
    onError: (error) => notify.error(error, 'Search failed'),
  });

  if (!canCreate) {
    return (
      <Page>
        <PageHeader title="OP billing" />
        <NoAccess what="OP billing" />
      </Page>
    );
  }

  return (
    <Page>
      <PageHeader
        title="OP billing"
        subtitle="Find the patient, bill the visit, collect and print"
        icon={<ReceiptAddRegular />}
        actions={
          <>
            <Button icon={<SettingsRegular />} onClick={() => setSettings(true)}>
              Counter settings
            </Button>
            <Button appearance="primary" icon={<PersonAddRegular />} onClick={newBill}>
              Next patient
              <KeyHint>Alt+N</KeyHint>
            </Button>
          </>
        }
      />
      <ShiftBar />

      <Section>
        <div className={styles.find}>
          <div ref={pickerRef}>
            <PatientPicker
              value={picked}
              onChange={(p) => void navigate({ search: p ? { patientId: p.id } : {} })}
              label="Patient"
              hint="UHID, 10-digit mobile number or name (3+ characters)"
            />
          </div>
          <form
            onSubmit={(event) => {
              event.preventDefault();
              if (lookupText.trim().length >= 4 && !lookup.isPending) {
                lookup.mutate(lookupText);
              }
            }}
          >
            <Field label="Or visit / bill number" hint="Press Enter to open">
              <Input
                value={lookupText}
                onChange={(_, data) => setLookupText(data.value)}
                contentAfter={lookup.isPending ? <Spinner size="extra-tiny" aria-hidden /> : <SearchRegular />}
                placeholder="e.g. NHOB260000123"
                aria-label="Visit or bill number"
              />
            </Field>
          </form>
        </div>
      </Section>

      {!search.patientId ? (
        <EmptyState
          icon={<ReceiptAddRegular />}
          title="Start with the patient"
          description="Search by UHID, mobile number or name, or type the visit or bill number from the patient's slip."
        />
      ) : (
        <>
          <BillingPatientBanner patientId={search.patientId} />
          {search.billId ? (
            <BillView
              patientId={search.patientId}
              billId={search.billId}
              onBack={() => void navigate({ search: { patientId: search.patientId } })}
            />
          ) : (
            <VisitChooser
              patientId={search.patientId}
              uhid={patient.data?.uhid ?? null}
              highlightVisitId={search.visitId ?? null}
              onOpenBill={(billId) => void navigate({ search: { patientId: search.patientId, billId } })}
            />
          )}
        </>
      )}
      <CounterSettingsDialog open={settings} onClose={() => setSettings(false)} />
    </Page>
  );
}

function BillView({ patientId, billId, onBack }: { patientId: string; billId: string; onBack: () => void }) {
  const styles = useStyles();
  const bill = useQuery({
    ...billingQueries.bill(billId),
    refetchInterval: (query) => (query.state.data?.pendingDiscount ? 15_000 : false),
  });

  if (bill.isLoading) {
    return <LoadingState label="Opening the bill…" />;
  }
  if (bill.error || !bill.data) {
    return <ErrorState error={bill.error} onRetry={() => void bill.refetch()} />;
  }
  const b = bill.data;
  if (b.patientId !== patientId) {
    return (
      <EmptyState
        title="This bill belongs to another patient"
        description="Search for the patient again to continue."
      />
    );
  }
  return (
    <>
      <div className={styles.back}>
        <Button appearance="subtle" icon={<ArrowLeftRegular />} onClick={onBack}>
          All visits and bills
        </Button>
        <Text size={500} weight="semibold">
          {b.billNumber ? `Bill ${b.billNumber}` : 'Draft bill'}
        </Text>
        <BillStatusBadge status={b.status} />
        {b.type === 'InPatient' && <Badge appearance="outline">In-patient</Badge>}
        {!b.visitId && b.type === 'OutPatient' && <Badge appearance="outline">Services without a visit</Badge>}
      </div>
      <BillWorkspace bill={b} onDiscarded={onBack} />
    </>
  );
}

interface VisitGroup {
  key: string;
  visitId: string | null;
  visit: VisitListItem | null;
  charges: ChargeDto[];
  draft: BillListItem | null;
}

/** Visits of the patient with their unbilled services, open drafts and unpaid bills: pick one and start billing. */
function VisitChooser({
  patientId,
  uhid,
  highlightVisitId,
  onOpenBill,
}: {
  patientId: string;
  uhid: string | null;
  highlightVisitId: string | null;
  onOpenBill: (billId: string) => void;
}) {
  const styles = useStyles();
  const navigate = useNavigate();
  const charges = useQuery(billingQueries.unbilledCharges(patientId));
  const drafts = useQuery(billingQueries.bills({ patientId, status: 'Draft', pageSize: 50 }));
  const visits = useQuery(patientQueries.visits(patientId, 1));
  const dues = useQuery(billingQueries.dues(patientId));

  if (charges.isLoading || drafts.isLoading || visits.isLoading) {
    return <LoadingState label="Looking for services to bill…" />;
  }
  const error = charges.error ?? drafts.error ?? visits.error;
  if (error) {
    return (
      <ErrorState
        error={error}
        onRetry={() => {
          void charges.refetch();
          void drafts.refetch();
          void visits.refetch();
        }}
      />
    );
  }

  const allCharges = charges.data?.items ?? [];
  const ipCharges = allCharges.filter((c) => c.admissionId);
  const opCharges = allCharges.filter((c) => !c.admissionId);
  const draftBills = (drafts.data?.items ?? []).filter((b) => b.type === 'OutPatient');
  const recentVisits = (visits.data?.items ?? []).filter((v) => v.type !== 'InPatient' && v.status !== 'Cancelled');

  const groups = new Map<string, VisitGroup>();
  for (const visit of recentVisits) {
    groups.set(visit.id, { key: visit.id, visitId: visit.id, visit, charges: [], draft: null });
  }
  for (const charge of opCharges) {
    const key = charge.visitId ?? 'no-visit';
    const group = groups.get(key) ?? { key, visitId: charge.visitId, visit: null, charges: [], draft: null };
    group.charges.push(charge);
    groups.set(key, group);
  }
  for (const draft of draftBills) {
    const key = draft.visitId ?? 'no-visit';
    const group = groups.get(key) ?? { key, visitId: draft.visitId, visit: null, charges: [], draft: null };
    group.draft = draft;
    groups.set(key, group);
  }
  // Visits with something to bill first (the highlighted one on top), then the rest by date.
  const ordered = [...groups.values()].sort((a, b) => {
    const score = (g: VisitGroup) =>
      (g.visitId === highlightVisitId ? 3 : 0) + (g.draft ? 2 : 0) + (g.charges.length > 0 ? 1 : 0);
    return score(b) - score(a) || (b.visit?.visitedAt ?? '').localeCompare(a.visit?.visitedAt ?? '');
  });
  const unpaid = dues.data?.bills ?? [];

  return (
    <>
      {ipCharges.length > 0 && (
        <MessageBar intent="info">
          <MessageBarBody>
            <MessageBarTitle>In-patient services</MessageBarTitle>
            {ipCharges.length} service{ipCharges.length === 1 ? '' : 's'} of an admission are billed from the IP
            account, not here.
          </MessageBarBody>
          <MessageBarActions>
            <Button
              icon={<BedRegular />}
              onClick={() => void navigate({ to: '/billing/ip', search: { search: uhid ?? undefined } })}
            >
              IP accounts
            </Button>
          </MessageBarActions>
        </MessageBar>
      )}

      {unpaid.length > 0 && (
        <Section title="Unpaid bills" description="Collect the balance before or together with today's bill">
          <div className={styles.cards}>
            {unpaid.map((bill) => (
              <div key={bill.id} className={styles.foot}>
                <span>
                  <Text weight="semibold">{bill.billNumber}</Text> <BillStatusBadge status={bill.status} />
                  <Caption1 block className={styles.muted}>
                    {formatDateTime(bill.finalizedAt ?? bill.createdAt)} · bill total {formatInr(bill.netAmount)}
                  </Caption1>
                </span>
                <Button onClick={() => onOpenBill(bill.id)}>Collect {formatInr(bill.dueAmount)}</Button>
              </div>
            ))}
          </div>
        </Section>
      )}

      <Section
        title="Visits and services to bill"
        description="Unbilled services are grouped by visit. Untick anything the patient is not paying for today."
      >
        {ordered.length === 0 ? (
          <EmptyState
            icon={<DocumentRegular />}
            title="Nothing to bill yet"
            description="This patient has no recent visit or unbilled service. Start a bill without a visit to add services directly."
            action={
              <>
                <PatientLevelStart patientId={patientId} onOpenBill={onOpenBill} />{' '}
                <AddChargeButton
                  target={{ patientId, visitId: null, admissionId: null, label: 'Services without a visit' }}
                />
              </>
            }
          />
        ) : (
          <div className={styles.cards}>
            {ordered.map((group) => (
              <VisitCard
                key={group.key}
                patientId={patientId}
                group={group}
                highlighted={group.visitId !== null && group.visitId === highlightVisitId}
                onOpenBill={onOpenBill}
              />
            ))}
            {!groups.has('no-visit') && (
              <div>
                <PatientLevelStart patientId={patientId} onOpenBill={onOpenBill} subtle />
              </div>
            )}
          </div>
        )}
      </Section>
    </>
  );
}

function useStartBill(onOpenBill: (billId: string) => void) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (request: { visitId: string | null; patientId: string | null; chargeIds: string[] | null }) =>
      billingApi.createBill(request),
    onSuccess: (detail: BillDetail) => {
      queryClient.setQueryData(billingQueries.bill(detail.id).queryKey, detail);
      void queryClient.invalidateQueries({ queryKey: ['billing', 'charges'] });
      onOpenBill(detail.id);
    },
    onError: (error) => {
      const draftId = existingDraftId(error);
      if (draftId) {
        onOpenBill(draftId);
      }
    },
  });
}

function PatientLevelStart({
  patientId,
  onOpenBill,
  subtle,
}: {
  patientId: string;
  onOpenBill: (billId: string) => void;
  subtle?: boolean;
}) {
  const start = useStartBill(onOpenBill);
  return (
    <>
      <Button
        appearance={subtle ? 'transparent' : 'primary'}
        icon={start.isPending ? <Spinner size="tiny" aria-hidden /> : <AddRegular />}
        onClick={() => start.mutate({ visitId: null, patientId, chargeIds: null })}
        disabled={start.isPending}
      >
        Start a bill without a visit
      </Button>
      <ActionError error={existingDraftId(start.error) ? null : start.error} />
    </>
  );
}

function VisitCard({
  patientId,
  group,
  highlighted,
  onOpenBill,
}: {
  patientId: string;
  group: VisitGroup;
  highlighted: boolean;
  onOpenBill: (billId: string) => void;
}) {
  const styles = useStyles();
  const start = useStartBill(onOpenBill);
  const canManageCharges = useCanManageCharges();
  const [cancelling, setCancelling] = useState<CancellableCharge | null>(null);
  const [excluded, setExcluded] = useState<ReadonlySet<string>>(() => new Set());
  const selected = group.charges.filter((c) => !excluded.has(c.id));
  const selectedTotal = sumRupees(selected.map((c) => c.netAmount));
  const visit = group.visit;
  const title = visit ? `Visit ${visit.visitNumber}` : group.visitId ? 'Earlier visit' : 'Services without a visit';

  const toggle = (chargeId: string, checked: boolean) =>
    setExcluded((current) => {
      const next = new Set(current);
      if (checked) {
        next.delete(chargeId);
      } else {
        next.add(chargeId);
      }
      return next;
    });

  const begin = () =>
    start.mutate({
      visitId: group.visitId,
      patientId: group.visitId ? null : patientId,
      chargeIds: excluded.size > 0 ? selected.map((c) => c.id) : null,
    });

  return (
    <div className={mergeClasses(styles.card, highlighted && styles.highlight)}>
      <div className={styles.cardHead}>
        <span>
          <Text weight="semibold">{title}</Text>
          {visit && (
            <Caption1 block className={styles.muted}>
              {formatDateTime(visit.visitedAt)} · {humanize(visit.type)} · {visit.departmentName ?? 'No department'}
              {visit.practitionerName ? ` · ${visit.practitionerName}` : ''}
              {visit.isFreeFollowUp ? ' · free follow-up' : ''}
            </Caption1>
          )}
        </span>
        <span>
          {visit && <StatusBadge status={visit.status} />}{' '}
          {group.draft && (
            <Badge appearance="tint" color="informative">
              Draft bill open
            </Badge>
          )}
        </span>
      </div>

      {group.charges.length > 0 ? (
        <div className={styles.charges} role="group" aria-label="Unbilled services">
          {group.charges.map((charge) => (
            <div key={charge.id} className={mergeClasses(styles.charge, canManageCharges && styles.chargeWithAction)}>
              <Checkbox
                checked={!excluded.has(charge.id)}
                onChange={(_, data) => toggle(charge.id, Boolean(data.checked))}
                disabled={Boolean(group.draft)}
                label={
                  <span>
                    {charge.description}
                    {charge.quantity < 0 && (
                      <Badge size="small" appearance="tint" color="danger" style={{ marginLeft: 6 }}>
                        Return
                      </Badge>
                    )}
                    <Caption1 block className={styles.muted}>
                      {charge.quantity} × {formatInr(charge.unitPrice)} · {SERVICE_CATEGORY_LABEL[charge.category]}
                      {charge.practitionerName ? ` · ${charge.practitionerName}` : ''} ·{' '}
                      {formatDate(charge.serviceDate)}
                    </Caption1>
                  </span>
                }
              />
              <Amount value={charge.netAmount} />
              {canManageCharges && (
                <Button
                  size="small"
                  appearance="subtle"
                  icon={<DismissCircleRegular />}
                  aria-label={`Cancel ${charge.description}`}
                  title="Cancel this charge"
                  onClick={() =>
                    setCancelling({
                      id: charge.id,
                      description: charge.description,
                      netAmount: charge.netAmount,
                      sourceModule: charge.sourceModule,
                      onDraft: charge.billId !== null,
                    })
                  }
                />
              )}
            </div>
          ))}
        </div>
      ) : (
        !group.draft && (
          <Caption1 className={styles.muted}>
            No unbilled services on this visit. You can still start a bill and add services.
          </Caption1>
        )
      )}

      <div className={styles.foot}>
        <Text>
          {group.charges.length > 0 && !group.draft && (
            <>
              {selected.length} of {group.charges.length} selected · <Amount value={selectedTotal} strong />
            </>
          )}
        </Text>
        <span className={styles.back}>
          {!group.draft && (
            <AddChargeButton target={{ patientId, visitId: group.visitId, admissionId: null, label: title }} />
          )}
          {group.draft ? (
            <Button appearance="primary" onClick={() => onOpenBill(group.draft!.id)} autoFocus={highlighted}>
              Continue the draft bill
            </Button>
          ) : (
            <Button
              appearance={highlighted || group.charges.length > 0 ? 'primary' : 'secondary'}
              icon={start.isPending ? <Spinner size="tiny" aria-hidden /> : <ReceiptAddRegular />}
              onClick={begin}
              disabled={start.isPending || (group.charges.length > 0 && selected.length === 0)}
              autoFocus={highlighted}
            >
              Start bill
            </Button>
          )}
        </span>
      </div>
      <ActionError error={existingDraftId(start.error) ? null : start.error} />
      <CancelChargeDialog charge={cancelling} onClose={() => setCancelling(null)} />
    </div>
  );
}
