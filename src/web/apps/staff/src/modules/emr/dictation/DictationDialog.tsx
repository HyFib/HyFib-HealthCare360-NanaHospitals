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
  Field,
  Input,
  makeStyles,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  Radio,
  RadioGroup,
  Spinner,
  Subtitle2,
  Text,
  Textarea,
  tokens,
} from '@fluentui/react-components';
import {
  ArrowLeftRegular,
  ErrorCircleRegular,
  MicRegular,
  RecordStopRegular,
  ShieldLockRegular,
  SparkleRegular,
} from '@fluentui/react-icons';
import { bridge, formatNumber } from '@hc360/core';
import { ConfirmDialog } from '@hc360/ui';
import { useEffect, useId, useRef, useState, type KeyboardEvent, type ReactNode } from 'react';
import type { UseFormReturn } from 'react-hook-form';
import {
  SPECIALTY_MAX_LENGTH,
  structureNote,
  TRANSCRIPT_MAX_LENGTH,
  type AiStatusDto,
  type ClinicalNoteDraft,
  type SuggestedIcdCode,
} from '@/lib/ai';
import { recorderSupported, voiceAvailability, type VoiceMode } from '@/lib/voice/recorder';
import { useVoiceCapture, type VoiceCapture } from '@/lib/voice/useVoiceCapture';
import { emrApi, type DiagnosisDto } from '../api';
import { NOTE_FIELD_LIMITS, type EncounterFormValues } from '../encounterForm';
import {
  addDiagnoses,
  appendTake,
  confidenceLabel,
  defaultSpecialty,
  DICTATION_VOICE,
  dictationErrorMessage,
  EMPTY_SOAP,
  findPlaceholders,
  insertHint,
  isEmptyReview,
  mergeIntoNote,
  needsDiscardConfirmation,
  overflowingSections,
  overflowMessage,
  recordedCodes,
  reviewFromDraft,
  sectionStatus,
  SOAP_FIELDS,
  SOAP_LABELS,
  soapOf,
  structureRequest,
  suggestChiefComplaint,
  tickedCodes,
  transcriptProblem,
  type InsertMode,
  type NotAdded,
  type SoapField,
  type SoapText,
} from './dictation';

/** What inserting did, for the encounter screen to report. */
export interface DictationInserted {
  /** The parts of the note that changed. */
  fields: SoapField[];
  diagnosesAdded: number;
  /** Ticked codes the server did not add. */
  notAdded: NotAdded[];
}

export interface DictationProps {
  patientId: string;
  encounterId: string;
  departmentName: string | null;
  /** The encounter note's form: read when the dialog opens and written on insert. Nothing is saved here. */
  form: Pick<UseFormReturn<EncounterFormValues>, 'getValues' | 'setValue'>;
  /** This encounter's diagnoses; undefined while they load. */
  diagnoses: readonly DiagnosisDto[] | undefined;
  aiStatus: AiStatusDto | undefined;
  onClose: () => void;
  onInserted: (result: DictationInserted) => void;
}

const ROWS: Record<SoapField, number> = {
  chiefComplaint: 2,
  historyOfPresentIllness: 5,
  examination: 4,
  assessment: 3,
  plan: 4,
};

const useStyles = makeStyles({
  surface: { width: 'min(1040px, 96vw)', maxWidth: 'min(1040px, 96vw)' },
  content: { display: 'flex', flexDirection: 'column', gap: '12px' },
  step: { margin: 0, color: tokens.colorNeutralForeground3, outlineStyle: 'none' },
  heading: { margin: 0 },
  muted: { color: tokens.colorNeutralForeground3 },
  voice: { display: 'flex', alignItems: 'center', gap: '12px', flexWrap: 'wrap' },
  voiceLine: { flex: '1 1 260px', minWidth: 0, display: 'flex', alignItems: 'center', gap: '6px' },
  voiceError: { color: tokens.colorPaletteRedForeground1 },
  specialty: { maxWidth: '420px' },
  privacy: { display: 'flex', alignItems: 'center', gap: '6px', color: tokens.colorNeutralForeground3 },
  list: { margin: '4px 0 0', paddingLeft: '20px' },
  placeholders: { fontFamily: tokens.fontFamilyMonospace },
  columns: {
    display: 'grid',
    gridTemplateColumns: 'minmax(0, 1.7fr) minmax(240px, 1fr)',
    gap: '20px',
    alignItems: 'start',
    '@media (max-width: 860px)': { gridTemplateColumns: 'minmax(0, 1fr)' },
  },
  sections: { display: 'flex', flexDirection: 'column', gap: '12px', minWidth: 0 },
  codes: { display: 'flex', flexDirection: 'column', gap: '8px', minWidth: 0 },
  codeList: { listStyle: 'none', margin: 0, padding: 0, display: 'flex', flexDirection: 'column', gap: '4px' },
  code: {
    fontFamily: tokens.fontFamilyMonospace,
    fontWeight: tokens.fontWeightSemibold,
    color: tokens.colorBrandForeground1,
  },
});

