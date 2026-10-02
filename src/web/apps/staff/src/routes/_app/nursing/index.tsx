import {
  Badge,
  Button,
  Caption1,
  Field,
  makeStyles,
  mergeClasses,
  SearchBox,
  Select,
  Skeleton,
  SkeletonItem,
  Tab,
  TabList,
  Text,
  tokens,
} from '@fluentui/react-components';
import {
  ArrowLeftRegular,
  BedRegular,
  ClipboardPulseRegular,
  OpenRegular,
  PeopleRegular,
  PillRegular,
} from '@fluentui/react-icons';
import { formatDateOnly, genderShort } from '@hc360/core';
import { EmptyState, ErrorState, Page, PageHeader, Section } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { PatientBannerWithFlags } from '@/modules/emr/PatientBannerWithFlags';
import { inpatientQueries, PAYER_LABEL, type BedBoardItem, type BedOccupant } from '@/modules/inpatient/api';
import { bedAlertBadge, bedAlertInfo } from '@/modules/inpatient/bedState';
import { BedAlertBadges } from '@/modules/inpatient/BedTile';
import { useStoredState, useWardRealtime } from '@/modules/inpatient/hooks';
import { Assessments } from '@/modules/inpatient/nursing/Assessments';
import { PatientHandovers, WardHandoverSheet } from '@/modules/inpatient/nursing/Handovers';
import { IntakeOutput } from '@/modules/inpatient/nursing/IntakeOutput';
import { MedicationOrders } from '@/modules/inpatient/nursing/MedicationOrders';
import { NursingNotes } from '@/modules/inpatient/nursing/NursingNotes';
import { PatientMar } from '@/modules/inpatient/nursing/PatientMar';
import { WardMarDue } from '@/modules/inpatient/nursing/WardMarDue';
import { nursingQueries } from '@/modules/inpatient/nursingApi';
import { dutyDate, SHIFT_LABEL, shiftAt } from '@/modules/inpatient/time';

type ChartTab = 'mar' | 'medicines' | 'notes' | 'handover' | 'io' | 'risk';
const CHART_TABS: ChartTab[] = ['mar', 'medicines', 'notes', 'handover', 'io', 'risk'];

interface NursingSearch {
  wardId?: string;
  admissionId?: string;
  tab?: ChartTab;
}

export const Route = createFileRoute('/_app/nursing/')({
  validateSearch: (s: Record<string, unknown>): NursingSearch => ({
    wardId: typeof s.wardId === 'string' && s.wardId ? s.wardId : undefined,
    admissionId: typeof s.admissionId === 'string' && s.admissionId ? s.admissionId : undefined,
    tab: CHART_TABS.includes(s.tab as ChartTab) ? (s.tab as ChartTab) : undefined,
  }),
  component: NursingStationPage,
});

const WARD_KEY = 'hc360:nursing:ward';

const useStyles = makeStyles({
  layout: {
    display: 'grid',
    gridTemplateColumns: 'minmax(260px, 320px) minmax(0, 1fr)',
    gap: '16px',
    alignItems: 'start',
    '@media (max-width: 1000px)': { gridTemplateColumns: 'minmax(0, 1fr)' },
  },
  list: {
    display: 'flex',
    flexDirection: 'column',
    gap: '6px',
    position: 'sticky',
    top: '12px',
    maxHeight: 'calc(100vh - 140px)',
    overflowY: 'auto',
    padding: '2px',
  },
  hideOnNarrow: { '@media (max-width: 1000px)': { display: 'none' } },
  backButton: { display: 'none', '@media (max-width: 1000px)': { display: 'inline-flex' } },
  patient: {
    appearance: 'none',
    margin: 0,
    textAlign: 'start',
    fontFamily: 'inherit',
    color: tokens.colorNeutralForeground1,
    display: 'grid',
    gridTemplateColumns: '56px minmax(0, 1fr)',
    gap: '2px 10px',
    padding: '10px',
    minHeight: '64px',
    borderRadius: tokens.borderRadiusMedium,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    backgroundColor: tokens.colorNeutralBackground1,
    cursor: 'pointer',
    ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover },
    ':focus-visible': { outline: `3px solid ${tokens.colorStrokeFocus2}`, outlineOffset: '1px' },
  },
  selected: { border: `2px solid ${tokens.colorBrandStroke1}`, backgroundColor: tokens.colorBrandBackground2 },
  bed: {
    fontWeight: tokens.fontWeightBold,
    fontFamily: tokens.fontFamilyNumeric,
    gridRow: 'span 2',
    alignSelf: 'center',
  },
  name: { fontWeight: tokens.fontWeightSemibold, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' },
  meta: { display: 'flex', gap: '4px', flexWrap: 'wrap', alignItems: 'center' },
  wardView: {
    display: 'grid',
    gap: '16px',
    gridTemplateColumns: 'minmax(0, 1fr)',
    alignItems: 'start',
    '@media (min-width: 1500px)': { gridTemplateColumns: 'minmax(0, 1fr) minmax(0, 1fr)' },
  },
  chart: { display: 'flex', flexDirection: 'column', gap: '12px', minWidth: 0 },
  muted: { color: tokens.colorNeutralForeground3 },
});

