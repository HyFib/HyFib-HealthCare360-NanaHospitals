import {
  Badge,
  Button,
  Caption1,
  makeStyles,
  Menu,
  MenuDivider,
  MenuItem,
  MenuList,
  MenuPopover,
  MenuTrigger,
  MessageBar,
  MessageBarActions,
  MessageBarBody,
  MessageBarTitle,
  Spinner,
  Text,
  tokens,
  Tooltip,
} from '@fluentui/react-components';
import {
  ArrowDownloadRegular,
  CheckmarkCircleRegular,
  DocumentArrowLeftRegular,
  EditRegular,
  MicSparkleRegular,
  PrintRegular,
  SaveRegular,
  SignatureRegular,
  WarningRegular,
} from '@fluentui/react-icons';
import { formatDateOnly, formatDateTime, formatRelative, todayIso, useSession } from '@hc360/core';
import {
  ConfirmDialog,
  ErrorState,
  type LeaveQuestion,
  Page,
  Section,
  StatusBadge,
  useLeaveGuard,
  useNotify,
} from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useEffect, useRef, useState, type ReactNode } from 'react';
import { aiQueries } from '@/lib/ai';
import { useServerDocuments } from '@/lib/documents';
import { useLetterhead } from '@/lib/letterhead';
import { usePermission } from '@/lib/permissions';
import { emrKeys, emrQueries, encounterTypeLabel, type EncounterDetail } from '@/modules/emr/api';
import { LatestVitals } from '@/modules/emr/ChartSummary';
import { BannerSkeleton, ClinicalBanner } from '@/modules/emr/ClinicalBanner';
import { EncounterDiagnoses } from '@/modules/emr/DiagnosisPanel';
import { DictationDialog, type DictationInserted } from '@/modules/emr/dictation/DictationDialog';
import {
  insertedToast,
  notAddedSummary,
  notePlaceholders,
  placeholderSignWarning,
  type NotAdded,
} from '@/modules/emr/dictation/dictation';
import {
  AmendDialog,
  AmendmentHistory,
  ComplaintSection,
  ExaminationSection,
  FollowUpSection,
} from '@/modules/emr/EncounterNote';
import { useClinicalSummary } from '@/modules/emr/hooks';
import { NoteComposer, NotesList } from '@/modules/emr/NotesPanel';
import { OrderComposer, OrdersList } from '@/modules/emr/OrdersPanel';
import { orderQueries } from '@/modules/emr/ordersApi';
import { PrescriptionEditor } from '@/modules/emr/PrescriptionEditor';
import { templateMedicineDefaults } from '@/modules/emr/prescriptionForm';
import { prescriptionDocument, prescriptionQueries } from '@/modules/emr/prescriptionsApi';
import { PrescriptionsList } from '@/modules/emr/PrescriptionsList';
import { PreviousVisit } from '@/modules/emr/PreviousVisit';
import { printEncounterSummary } from '@/modules/emr/print';
import { useEncounterNote, type EncounterNote } from '@/modules/emr/useEncounterNote';
import { EncounterVitalsList, VitalsForm } from '@/modules/emr/VitalsPanel';

export const Route = createFileRoute('/_app/emr/encounters/$encounterId')({
  component: EncounterPage,
});

const SECTIONS = [
  { id: 'complaint', label: 'Complaint and history' },
  { id: 'vitals', label: 'Vitals' },
  { id: 'exam', label: 'Examination and plan' },
  { id: 'diagnoses', label: 'Diagnoses' },
  { id: 'orders', label: 'Orders' },
  { id: 'prescription', label: 'Prescription' },
  { id: 'notes', label: 'Clinical notes' },
  { id: 'followup', label: 'Follow-up and referral' },
] as const;

type SectionId = (typeof SECTIONS)[number]['id'];