/**
 * "Dictate with AI" on the encounter workspace (SRS AIV-04..06): the doctor speaks or types the consultation, the
 * server turns it into a SOAP draft with ICD-10 suggestions, and the doctor checks it before it goes into the note.
 * Inserting fills the note's boxes (the doctor then saves and signs as usual) and adds only the codes the doctor ticked.
 * `onClosed` runs once the dialog has gone from the screen.
 */
export function DictationDialog({
  open,
  onClosed,
  ...props
}: DictationProps & { open: boolean; onClosed?: () => void }) {
  // A fresh dictation every time the dialog opens, even if it is reopened while still closing.
  const [session, setSession] = useState(0);
  const [wasOpen, setWasOpen] = useState(open);
  if (open !== wasOpen) {
    setWasOpen(open);
    if (open) {
      setSession((count) => count + 1);
    }
  }
  return (
    <Dialog
      open={open}
      onOpenChange={(_, data) => !data.open && props.onClose()}
      surfaceMotion={{ onMotionFinish: (_, data) => data.direction === 'exit' && onClosed?.() }}
    >
      <DictationFlow key={session} {...props} />
    </Dialog>
  );
}

/**
 * The dialog itself: dictate, then review and insert. Esc, a click beside the dialog and Cancel all ask before throwing
 * a dictation or draft away. A draft that arrives after closing is ignored (the server has already counted the call).
 */