interface WardPatient {
  bed: BedBoardItem;
  occupant: BedOccupant;
}

/**
 * Ward nursing station (tablet-first): the ward's patients on the left, the medication round and handover sheet when no
 * patient is open, and each patient's chart (MAR, medicines, notes, SBAR handover, intake/output, risk scores) on the right.
 */
function NursingStationPage() {
  const styles = useStyles();
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const canChart = usePermission('nursing.chart');
  const canMar = usePermission('nursing.mar');
  const canReadAdmissions = usePermission('inpatient.admissions.read');
  const [storedWard, setStoredWard] = useStoredState(WARD_KEY);
  const [term, setTerm] = useState('');
  const [now] = useState(() => new Date());

  const wards = useQuery(inpatientQueries.wards());
  const wardList = wards.data ?? [];
  const wardId =
    search.wardId ??
    (wardList.some((w) => w.id === storedWard) ? storedWard : wardList.length === 1 ? (wardList[0]?.id ?? '') : '');
  const ward = wardList.find((w) => w.id === wardId) ?? null;
  useWardRealtime(wardId || null);

  const board = useQuery({ ...inpatientQueries.board(wardId || null), enabled: Boolean(wardId) && canReadAdmissions });
  const due = useQuery({ ...nursingQueries.wardMarDue(wardId, 60), enabled: Boolean(wardId) && canMar });
  const dueByAdmission = new Map<string, { due: number; overdue: number }>();
  for (const item of due.data ?? []) {
    const current = dueByAdmission.get(item.admissionId) ?? { due: 0, overdue: 0 };
    current.due += 1;
    current.overdue += item.isOverdue ? 1 : 0;
    dueByAdmission.set(item.admissionId, current);
  }

  const patients: WardPatient[] = (board.data?.[0]?.beds ?? [])
    .filter((bed): bed is BedBoardItem & { occupant: BedOccupant } => bed.occupant !== null)
    .map((bed) => ({ bed, occupant: bed.occupant }));
  const needle = term.trim().toLowerCase();
  const shown = needle
    ? patients.filter((p) =>
        [p.bed.label, p.occupant.patientName, p.occupant.uhid].some((v) => v.toLowerCase().includes(needle)),
      )
    : patients;
  const selected = patients.find((p) => p.occupant.admissionId === search.admissionId) ?? null;

  const chooseWard = (id: string) => {
    setStoredWard(id);
    void navigate({ search: { wardId: id || undefined }, replace: true });
  };
  const openPatient = (admissionId: string | undefined) =>
    void navigate({ search: (prev) => ({ ...prev, wardId: wardId || undefined, admissionId, tab: undefined }) });

  const shift = shiftAt(now);
  const totalDue = due.data?.length ?? 0;

  return (
    <Page>
      <PageHeader
        title="Nursing station"
        icon={<ClipboardPulseRegular />}
        subtitle={`${SHIFT_LABEL[shift]} · duty date ${formatDateOnly(dutyDate(now))}${ward ? ` · ${ward.name}` : ''}`}
        actions={
          <>
            <Field label="Ward" orientation="horizontal">
              <Select value={wardId} onChange={(_, d) => chooseWard(d.value)} aria-label="Ward">
                <option value="">Choose your ward</option>
                {wardList.map((w) => (
                  <option key={w.id} value={w.id}>
                    {w.name}
                  </option>
                ))}
              </Select>
            </Field>
            {wardId && (
              <Button
                icon={<BedRegular />}
                onClick={() => void navigate({ to: '/inpatient/beds', search: { wardId } })}
              >
                Bed board
              </Button>
            )}
          </>
        }
      />

      {!wardId ? (
        <EmptyState
          icon={<ClipboardPulseRegular />}
          title="Choose your ward"
          description="Your choice is remembered on this tablet or computer. The ward's patients, medication round and handovers appear here."
        />
      ) : !canReadAdmissions ? (
        <EmptyState
          title="You cannot see the ward's patient list"
          description="Ask the administrator for in-patient read access."
        />
      ) : (
        <div className={styles.layout}>
          <nav
            aria-label="Patients on the ward"
            className={mergeClasses(styles.list, selected ? styles.hideOnNarrow : undefined)}
          >
            <SearchBox
              value={term}
              onChange={(_, d) => setTerm(d.value)}
              placeholder="Bed, name or UHID"
              aria-label="Find a patient on the ward"
            />
            <Button
              appearance={selected ? 'secondary' : 'primary'}
              icon={canMar ? <PillRegular /> : <PeopleRegular />}
              onClick={() => openPatient(undefined)}
              style={{ justifyContent: 'flex-start', minHeight: 44 }}
            >
              {canMar ? `Medication round${totalDue ? ` (${totalDue} due)` : ''}` : 'Ward overview'}
            </Button>
            {board.error ? (
              <ErrorState error={board.error} onRetry={() => void board.refetch()} />
            ) : board.isLoading ? (
              <Skeleton aria-label="Loading patients">
                {Array.from({ length: 6 }, (_, i) => (
                  <SkeletonItem key={i} style={{ height: 64, marginBottom: 6 }} />
                ))}
              </Skeleton>
            ) : shown.length === 0 ? (
              <Caption1 className={styles.muted}>
                {patients.length === 0 ? 'No patients on this ward.' : 'No patient matches.'}
              </Caption1>
            ) : (
              shown.map(({ bed, occupant }) => {
                const meds = dueByAdmission.get(occupant.admissionId);
                const isSelected = occupant.admissionId === selected?.occupant.admissionId;
                return (
                  <button
                    key={bed.bedId}
                    type="button"
                    className={mergeClasses(styles.patient, isSelected && styles.selected)}
                    aria-current={isSelected ? 'true' : undefined}
                    aria-label={`Bed ${bed.label}, ${occupant.patientName}${meds ? `, ${meds.due} medicines due` : ''}${
                      bed.alerts.length ? `, alerts: ${bed.alerts.map((a) => bedAlertInfo(a).label).join(', ')}` : ''
                    }`}
                    onClick={() => openPatient(occupant.admissionId)}
                  >
                    <span className={styles.bed}>{bed.label}</span>
                    <span className={styles.name}>{occupant.patientName}</span>
                    <span className={styles.meta}>
                      <Caption1>
                        {occupant.age ?? '—'}/{genderShort[occupant.gender] ?? occupant.gender.charAt(0)} · Day{' '}
                        {occupant.lengthOfStayDays}
                      </Caption1>
                      {meds && (
                        <Badge
                          size="small"
                          appearance="tint"
                          color={meds.overdue ? 'danger' : 'warning'}
                          icon={<PillRegular />}
                        >
                          {meds.due}
                        </Badge>
                      )}
                      <BedAlertBadges alerts={bed.alerts.filter((a) => a !== 'MedicationOverdue')} max={2} />
                    </span>
                  </button>
                );
              })
            )}
          </nav>

          {selected ? (
            <PatientChart
              patient={selected}
              tab={search.tab ?? (canMar ? 'mar' : 'notes')}
              canChart={canChart}
              canMar={canMar}
              onTab={(tab) => void navigate({ search: (prev) => ({ ...prev, tab }), replace: true })}
              onBack={() => openPatient(undefined)}
              onOpenAdmission={() =>
                void navigate({
                  to: '/inpatient/admissions/$admissionId',
                  params: { admissionId: selected.occupant.admissionId },
                })
              }
            />
          ) : search.admissionId && board.data ? (
            <EmptyState
              title="This patient is no longer on the ward"
              description="They may have been transferred or discharged. Choose a patient from the list."
              action={<Button onClick={() => openPatient(undefined)}>Show the ward</Button>}
            />
          ) : (
            <div className={styles.wardView}>
              {canMar && <WardMarDue wardId={wardId} onOpenPatient={(id) => openPatient(id)} />}
              {canChart && <WardHandoverSheet wardId={wardId} canWrite={canChart} />}
              {!canMar && !canChart && (
                <Section>
                  <Text>Choose a patient to view their chart.</Text>
                </Section>
              )}
            </div>
          )}
        </div>
      )}
    </Page>
  );
}