const useStyles = makeStyles({
  layout: {
    display: 'grid',
    gridTemplateColumns: 'minmax(0, 1fr) 220px',
    gap: '16px',
    alignItems: 'start',
    '@media (max-width: 1200px)': { gridTemplateColumns: 'minmax(0, 1fr)' },
  },
  main: { display: 'flex', flexDirection: 'column', gap: '16px', minWidth: 0 },
  section: { scrollMarginTop: '96px' },
  rail: {
    position: 'sticky',
    top: '96px',
    display: 'flex',
    flexDirection: 'column',
    gap: '2px',
    '@media (max-width: 1200px)': { display: 'none' },
  },
  railLink: { justifyContent: 'flex-start', width: '100%' },
  railKey: {
    color: tokens.colorNeutralForeground3,
    marginLeft: 'auto',
    paddingLeft: '8px',
    fontFamily: tokens.fontFamilyMonospace,
  },
  meta: { display: 'flex', gap: '8px', alignItems: 'center', flexWrap: 'wrap' },
  muted: { color: tokens.colorNeutralForeground3 },
  checklist: {
    display: 'flex',
    flexDirection: 'column',
    gap: '8px',
    margin: '12px 0 0',
    padding: 0,
    listStyle: 'none',
  },
  checkItem: { display: 'flex', gap: '8px', alignItems: 'flex-start' },
  ok: { color: tokens.colorPaletteGreenForeground1, display: 'flex', fontSize: '18px' },
  warn: { color: tokens.colorPaletteMarigoldForeground1, display: 'flex', fontSize: '18px' },
});

const LEAVE_NOTE: LeaveQuestion = {
  title: 'Leave without saving the note?',
  body:
    'Your unsaved text is kept on this computer and offered again when you reopen this encounter. Save it with ' +
    'Ctrl+S to store it on the server.',
  confirmLabel: 'Leave',
  destructive: false,
};

function EncounterPage() {
  const { encounterId } = Route.useParams();
  const encounter = useQuery(emrQueries.encounter(encounterId));
  if (encounter.isLoading) {
    return (
      <Page>
        <BannerSkeleton />
      </Page>
    );
  }
  if (encounter.error || !encounter.data) {
    return (
      <Page maxWidth={900}>
        <ErrorState error={encounter.error} onRetry={() => void encounter.refetch()} />
      </Page>
    );
  }
  return <EncounterWorkspace key={encounterId} encounter={encounter.data} />;
}

function WorkspaceSection({
  id,
  title,
  description,
  actions,
  children,
}: {
  id: SectionId;
  title: string;
  description?: ReactNode;
  actions?: ReactNode;
  children: ReactNode;
}) {
  const styles = useStyles();
  return (
    <div id={`enc-${id}`} className={styles.section}>
      <Section title={title} description={description} actions={actions}>
        {children}
      </Section>
    </div>
  );
}

function jumpTo(id: SectionId, focus = 'input, textarea, select, button') {
  const element = document.getElementById(`enc-${id}`);
  element?.scrollIntoView({ behavior: 'smooth', block: 'start' });
  element?.querySelector<HTMLElement>(focus)?.focus({ preventScroll: true });
}

/**
 * The encounter workspace (EMR-02..07): the previous visit's summary, complaint and history with templates, vitals
 * with live flags, examination, ICD-coded diagnoses, orders, the e-prescription with safety checks, notes, follow-up
 * and referral. Alt+1…8 jumps between sections; Ctrl+S saves the note; signing locks it and later changes are
 * amendments with a reason. "Dictate with AI" (AIV-04..06) drafts the SOAP note from speech or typed text for the
 * doctor to check and insert.
 */
