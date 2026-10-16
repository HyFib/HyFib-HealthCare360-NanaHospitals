import {
  Button,
  makeStyles,
  Menu,
  MenuItem,
  MenuList,
  MenuPopover,
  MenuTrigger,
  MessageBar,
  MessageBarActions,
  MessageBarBody,
  Tab,
  TabList,
  useRestoreFocusTarget,
} from '@fluentui/react-components';
import {
  AlertUrgentRegular,
  HeartPulseRegular,
  MoreHorizontalRegular,
  PersonRegular,
  PlayRegular,
  WarningRegular,
} from '@fluentui/react-icons';
import { formatDateTime } from '@hc360/core';
import { Page, Section } from '@hc360/ui';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import type { TimelineFilters } from '@/modules/emr/api';
import { ChartSummary } from '@/modules/emr/ChartSummary';
import { BannerSkeleton, ChartLoadError, ClinicalBanner } from '@/modules/emr/ClinicalBanner';
import { DiagnosesTab } from '@/modules/emr/DiagnosisPanel';
import { AlertsTab, AllergiesTab, RaiseAlertDialog, RecordAllergyDialog } from '@/modules/emr/FlagsPanels';
import { useClinicalSummary } from '@/modules/emr/hooks';
import { NotesTab } from '@/modules/emr/NotesPanel';
import { OrdersTab } from '@/modules/emr/OrdersPanel';
import { PrescriptionsList } from '@/modules/emr/PrescriptionsList';
import { ProceduresTab } from '@/modules/emr/ProceduresPanel';
import { AdmissionsTab, DocumentsTab, EncountersTab, ImmunizationsTab } from '@/modules/emr/RecordTabs';
import { StartEncounterDialog } from '@/modules/emr/StartEncounterDialog';
import {
  CHART_TABS,
  chartTabLabel,
  parseChartTab,
  parseEncounterTypes,
  parseTypes,
  serializeEncounterTypes,
  serializeTypes,
  type ChartTab,
  type TimelineTarget,
} from '@/modules/emr/timeline';
import { TimelinePanel } from '@/modules/emr/TimelinePanel';
import { RecordVitalsDialog, VitalsTab } from '@/modules/emr/VitalsPanel';
import { ConsentsPanel } from '@/modules/patients/ConsentsPanel';

const DATE = /^\d{4}-\d{2}-\d{2}$/;

interface ChartSearch {
  tab?: ChartTab;
  types?: string;
  /** Kinds of care on the timeline (OutPatient, InPatient…). */
  care?: string;
  from?: string;
  to?: string;
  dept?: string;
  tpage?: number;
}

export const Route = createFileRoute('/_app/emr/patients/$patientId/')({
  validateSearch: (s: Record<string, unknown>): ChartSearch => ({
    tab: parseChartTab(s.tab),
    types: typeof s.types === 'string' ? (serializeTypes(parseTypes(s.types)) ?? undefined) : undefined,
    care: serializeEncounterTypes(parseEncounterTypes(s.care)),
    from: typeof s.from === 'string' && DATE.test(s.from) ? s.from : undefined,
    to: typeof s.to === 'string' && DATE.test(s.to) ? s.to : undefined,
    dept: typeof s.dept === 'string' && s.dept ? s.dept : undefined,
    tpage: typeof s.tpage === 'number' && s.tpage > 1 ? s.tpage : undefined,
  }),
  component: PatientChartPage,
});

const useStyles = makeStyles({
  tabs: { padding: '0 8px', flexWrap: 'wrap', rowGap: '2px' },
  tabBody: { padding: '16px' },
});

type OpenDialog = 'encounter' | 'vitals' | 'allergy' | 'alert' | null;