function PatientChart({
  patient,
  tab,
  canChart,
  canMar,
  onTab,
  onBack,
  onOpenAdmission,
}: {
  patient: WardPatient;
  tab: ChartTab;
  canChart: boolean;
  canMar: boolean;
  onTab: (tab: ChartTab) => void;
  onBack: () => void;
  onOpenAdmission: () => void;
}) {
  const styles = useStyles();
  const { occupant, bed } = patient;
  const admissionId = occupant.admissionId;
  const canView = canChart || canMar;
  const tabs: { id: ChartTab; label: string; show: boolean }[] = [
    { id: 'mar', label: 'MAR', show: canView },
    { id: 'medicines', label: 'Medicines', show: canView },
    { id: 'notes', label: 'Notes', show: canChart },
    { id: 'handover', label: 'Handover', show: canChart },
    { id: 'io', label: 'Intake / output', show: canChart },
    { id: 'risk', label: 'Risk scores', show: canChart },
  ];
  const visible = tabs.filter((t) => t.show);
  const current = visible.some((t) => t.id === tab) ? tab : (visible[0]?.id ?? 'notes');

  return (
    <div className={styles.chart}>
      <Button className={styles.backButton} appearance="subtle" icon={<ArrowLeftRegular />} onClick={onBack}>
        All patients
      </Button>
      <PatientBannerWithFlags
        patientId={occupant.patientId}
        patient={{
          id: occupant.patientId,
          uhid: occupant.uhid,
          fullName: occupant.patientName,
          gender: occupant.gender,
          age: occupant.age,
          payer: `Bed ${bed.label} · Day ${occupant.lengthOfStayDays}${occupant.consultantName ? ` · ${occupant.consultantName}` : ''} · ${PAYER_LABEL[occupant.payerType]}`,
          alerts: bed.alerts.map(bedAlertBadge),
        }}
        actions={
          <Button icon={<OpenRegular />} onClick={onOpenAdmission}>
            Admission
          </Button>
        }
      />
      <Section flush>
        <div style={{ padding: '0 8px', overflowX: 'auto' }}>
          <TabList selectedValue={current} onTabSelect={(_, d) => onTab(d.value as ChartTab)} size="large">
            {visible.map((t) => (
              <Tab key={t.id} value={t.id}>
                {t.label}
              </Tab>
            ))}
          </TabList>
        </div>
        <div style={{ padding: 16 }} key={`${admissionId}-${current}`}>
          {current === 'mar' && <PatientMar admissionId={admissionId} patientName={occupant.patientName} />}
          {current === 'medicines' && <MedicationOrders admissionId={admissionId} />}
          {current === 'notes' && <NursingNotes admissionId={admissionId} canWrite={canChart} />}
          {current === 'handover' && <PatientHandovers admissionId={admissionId} canWrite={canChart} />}
          {current === 'io' && <IntakeOutput admissionId={admissionId} canWrite={canChart} />}
          {current === 'risk' && <Assessments admissionId={admissionId} canWrite={canChart} />}
        </div>
      </Section>
    </div>
  );
}