function EncounterWorkspace({ encounter }: { encounter: EncounterDetail }) {
  const styles = useStyles();
  const notify = useNotify();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const patientId = encounter.patientId;
  const letterhead = useLetterhead();
  const userPractitionerId = useSession((s) => s.user?.practitionerId ?? null);
  const canWrite = usePermission('clinical.emr.write');
  const canSign = usePermission('clinical.encounters.sign');
  const canVitals = usePermission('clinical.vitals.record');
  const canOrder = usePermission('clinical.orders.place');
  const canPrescribe = usePermission('clinical.prescriptions.write');
  const [today] = useState(() => todayIso());
  const note = useEncounterNote(encounter, today);
  const summary = useClinicalSummary(patientId);
  const [templateId, setTemplateId] = useState<string | null>(encounter.templateId);
  const template = useQuery({ ...emrQueries.template(templateId ?? ''), enabled: Boolean(templateId) });
  const diagnoses = useQuery(emrQueries.diagnoses(patientId, encounter.id));
  const notes = useQuery(emrQueries.notes(patientId, encounter.id));
  const rxList = useQuery(prescriptionQueries.list(patientId, { encounterId: encounter.id }));
  const draftItem = rxList.data?.items.find(
    (p) => p.status === 'Draft' && (!userPractitionerId || p.practitionerId === userPractitionerId),
  );
  const draftDetail = useQuery({ ...prescriptionQueries.detail(draftItem?.id ?? ''), enabled: Boolean(draftItem) });
  const [signOpen, setSignOpen] = useState(false);
  const [amendOpen, setAmendOpen] = useState(false);
  const isDirty = note.form.formState.isDirty;
  const signed = encounter.status !== 'InProgress';
  const canDictate = usePermission('ai.dictation.use') && canWrite;
  const aiStatus = useQuery({ ...aiQueries.status(), enabled: canDictate && !note.locked });
  const [dictating, setDictating] = useState(false);
  const [codesNotAdded, setCodesNotAdded] = useState<NotAdded[]>([]);
  // Where to take the doctor once the dictation dialog has gone: focus cannot leave it while it is still closing.
  const jumpAfterDictation = useRef<SectionId | null>(null);

  // Alt+1…8 jumps between sections (works while typing, like the rest of the desktop shortcuts).
  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.altKey && !event.ctrlKey && !event.metaKey && /^[1-8]$/.test(event.key)) {
        const section = SECTIONS[Number(event.key) - 1];
        if (section) {
          event.preventDefault();
          jumpTo(section.id);
        }
      }
    };
    window.addEventListener('keydown', onKeyDown);
    return () => window.removeEventListener('keydown', onKeyDown);
  }, []);

  // Leaving with an unsaved note asks first (SRS UX-08); its draft stays on this computer.
  useLeaveGuard({ dirty: note.form.formState.isDirty, leaves: () => true, leaveQuestion: LEAVE_NOTE });

  const printSummary = useMutation({
    mutationFn: async () => {
      const [orders, vitals] = await Promise.all([
        queryClient.fetchQuery(orderQueries.list(patientId, { encounterId: encounter.id })),
        queryClient.fetchQuery(emrQueries.vitals(patientId, encounter.id, 1, 1)),
      ]);
      const signedRx = (rxList.data?.items ?? []).filter((p) => p.status === 'Signed');
      const prescriptions = await Promise.all(
        signedRx.map((p) => queryClient.fetchQuery(prescriptionQueries.detail(p.id))),
      );
      const s = summary.data;
      await printEncounterSummary({
        letterhead,
        patient: {
          fullName: s?.patient.fullName ?? '',
          uhid: s?.patient.uhid ?? '',
          age: s?.patient.age ?? null,
          gender: s?.patient.gender ?? 'Unknown',
        },
        encounter,
        diagnoses: diagnoses.data ?? [],
        vitals: vitals.items[0] ?? null,
        orders: orders.items,
        prescriptions,
        allergies: (s?.allergies ?? []).map((a) => a.allergen),
      });
    },
    onError: (error) => notify.error(error, 'Printing failed'),
  });

  const documents = useServerDocuments();

  if (!summary.data) {
    return (
      <Page>
        {summary.error ? (
          <ErrorState error={summary.error} onRetry={() => void summary.refetch()} />
        ) : (
          <BannerSkeleton />
        )}
      </Page>
    );
  }

  const s = summary.data;
  const signedRx = (rxList.data?.items ?? []).filter((p) => p.status === 'Signed');
  const diagnosisCount = diagnoses.data?.length ?? 0;
  const unsignedNotes = (notes.data?.items ?? []).filter((n) => !n.isSigned).length;
  const templateMedicines = (template.data?.medications ?? []).map(templateMedicineDefaults);
  const rxReady = rxList.isSuccess && (!draftItem || draftDetail.isSuccess);
  const restorable =
    note.draft.saved !== null && JSON.stringify(note.draft.saved.value) !== JSON.stringify(note.defaults);

  const askToSign = () => {
    if (!note.canSign()) {
      notify.warning('Nothing to sign yet', 'Record at least the chief complaint or the assessment.');
      jumpTo('complaint');
      return;
    }
    setSignOpen(true);
  };

  const dictationInserted = (result: DictationInserted) => {
    jumpAfterDictation.current =
      result.fields.length > 0 ? 'complaint' : result.diagnosesAdded > 0 ? 'diagnoses' : null;
    setDictating(false);
    setCodesNotAdded(result.notAdded);
    if (result.diagnosesAdded + result.notAdded.length > 0) {
      void queryClient.invalidateQueries({ queryKey: emrKeys.patient(patientId) });
    }
    const toast = insertedToast(result.fields.length, result.diagnosesAdded, note.amending);
    notify[toast.intent](toast.title, toast.body);
  };

  const afterDictation = () => {
    const section = jumpAfterDictation.current;
    jumpAfterDictation.current = null;
    if (section) {
      jumpTo(section, section === 'complaint' ? 'textarea' : undefined);
    }
  };

  return (
    <Page>
      <ClinicalBanner
        summary={s}
        actions={
          <>
            {canDictate && !note.locked && aiStatus.data?.enabled && (
              <Tooltip
                content="Speak or type the consultation; AI drafts the note for you to check"
                relationship="description"
              >
                <Button icon={<MicSparkleRegular />} onClick={() => setDictating(true)}>
                  Dictate with AI
                </Button>
              </Tooltip>
            )}
            {!signed && canWrite && (
              <Tooltip content="Save the note (Ctrl+S)" relationship="description">
                <Button
                  icon={note.save.isPending ? <Spinner size="tiny" aria-hidden /> : <SaveRegular />}
                  disabled={note.save.isPending || !isDirty}
                  onClick={() => note.save.mutate()}
                >
                  {isDirty ? 'Save' : 'Saved'}
                </Button>
              </Tooltip>
            )}
            {!signed && canSign && (
              <Button
                appearance="primary"
                icon={<SignatureRegular />}
                disabled={note.sign.isPending}
                onClick={askToSign}
              >
                Sign encounter
              </Button>
            )}
            {signed && canSign && !note.amending && (
              <Button icon={<EditRegular />} onClick={note.startAmendment}>
                Amend
              </Button>
            )}
            {note.amending && (
              <>
                <Button onClick={note.cancelAmendment}>Cancel amendment</Button>
                <Button
                  appearance="primary"
                  icon={<SignatureRegular />}
                  disabled={!isDirty || note.amend.isPending}
                  onClick={() => setAmendOpen(true)}
                >
                  Record amendment
                </Button>
              </>
            )}
            <Menu>
              <MenuTrigger disableButtonEnhancement>
                <Button icon={<PrintRegular />}>Print</Button>
              </MenuTrigger>
              <MenuPopover>
                <MenuList>
                  <MenuItem
                    icon={<PrintRegular />}
                    disabled={printSummary.isPending}
                    onClick={() => printSummary.mutate()}
                  >
                    Consultation summary (A4)
                  </MenuItem>
                  {signedRx.map((p) => (
                    <MenuItem
                      key={p.id}
                      icon={<PrintRegular />}
                      disabled={documents.print.isPending}
                      onClick={() => documents.print.mutate(prescriptionDocument(p.id))}
                    >
                      Prescription {p.prescriptionNumber} (A5)
                    </MenuItem>
                  ))}
                  {signedRx.length === 0 && <MenuItem disabled>No signed prescription yet</MenuItem>}
                  {signedRx.length > 0 && <MenuDivider />}
                  {signedRx.map((p) => (
                    <MenuItem
                      key={p.id}
                      icon={<ArrowDownloadRegular />}
                      disabled={documents.downloadPdf.isPending}
                      onClick={() => documents.downloadPdf.mutate(prescriptionDocument(p.id))}
                    >
                      Prescription {p.prescriptionNumber} as PDF
                    </MenuItem>
                  ))}
                </MenuList>
              </MenuPopover>
            </Menu>
          </>
        }
      />

      <div className={styles.meta}>
        <Text weight="semibold">{encounterTypeLabel[encounter.type]}</Text>
        <Caption1 className={styles.muted}>
          {formatDateTime(encounter.startedAt)} · {encounter.practitionerName ?? 'Doctor'}
          {encounter.departmentName ? ` · ${encounter.departmentName}` : ''}
        </Caption1>
        <StatusBadge
          status={signed ? 'Signed' : 'Draft'}
          label={signed ? (encounter.status === 'Amended' ? 'Signed, amended' : 'Signed') : 'Not signed'}
        />
        {signed && encounter.signedAt && (
          <Caption1 className={styles.muted}>Signed {formatDateTime(encounter.signedAt)}</Caption1>
        )}
        {!signed && isDirty && <Caption1 className={styles.muted}>Unsaved changes are kept on this computer</Caption1>}
        <Button
          size="small"
          appearance="transparent"
          onClick={() => void navigate({ to: '/emr/patients/$patientId', params: { patientId } })}
        >
          Open full chart
        </Button>
      </div>

      {restorable && note.draft.saved && (
        <MessageBar intent="info" icon={<DocumentArrowLeftRegular />}>
          <MessageBarBody>
            Unsaved changes to this note from {formatRelative(new Date(note.draft.saved.savedAt).toISOString())} were
            found on this computer.
          </MessageBarBody>
          <MessageBarActions>
            <Button onClick={note.draft.restore}>Restore them</Button>
            <Button appearance="subtle" onClick={note.draft.discard}>
              Discard
            </Button>
          </MessageBarActions>
        </MessageBar>
      )}

      {codesNotAdded.length > 0 && (
        <MessageBar intent="warning" layout="multiline">
          <MessageBarBody>
            <MessageBarTitle>Some codes from the AI draft were not added</MessageBarTitle>
            {notAddedSummary(codesNotAdded)}
          </MessageBarBody>
          <MessageBarActions>
            <Button onClick={() => jumpTo('diagnoses')}>Go to Diagnoses</Button>
            <Button appearance="subtle" onClick={() => setCodesNotAdded([])}>
              Dismiss
            </Button>
          </MessageBarActions>
        </MessageBar>
      )}

      <div className={styles.layout}>
        <div className={styles.main}>
          <PreviousVisit
            encounterId={encounter.id}
            onOpen={(id) => void navigate({ to: '/emr/encounters/$encounterId', params: { encounterId: id } })}
          />

          <WorkspaceSection id="complaint" title="Complaint and history">
            <ComplaintSection
              note={note}
              today={today}
              activeTemplate={template.data ?? null}
              onTemplateLoaded={(t) => setTemplateId(t.id)}
            />
          </WorkspaceSection>

          <WorkspaceSection
            id="vitals"
            title="Vitals"
            description="Values outside the usual range for the patient's age are flagged as you type."
          >
            {canVitals ? (
              <>
                <VitalsForm patientId={patientId} encounterId={encounter.id} autoFocus={false} />
                <div style={{ height: 12 }} />
                <EncounterVitalsList patientId={patientId} encounterId={encounter.id} />
              </>
            ) : (
              <LatestVitals vitals={s.lastVitals} />
            )}
          </WorkspaceSection>

          <WorkspaceSection id="exam" title="Examination and plan">
            <ExaminationSection note={note} />
          </WorkspaceSection>

          <WorkspaceSection
            id="diagnoses"
            title={`Diagnoses (${diagnosisCount})`}
            description="ICD-10 coded; the first primary diagnosis prints on the prescription."
          >
            <EncounterDiagnoses patientId={patientId} encounterId={encounter.id} problems={s.activeProblems} />
          </WorkspaceSection>

          <WorkspaceSection
            id="orders"
            title="Orders"
            description="Lab, imaging and procedures go straight to the departments and billing."
          >
            {canOrder && (
              <OrderComposer
                patientId={patientId}
                encounterId={encounter.id}
                visitId={encounter.visitId}
                admissionId={encounter.admissionId}
                templateOrders={template.data?.defaultOrders ?? []}
              />
            )}
            <div style={{ height: 12 }} />
            <OrdersList patientId={patientId} encounterId={encounter.id} />
          </WorkspaceSection>

          <WorkspaceSection
            id="prescription"
            title="Prescription"
            description="Safety checks run against allergies, current medicines, interactions and maximum doses before signing."
          >
            {canPrescribe &&
              (rxReady ? (
                <PrescriptionEditor
                  key={`rx-${encounter.id}`}
                  patientId={patientId}
                  encounterId={encounter.id}
                  visitId={encounter.visitId}
                  admissionId={encounter.admissionId}
                  isInpatient={encounter.type === 'InPatient'}
                  initialDraft={draftItem ? (draftDetail.data ?? null) : null}
                  templateMedicines={templateMedicines}
                  currentMedications={s.currentMedications}
                />
              ) : (
                <Spinner size="small" label="Loading the prescription…" />
              ))}
            <div style={{ height: 12 }} />
            <PrescriptionsList patientId={patientId} encounterId={encounter.id} excludeDrafts />
          </WorkspaceSection>

          <WorkspaceSection
            id="notes"
            title="Clinical notes"
            description="Procedure, counselling and progress notes of this encounter."
          >
            {canWrite && (
              <NoteComposer patientId={patientId} encounterId={encounter.id} admissionId={encounter.admissionId} />
            )}
            <div style={{ height: 12 }} />
            <NotesList
              patientId={patientId}
              encounterId={encounter.id}
              emptyHint="Notes written in this encounter appear here."
            />
          </WorkspaceSection>

          <WorkspaceSection id="followup" title="Follow-up and referral">
            <FollowUpSection note={note} today={today} />
          </WorkspaceSection>

          {encounter.amendments.length > 0 && (
            <Section
              title={`Amendment history (${encounter.amendments.length})`}
              description="The text before each amendment, kept word for word."
            >
              <AmendmentHistory encounter={encounter} />
            </Section>
          )}
        </div>

        <nav className={styles.rail} aria-label="Encounter sections">
          {SECTIONS.map((section, index) => (
            <Button
              key={section.id}
              className={styles.railLink}
              appearance="subtle"
              onClick={() => jumpTo(section.id)}
              aria-keyshortcuts={`Alt+${index + 1}`}
            >
              {section.label}
              <span className={styles.railKey}>Alt+{index + 1}</span>
            </Button>
          ))}
        </nav>
      </div>

      <SignDialog
        open={signOpen}
        note={note}
        diagnosisCount={diagnosisCount}
        hasDraftPrescription={Boolean(draftItem)}
        unsignedNotes={unsignedNotes}
        followUp={note.form.getValues('followUpDate')}
        onClose={() => setSignOpen(false)}
      />
      <AmendDialog
        open={amendOpen}
        busy={note.amend.isPending}
        onConfirm={(reason) => note.amend.mutate(reason, { onSettled: () => setAmendOpen(false) })}
        onCancel={() => setAmendOpen(false)}
      />
      {canDictate && (
        <DictationDialog
          open={dictating && !note.locked}
          patientId={patientId}
          encounterId={encounter.id}
          departmentName={encounter.departmentName}
          form={note.form}
          diagnoses={diagnoses.data}
          aiStatus={aiStatus.data}
          onClose={() => setDictating(false)}
          onInserted={dictationInserted}
          onClosed={afterDictation}
        />
      )}
    </Page>
  );
}