export function DictationFlow({
  patientId,
  encounterId,
  departmentName,
  form,
  diagnoses,
  aiStatus,
  onClose,
  onInserted,
}: DictationProps) {
  const styles = useStyles();
  const [note] = useState(() => soapOf(form.getValues()));
  const [step, setStep] = useState<'dictate' | 'review'>('dictate');
  const [transcript, setTranscript] = useState('');
  const [specialty, setSpecialty] = useState(() => defaultSpecialty(departmentName));
  const [takes, setTakes] = useState(0);
  const [making, setMaking] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [draft, setDraft] = useState<ClinicalNoteDraft | null>(null);
  const [review, setReview] = useState<SoapText>(EMPTY_SOAP);
  const [mode, setMode] = useState<InsertMode>('append');
  const [ticked, setTicked] = useState<ReadonlySet<string>>(() => new Set());
  const [inserting, setInserting] = useState(false);
  const [confirmingClose, setConfirmingClose] = useState(false);
  const [canRecord] = useState(recorderSupported);
  // The draft request whose answer is still wanted: closing moves it on, so a late answer is dropped.
  const wantedRequest = useRef(0);
  const transcriptRef = useRef<HTMLTextAreaElement>(null);
  const reviewHeadingRef = useRef<HTMLHeadingElement>(null);

  const voice = voiceAvailability(
    { isDesktop: bridge.isDesktop, recorderSupported: canRecord, status: aiStatus, statusFailed: false },
    DICTATION_VOICE,
  );
  const capture = useVoiceCapture(
    voice.mode,
    patientId,
    (take) => {
      setTranscript((current) => appendTake(current, take));
      setTakes((count) => count + 1);
    },
    DICTATION_VOICE,
  );

  useEffect(() => {
    (step === 'review' ? reviewHeadingRef : transcriptRef).current?.focus();
  }, [step]);

  const transcriptIssue = transcriptProblem(transcript);
  const listening = capture.phase !== 'idle';
  const suggestions = draft?.suggestedIcd ?? [];
  const recorded = recordedCodes(suggestions, diagnoses);
  const codes = tickedCodes(suggestions, ticked, recorded);
  // While the diagnoses are unknown, never risk replacing a primary diagnosis.
  const hasPrimary = diagnoses ? diagnoses.some((d) => d.isPrimary) : true;
  const placeholders = findPlaceholders(...SOAP_FIELDS.map((field) => review[field]));
  const overflows = overflowingSections(note, review, mode);
  const nothingToInsert = isEmptyReview(review) && codes.length === 0;

  const makeDraft = async () => {
    if (making || inserting || listening || transcriptIssue) {
      return;
    }
    const request = ++wantedRequest.current;
    setMaking(true);
    setError(null);
    try {
      const result = await structureNote(structureRequest(transcript, specialty, patientId), aiStatus?.timeoutSeconds);
      if (request !== wantedRequest.current) {
        return;
      }
      setDraft(result);
      setReview(reviewFromDraft(result, note));
      setMode('append');
      setTicked(new Set());
      setStep('review');
    } catch (failure) {
      if (request === wantedRequest.current) {
        setError(dictationErrorMessage(failure, 'draft'));
      }
    } finally {
      if (request === wantedRequest.current) {
        setMaking(false);
      }
    }
  };

  const close = () => {
    wantedRequest.current += 1;
    capture.cancel();
    setConfirmingClose(false);
    onClose();
  };

  const requestClose = () => {
    if (inserting) {
      return;
    }
    if (needsDiscardConfirmation(transcript, draft !== null)) {
      setConfirmingClose(true);
    } else {
      close();
    }
  };

  const insert = async () => {
    if (inserting || overflows.length > 0 || nothingToInsert) {
      return;
    }
    setInserting(true);
    const patch = mergeIntoNote(soapOf(form.getValues()), review, mode);
    for (const field of SOAP_FIELDS) {
      const value = patch[field];
      if (value !== undefined) {
        form.setValue(field, value, { shouldDirty: true });
      }
    }
    const outcome = await addDiagnoses(codes, hasPrimary, (request) => emrApi.addDiagnosis(encounterId, request));
    onInserted({
      fields: SOAP_FIELDS.filter((field) => patch[field] !== undefined),
      diagnosesAdded: outcome.added.length,
      notAdded: outcome.notAdded,
    });
  };

  const tick = (code: string, checked: boolean) =>
    setTicked((current) => {
      const next = new Set(current);
      if (checked) {
        next.add(code);
      } else {
        next.delete(code);
      }
      return next;
    });

  const onSurfaceKeyDown = (event: KeyboardEvent<HTMLDivElement>) => {
    // Esc in a nested dialog (the discard question) is handled, and marked, by that dialog.
    if (event.key === 'Escape' && !event.defaultPrevented) {
      event.preventDefault();
      requestClose();
    }
  };

  const onTranscriptKeyDown = (event: KeyboardEvent<HTMLTextAreaElement>) => {
    if (event.key === 'Enter' && (event.ctrlKey || event.metaKey) && !event.nativeEvent.isComposing) {
      event.preventDefault();
      void makeDraft();
    }
  };

  const transcriptHint = (): string => {
    if (!transcript.trim()) {
      return voice.mode ? 'Speak, type or paste the consultation.' : 'Type or paste the consultation.';
    }
    if (transcriptIssue) {
      return transcriptIssue;
    }
    return `${formatNumber(transcript.length)} of ${formatNumber(TRANSCRIPT_MAX_LENGTH)} characters. Ctrl+Enter makes the draft.`;
  };
  const atLimit = transcript.length >= TRANSCRIPT_MAX_LENGTH;
  const suggestedComplaint = draft && !note.chiefComplaint.trim() ? suggestChiefComplaint(draft.subjective) : '';

  return (
    <DialogSurface
      className={styles.surface}
      onKeyDown={onSurfaceKeyDown}
      backdrop={{
        onClick: (event) => {
          event.preventDefault();
          requestClose();
        },
      }}
    >
      <DialogBody>
        <DialogTitle>Dictate with AI</DialogTitle>
        <DialogContent>
          {/* Not a flex column itself: DialogContent's height is limited (it scrolls), and in a limited column Fluent's
              MessageBar (min-height 36px) shrinks below its text and overlaps the next item. */}
          <div className={styles.content}>
            {step === 'dictate' || !draft ? (
              <>
                <Text as="h3" size={400} weight="semibold" className={styles.step}>
                  Step 1 of 2: dictate
                </Text>
                <Text>
                  Speak or type the consultation in your own words: the complaints and history, what you found, what you
                  think and what you plan. AI turns it into a SOAP draft for you to check before anything goes into the
                  note.
                </Text>
                <Field
                  label="Dictation"
                  hint={atLimit ? undefined : transcriptHint()}
                  validationState={atLimit ? 'warning' : 'none'}
                  validationMessage={
                    atLimit
                      ? 'The dictation is at the 20,000-character limit. Make a draft of this part, insert it, then dictate the rest.'
                      : undefined
                  }
                >
                  <Textarea
                    ref={transcriptRef}
                    value={transcript}
                    onChange={(_, data) => setTranscript(data.value)}
                    onKeyDown={onTranscriptKeyDown}
                    readOnly={making}
                    rows={12}
                    resize="vertical"
                    maxLength={TRANSCRIPT_MAX_LENGTH}
                    placeholder="For example: fever for three days with dry cough. Temperature 101, throat congested. Likely viral pharyngitis. Paracetamol 650 mg three times a day for three days; review if not better."
                  />
                </Field>
                <VoiceControls
                  capture={capture}
                  mode={voice.mode}
                  reason={voice.reason}
                  takes={takes}
                  disabled={making || atLimit}
                />
                <Field
                  className={styles.specialty}
                  label="Specialty"
                  hint="Optional. Helps the AI use the right terms; filled in from the encounter’s department."
                >
                  <Input
                    value={specialty}
                    onChange={(_, data) => setSpecialty(data.value)}
                    maxLength={SPECIALTY_MAX_LENGTH}
                    readOnly={making}
                  />
                </Field>
                <Caption1 className={styles.privacy}>
                  <ShieldLockRegular aria-hidden />
                  Names, phone numbers and hospital numbers are taken out before the text leaves the hospital.
                </Caption1>
                {error && (
                  <MessageBar intent="error" layout="multiline">
                    <MessageBarBody>
                      <MessageBarTitle>No draft this time</MessageBarTitle>
                      {error}
                    </MessageBarBody>
                  </MessageBar>
                )}
                <div aria-live="polite">
                  {making ? (
                    <Spinner
                      size="tiny"
                      labelPosition="after"
                      label="Making the SOAP draft. This can take up to a minute; keep this window open."
                    />
                  ) : (
                    draft && (
                      <Caption1 className={styles.muted}>
                        “Back to the draft” returns to the draft you were checking; “Make a new draft” replaces it.
                      </Caption1>
                    )
                  )}
                </div>
              </>
            ) : (
              <>
                <Text as="h3" size={400} weight="semibold" ref={reviewHeadingRef} tabIndex={-1} className={styles.step}>
                  Step 2 of 2: check the draft
                </Text>
                <MessageBar intent="warning" layout="multiline">
                  <MessageBarBody>
                    <MessageBarTitle>Check every part before inserting</MessageBarTitle>
                    <ul className={styles.list}>
                      {draft.warnings.map((warning, index) => (
                        <li key={index}>{warning}</li>
                      ))}
                    </ul>
                  </MessageBarBody>
                </MessageBar>
                {placeholders.length > 0 && (
                  <MessageBar intent="warning" layout="multiline">
                    <MessageBarBody>
                      <MessageBarTitle>
                        {placeholders.length === 1 ? '1 placeholder' : `${placeholders.length} placeholders`} to replace
                      </MessageBarTitle>
                      Names and numbers were taken out before the dictation left the hospital and are shown as{' '}
                      <span className={styles.placeholders}>{placeholders.join(', ')}</span>. Replace each one with the
                      right words, or delete it. Any left in the note are pointed out again before signing.
                    </MessageBarBody>
                  </MessageBar>
                )}
                <div className={styles.columns}>
                  <div className={styles.sections}>
                    {SOAP_FIELDS.some((field) => note[field].trim()) && (
                      <Field
                        label="Where the note already has text"
                        hint="Empty boxes leave that part of the note as it is."
                      >
                        <RadioGroup
                          layout="horizontal"
                          value={mode}
                          disabled={inserting}
                          onChange={(_, data) => setMode(data.value === 'replace' ? 'replace' : 'append')}
                        >
                          <Radio value="append" label="Add below what’s written" />
                          <Radio value="replace" label="Replace what’s written" />
                        </RadioGroup>
                      </Field>
                    )}
                    {SOAP_FIELDS.map((field) => {
                      const overflow = overflows.find((o) => o.field === field);
                      const status = sectionStatus(note[field], review[field], mode);
                      return (
                        <Field
                          key={field}
                          label={SOAP_LABELS[field]}
                          hint={
                            field === 'chiefComplaint' &&
                            suggestedComplaint &&
                            review.chiefComplaint === suggestedComplaint
                              ? `Taken from the first line of the history. ${status}`
                              : status
                          }
                          validationState={overflow ? 'error' : 'none'}
                          validationMessage={overflow ? overflowMessage(overflow, mode) : undefined}
                        >
                          <Textarea
                            value={review[field]}
                            onChange={(_, data) => setReview((current) => ({ ...current, [field]: data.value }))}
                            rows={ROWS[field]}
                            resize="vertical"
                            maxLength={NOTE_FIELD_LIMITS[field]}
                            readOnly={inserting}
                          />
                        </Field>
                      );
                    })}
                  </div>
                  <IcdSuggestions
                    suggestions={suggestions}
                    ticked={ticked}
                    recorded={recorded}
                    hasPrimary={hasPrimary}
                    disabled={inserting}
                    onTick={tick}
                  />
                </div>
                <Caption1 aria-live="polite" className={styles.muted}>
                  {insertHint(overflows.length > 0, nothingToInsert, codes.length)}
                </Caption1>
              </>
            )}
          </div>
        </DialogContent>
        {step === 'dictate' || !draft ? (
          <DialogActions>
            <Button onClick={requestClose}>Cancel</Button>
            {draft && (
              <Button disabled={making} onClick={() => setStep('review')}>
                Back to the draft
              </Button>
            )}
            <Button
              appearance="primary"
              icon={making ? <Spinner size="tiny" /> : <SparkleRegular />}
              disabled={making || inserting || listening || transcriptIssue !== null}
              onClick={() => void makeDraft()}
            >
              {making ? 'Making the draft…' : draft ? 'Make a new draft' : 'Make SOAP draft'}
            </Button>
          </DialogActions>
        ) : (
          <>
            <DialogActions position="start">
              <Button icon={<ArrowLeftRegular />} disabled={inserting} onClick={() => setStep('dictate')}>
                Back to dictation
              </Button>
            </DialogActions>
            <DialogActions position="end">
              <Button disabled={inserting} onClick={requestClose}>
                Cancel
              </Button>
              <Button
                appearance="primary"
                icon={inserting ? <Spinner size="tiny" /> : undefined}
                disabled={inserting || overflows.length > 0 || nothingToInsert}
                onClick={() => void insert()}
              >
                {inserting ? (codes.length > 0 ? 'Adding diagnoses…' : 'Inserting…') : 'Insert into note'}
              </Button>
            </DialogActions>
          </>
        )}
      </DialogBody>
      <ConfirmDialog
        open={confirmingClose}
        title="Discard this dictation?"
        confirmLabel="Discard"
        destructive
        onConfirm={close}
        onCancel={() => setConfirmingClose(false)}
      >
        {making ? 'The draft being made will be ignored. ' : ''}
        {draft ? 'Your dictation and the draft are' : 'Your dictation is'} not kept anywhere, and nothing has been added
        to the note.
      </ConfirmDialog>
    </DialogSurface>
  );
}