/** The patient's clinical chart (EHR-01..03): persistent banner, summary, timeline and every part of the record. */
function PatientChartPage() {
  const styles = useStyles();
  // Closing a dialog opened from the menu returns focus to the menu button.
  const restoreFocusTarget = useRestoreFocusTarget();
  const { patientId } = Route.useParams();
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const summary = useClinicalSummary(patientId);
  const canStart = usePermission('clinical.emr.write');
  const canVitals = usePermission('clinical.vitals.record');
  const canAllergies = usePermission('clinical.allergies.manage');
  const canAlerts = usePermission('clinical.alerts.manage');
  const [dialog, setDialog] = useState<OpenDialog>(null);
  const tab: ChartTab = search.tab ?? 'timeline';

  if (summary.isLoading) {
    return (
      <Page>
        <BannerSkeleton />
      </Page>
    );
  }
  if (summary.error || !summary.data) {
    return <ChartLoadError patientId={patientId} error={summary.error} onRetry={() => void summary.refetch()} />;
  }

  const s = summary.data;
  const openTab = (next: ChartTab) =>
    void navigate({ search: (prev) => ({ ...prev, tab: next === 'timeline' ? undefined : next }), replace: true });
  const openEncounter = (encounterId: string) =>
    void navigate({ to: '/emr/encounters/$encounterId', params: { encounterId } });
  const timelineFilters: TimelineFilters = {
    types: parseTypes(search.types),
    encounterTypes: parseEncounterTypes(search.care),
    from: search.from,
    to: search.to,
    departmentId: search.dept,
    page: search.tpage,
  };
  const onTimelineOpen = (target: TimelineTarget) =>
    target.kind === 'encounter' ? openEncounter(target.encounterId) : openTab(target.tab);
  const unsigned = s.recentEncounters.find((e) => e.status === 'InProgress');

  return (
    <Page>
      <ClinicalBanner
        summary={s}
        actions={
          <>
            {canStart && !s.patient.isDeceased && (
              <Button appearance="primary" icon={<PlayRegular />} onClick={() => setDialog('encounter')}>
                Start encounter
              </Button>
            )}
            {canVitals && (
              <Button icon={<HeartPulseRegular />} onClick={() => setDialog('vitals')}>
                Record vitals
              </Button>
            )}
            <Menu>
              <MenuTrigger disableButtonEnhancement>
                <Button icon={<MoreHorizontalRegular />} aria-label="More actions" {...restoreFocusTarget} />
              </MenuTrigger>
              <MenuPopover>
                <MenuList>
                  {canAllergies && (
                    <MenuItem icon={<WarningRegular />} onClick={() => setDialog('allergy')}>
                      Record allergy
                    </MenuItem>
                  )}
                  {canAlerts && (
                    <MenuItem icon={<AlertUrgentRegular />} onClick={() => setDialog('alert')}>
                      Add patient alert
                    </MenuItem>
                  )}
                  <MenuItem
                    icon={<PersonRegular />}
                    onClick={() => void navigate({ to: '/patients/$patientId', params: { patientId } })}
                  >
                    Patient profile
                  </MenuItem>
                </MenuList>
              </MenuPopover>
            </Menu>
          </>
        }
      />

      {unsigned && (
        <MessageBar intent="warning">
          <MessageBarBody>
            An encounter from {formatDateTime(unsigned.startedAt)}
            {unsigned.practitionerName ? ` (${unsigned.practitionerName})` : ''} is not signed yet.
          </MessageBarBody>
          <MessageBarActions>
            <Button onClick={() => openEncounter(unsigned.id)}>Continue it</Button>
          </MessageBarActions>
        </MessageBar>
      )}
      {s.resultsAwaitingReview > 0 && (
        <MessageBar intent="info">
          <MessageBarBody>
            {s.resultsAwaitingReview} {s.resultsAwaitingReview === 1 ? 'result is' : 'results are'} waiting for the
            doctor's review.
          </MessageBarBody>
          <MessageBarActions>
            <Button onClick={() => void navigate({ to: '/emr' })}>Open worklist</Button>
          </MessageBarActions>
        </MessageBar>
      )}

      <ChartSummary
        summary={s}
        onOpenTab={openTab}
        onOpenEncounter={openEncounter}
        onRecordVitals={canVitals ? () => setDialog('vitals') : undefined}
      />

      <Section flush>
        <TabList
          className={styles.tabs}
          selectedValue={tab}
          onTabSelect={(_, d) => openTab(d.value as ChartTab)}
          size="small"
          aria-label="Patient record"
        >
          {CHART_TABS.map((t) => (
            <Tab key={t} value={t}>
              {chartTabLabel[t]}
            </Tab>
          ))}
        </TabList>
        <div className={styles.tabBody}>
          {tab === 'timeline' && (
            <TimelinePanel
              patientId={patientId}
              filters={timelineFilters}
              onFiltersChange={(f) =>
                void navigate({
                  search: (prev) => ({
                    ...prev,
                    types: serializeTypes(f.types),
                    care: serializeEncounterTypes(f.encounterTypes),
                    from: f.from,
                    to: f.to,
                    dept: f.departmentId,
                    tpage: f.page,
                  }),
                  replace: true,
                })
              }
              onOpen={onTimelineOpen}
            />
          )}
          {tab === 'encounters' && <EncountersTab patientId={patientId} />}
          {tab === 'vitals' && <VitalsTab patientId={patientId} />}
          {tab === 'allergies' && <AllergiesTab patientId={patientId} />}
          {tab === 'alerts' && <AlertsTab patientId={patientId} />}
          {tab === 'diagnoses' && <DiagnosesTab patientId={patientId} />}
          {tab === 'notes' && <NotesTab patientId={patientId} />}
          {tab === 'procedures' && <ProceduresTab patientId={patientId} />}
          {tab === 'immunisations' && <ImmunizationsTab patientId={patientId} />}
          {tab === 'orders' && <OrdersTab patientId={patientId} />}
          {tab === 'prescriptions' && <PrescriptionsList patientId={patientId} />}
          {tab === 'documents' && <DocumentsTab patientId={patientId} />}
          {tab === 'consents' && <ConsentsPanel patientId={patientId} />}
          {tab === 'admissions' && <AdmissionsTab patientId={patientId} />}
        </div>
      </Section>

      <StartEncounterDialog patientId={patientId} open={dialog === 'encounter'} onClose={() => setDialog(null)} />
      <RecordVitalsDialog
        patientId={patientId}
        encounterId={null}
        open={dialog === 'vitals'}
        onClose={() => setDialog(null)}
      />
      <RecordAllergyDialog
        patientId={patientId}
        open={dialog === 'allergy'}
        onClose={() => setDialog(null)}
        onOpenExisting={() => {
          setDialog(null);
          openTab('allergies');
        }}
      />
      <RaiseAlertDialog patientId={patientId} open={dialog === 'alert'} onClose={() => setDialog(null)} />
    </Page>
  );
}