function SignDialog({
  open,
  note,
  diagnosisCount,
  hasDraftPrescription,
  unsignedNotes,
  followUp,
  onClose,
}: {
  open: boolean;
  note: EncounterNote;
  diagnosisCount: number;
  hasDraftPrescription: boolean;
  unsignedNotes: number;
  followUp: string | null;
  onClose: () => void;
}) {
  const styles = useStyles();
  const item = (ok: boolean, text: string) => (
    <li className={styles.checkItem}>
      <span className={ok ? styles.ok : styles.warn} aria-hidden>
        {ok ? <CheckmarkCircleRegular /> : <WarningRegular />}
      </span>
      <Text>{text}</Text>
    </li>
  );
  const errorCount = Object.keys(note.form.formState.errors).length;
  const placeholders = open ? notePlaceholders(note.form.getValues()) : [];
  return (
    <ConfirmDialog
      open={open}
      title="Sign this encounter?"
      confirmLabel="Sign encounter"
      busy={note.sign.isPending}
      onConfirm={() => note.sign.mutate(undefined, { onSettled: onClose })}
      onCancel={onClose}
    >
      <Text>Signing saves and locks the note. Later corrections are recorded as amendments with a reason.</Text>
      <ul className={styles.checklist}>
        {placeholders.length > 0 && item(false, placeholderSignWarning(placeholders))}
        {item(
          diagnosisCount > 0,
          diagnosisCount > 0
            ? `${diagnosisCount} diagnosis${diagnosisCount === 1 ? '' : 'es'} recorded`
            : 'No diagnosis recorded (medical records will flag the chart)',
        )}
        {item(
          !hasDraftPrescription,
          hasDraftPrescription
            ? 'A prescription draft is not signed and has not reached the pharmacy'
            : 'No unsigned prescription',
        )}
        {item(
          unsignedNotes === 0,
          unsignedNotes === 0
            ? 'All notes are signed'
            : `${unsignedNotes} note${unsignedNotes === 1 ? ' is' : 's are'} not signed`,
        )}
        {item(true, followUp ? `Follow-up on ${formatDateOnly(followUp)}` : 'No follow-up date')}
      </ul>
      {errorCount > 0 && (
        <Badge color="danger" style={{ marginTop: 8 }}>
          Correct the highlighted fields first
        </Badge>
      )}
    </ConfirmDialog>
  );
}