/** The microphone button and a status line read out to screen readers: listening, seconds recorded, transcribing. */
function VoiceControls({
  capture,
  mode,
  reason,
  takes,
  disabled,
}: {
  capture: VoiceCapture;
  mode: VoiceMode | null;
  reason: string | null;
  takes: number;
  disabled: boolean;
}) {
  const styles = useStyles();
  let status: ReactNode;
  if (capture.phase === 'starting') {
    status = <Caption1>Starting the microphone…</Caption1>;
  } else if (capture.phase === 'listening') {
    status =
      mode === 'server' ? (
        <Caption1>
          Listening… {capture.seconds} s of {capture.maxSeconds}. Choose Stop when you finish; at {capture.maxSeconds} s
          it stops by itself and you can speak again.
        </Caption1>
      ) : (
        <Caption1>Listening… speak now. It stops when you pause.</Caption1>
      );
  } else if (capture.phase === 'transcribing') {
    status = <Spinner size="extra-tiny" label="Turning your speech into text…" labelPosition="after" />;
  } else if (capture.error) {
    status = (
      <>
        <ErrorCircleRegular aria-hidden className={styles.voiceError} />
        <Caption1 className={styles.voiceError}>{capture.error}</Caption1>
      </>
    );
  } else if (!mode) {
    status = reason && <Caption1 className={styles.muted}>{reason}</Caption1>;
  } else if (takes > 0) {
    status = (
      <Caption1 className={styles.muted}>
        {takes === 1 ? '1 recording' : `${takes} recordings`} added at the end. Choose Speak to add more.
      </Caption1>
    );
  } else {
    status = (
      <Caption1 className={styles.muted}>
        Choose Speak and talk normally. Each recording is added at the end of the dictation.
      </Caption1>
    );
  }

  let button: ReactNode = null;
  if (mode === 'server' && capture.phase === 'listening') {
    button = (
      <Button icon={<RecordStopRegular />} aria-label="Stop recording" onClick={capture.stop}>
        Stop
      </Button>
    );
  } else if (mode) {
    button = (
      <Button
        icon={capture.phase === 'idle' ? <MicRegular /> : <Spinner size="tiny" />}
        disabled={disabled || capture.phase !== 'idle'}
        onClick={capture.start}
      >
        Speak
      </Button>
    );
  }

  return (
    <div className={styles.voice}>
      {button}
      <div className={styles.voiceLine} aria-live="polite">
        {status}
      </div>
    </div>
  );
}

/** The model's ICD-10 suggestions as checkboxes, none ticked to begin with. */
function IcdSuggestions({
  suggestions,
  ticked,
  recorded,
  hasPrimary,
  disabled,
  onTick,
}: {
  suggestions: readonly SuggestedIcdCode[];
  ticked: ReadonlySet<string>;
  recorded: ReadonlySet<string>;
  hasPrimary: boolean;
  disabled: boolean;
  onTick: (code: string, checked: boolean) => void;
}) {
  const styles = useStyles();
  const headingId = useId();
  return (
    <section className={styles.codes} aria-labelledby={headingId}>
      <Subtitle2 as="h4" id={headingId} className={styles.heading}>
        Suggested ICD-10 codes
      </Subtitle2>
      {suggestions.length === 0 ? (
        <Text className={styles.muted}>No codes were suggested. Add diagnoses from the Diagnoses section.</Text>
      ) : (
        <>
          <Caption1 className={styles.muted}>
            Tick the codes that apply. They are added as provisional diagnoses when you insert
            {hasPrimary ? '' : ', and the first one becomes the primary diagnosis'}. A code that is not in the
            hospital’s ICD list is skipped, and you are told which.
          </Caption1>
          <ul className={styles.codeList}>
            {suggestions.map((suggestion) => {
              const isRecorded = recorded.has(suggestion.code);
              return (
                <li key={suggestion.code}>
                  <Checkbox
                    checked={!isRecorded && ticked.has(suggestion.code)}
                    disabled={disabled || isRecorded}
                    onChange={(_, data) => onTick(suggestion.code, data.checked === true)}
                    label={
                      <span>
                        <span className={styles.code}>{suggestion.code}</span> {suggestion.title}
                        <Caption1 block className={styles.muted}>
                          {isRecorded
                            ? 'Already a diagnosis of this encounter'
                            : confidenceLabel(suggestion.confidence)}
                        </Caption1>
                      </span>
                    }
                  />
                </li>
              );
            })}
          </ul>
        </>
      )}
    </section>
  );
}
