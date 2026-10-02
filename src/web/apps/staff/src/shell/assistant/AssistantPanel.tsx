import {
  Badge,
  Button,
  Caption1,
  DrawerBody,
  DrawerFooter,
  DrawerHeader,
  DrawerHeaderTitle,
  Field,
  makeStyles,
  mergeClasses,
  MessageBar,
  MessageBarActions,
  MessageBarBody,
  OverlayDrawer,
  Spinner,
  Subtitle2,
  Text,
  Textarea,
  tokens,
  Tooltip,
} from '@fluentui/react-components';
import {
  DeleteRegular,
  DismissRegular,
  ErrorCircleRegular,
  MicRegular,
  PersonRegular,
  RecordStopRegular,
  SendRegular,
  SparkleRegular,
  WarningRegular,
} from '@fluentui/react-icons';
import { bridge, draftStorageKey, hasPermission, todayIso, useSession } from '@hc360/core';
import { useNotify } from '@hc360/ui';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useLocation, useNavigate, useRouter } from '@tanstack/react-router';
import {
  useEffect,
  useRef,
  useState,
  type Dispatch,
  type KeyboardEvent,
  type ReactNode,
  type SetStateAction,
} from 'react';
import { aiQueries } from '@/lib/ai';
import { useFeatureSwitches } from '@/lib/features';
import { FEATURE_OFF_TEXT, isFeatureOn } from '@/lib/featureSwitches';
import { moduleManifests } from '@/lib/moduleRegistry';
import { recorderSupported, voiceAvailability, type VoiceMode, type VoiceUse } from '@/lib/voice/recorder';
import { useVoiceCapture, type VoiceCapture } from '@/lib/voice/useVoiceCapture';
import { patientQueries, type PatientListItem } from '@/modules/patients/api';
import { sendAssistantCommand } from './api';
import { assistantErrorMessage } from './errors';
import {
  canOpenPath,
  contextRoute,
  EXAMPLE_COMMANDS,
  patientIdFromPath,
  planAssistantAction,
  planDictatedNote,
  switchedOffFeature,
  type AssistantPlan,
  type PlanEnvironment,
} from './intents';
import { emrNoteDraftName, keepNoteDraft } from './noteDraft';

/** One command and what came of it. Kept in memory for this sign-in only; never stored or logged. */
export interface AssistantExchange {
  id: string;
  said: string;
  state: 'working' | 'answered' | 'failed';
  reply?: string;
  tone?: 'info' | 'warning' | 'error';
  /** "rules" (offline parser) or "ai". */
  source?: string;
  plan?: AssistantPlan;
  searching?: boolean;
  matches?: PatientListItem[];
  matchTotal?: number;
  /** What happened after the answer ("Cancelled. Nothing was changed."). Hides the Confirm buttons. */
  outcome?: string;
}

const MAX_HISTORY = 20;
const COMMAND_MAX_LENGTH = 500;
const NOTE_MAX_LENGTH = 20_000;

/** A spoken command takes a few seconds, so a browser recording stops after 30. */
const ASSISTANT_VOICE: VoiceUse = {
  maxRecordingMs: 30_000,
  typeInstead: 'type the command',
  describeError: assistantErrorMessage,
};

/** Every screen and quick action with its permission, to check "go to billing" before opening it. */
const ALL_LINKS = moduleManifests.flatMap((module) => [...module.links, ...(module.quickActions ?? [])]);

const useStyles = makeStyles({
  muted: { color: tokens.colorNeutralForeground3 },
  headerActions: { display: 'flex', gap: '2px' },
  hints: { display: 'flex', flexDirection: 'column', gap: '8px', paddingTop: '4px' },
  hintList: {
    listStyle: 'none',
    margin: 0,
    padding: 0,
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'flex-start',
    gap: '2px',
  },
  hint: { justifyContent: 'flex-start', textAlign: 'start', fontWeight: tokens.fontWeightRegular, minWidth: 0 },
  log: { listStyle: 'none', margin: 0, padding: 0, display: 'flex', flexDirection: 'column', gap: '14px' },
  exchange: { display: 'flex', flexDirection: 'column', gap: '6px' },
  said: {
    alignSelf: 'flex-end',
    maxWidth: '85%',
    padding: '8px 12px',
    borderRadius: tokens.borderRadiusLarge,
    backgroundColor: tokens.colorBrandBackground2,
    whiteSpace: 'pre-wrap',
    wordBreak: 'break-word',
  },
  reply: {
    alignSelf: 'flex-start',
    maxWidth: '92%',
    padding: '8px 12px',
    borderRadius: tokens.borderRadiusLarge,
    backgroundColor: tokens.colorNeutralBackground3,
    display: 'flex',
    flexDirection: 'column',
    gap: '6px',
    wordBreak: 'break-word',
  },
  replyWarning: { borderLeft: `3px solid ${tokens.colorPaletteMarigoldBorderActive}` },
  replyError: { borderLeft: `3px solid ${tokens.colorPaletteRedBorder2}` },
  replyText: { display: 'flex', gap: '6px', alignItems: 'flex-start' },
  toneIcon: { flexShrink: 0, marginTop: '2px', fontSize: '16px' },
  aiBadge: { alignSelf: 'flex-start' },
  confirmButtons: { display: 'flex', gap: '8px', flexWrap: 'wrap' },
  matches: { listStyle: 'none', margin: 0, padding: 0, display: 'flex', flexDirection: 'column', gap: '2px' },
  match: {
    width: '100%',
    justifyContent: 'flex-start',
    textAlign: 'start',
    height: 'auto',
    padding: '6px 8px',
    fontWeight: tokens.fontWeightRegular,
  },
  matchText: { display: 'flex', flexDirection: 'column', minWidth: 0 },
  footer: { display: 'flex', flexDirection: 'column', alignItems: 'stretch', gap: '8px' },
  row: { display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: '8px', flexWrap: 'wrap' },
  voiceLine: { flex: '1 1 180px', minWidth: 0, display: 'flex', alignItems: 'center', gap: '6px' },
  voiceError: { color: tokens.colorPaletteRedForeground1 },
  buttons: { display: 'flex', gap: '8px', marginLeft: 'auto' },
});

function toneOf(plan: AssistantPlan): AssistantExchange['tone'] {
  return plan.kind === 'message' ? plan.tone : 'info';
}

function localStorageOrNull(): Storage | null {
  try {
    return window.localStorage;
  } catch {
    return null;
  }
}

/**
 * The assistant panel (SRS AIV-01..03): type or speak a command; the server interprets it and this panel acts on the
 * answer. Screens open straight away; bookings and notes are confirmed first and then open the normal screen with the
 * details filled in, so nothing is saved without the clinician.
 */
export function AssistantPanel({
  open,
  onClose,
  history,
  onHistoryChange,
}: {
  open: boolean;
  onClose: () => void;
  history: AssistantExchange[];
  onHistoryChange: Dispatch<SetStateAction<AssistantExchange[]>>;
}) {
  const styles = useStyles();
  const navigate = useNavigate();
  const router = useRouter();
  const { pathname } = useLocation();
  const user = useSession((s) => s.user);
  const features = useFeatureSwitches();
  const queryClient = useQueryClient();
  const notify = useNotify();
  const status = useQuery({ ...aiQueries.status(), enabled: open });
  const [canRecord] = useState(recorderSupported);
  const [text, setText] = useState('');
  const [dictation, setDictation] = useState<{ patientId: string | null } | null>(null);
  const [busy, setBusy] = useState(false);
  const inputRef = useRef<HTMLTextAreaElement>(null);
  const endRef = useRef<HTMLDivElement>(null);
  // An answer that arrives after the panel was closed must not open a screen or start the microphone.
  const openRef = useRef(open);
  const contextPatientId = patientIdFromPath(pathname);

  useEffect(() => {
    openRef.current = open;
  }, [open]);

  const voice = voiceAvailability(
    {
      isDesktop: bridge.isDesktop,
      recorderSupported: canRecord,
      status: status.data,
      statusFailed: status.isError,
    },
    ASSISTANT_VOICE,
  );
  const capture = useVoiceCapture(
    voice.mode,
    dictation?.patientId ?? contextPatientId,
    (heard) => {
      setText((current) => (current.trim() ? `${current.trimEnd()} ${heard}` : heard));
      inputRef.current?.focus();
    },
    ASSISTANT_VOICE,
  );

  // Keep the latest answer in view.
  useEffect(() => {
    if (open) {
      endRef.current?.scrollIntoView({ block: 'end' });
    }
  }, [open, history.length]);

  const environment = (): PlanEnvironment => ({
    hasRoute: (path) => Object.hasOwn(router.routesByPath, path),
    canOpen: (path) =>
      canOpenPath(
        path,
        ALL_LINKS,
        (permission) => hasPermission(user, permission),
        (feature) => isFeatureOn(features, feature),
      ),
    switchedOff: (path) => {
      const feature = switchedOffFeature(path, ALL_LINKS, (f) => isFeatureOn(features, f));
      return feature ? `${FEATURE_OFF_TEXT[feature].title}.` : null;
    },
    can: (permission) => hasPermission(user, permission),
    practitionerId: user?.practitionerId ?? null,
    contextPatientId,
    today: todayIso(),
  });

  const add = (exchange: AssistantExchange) => onHistoryChange((all) => [...all, exchange].slice(-MAX_HISTORY));
  const update = (id: string, patch: Partial<AssistantExchange>) =>
    onHistoryChange((all) => all.map((item) => (item.id === id ? { ...item, ...patch } : item)));

  const close = () => {
    capture.cancel();
    onClose();
  };

  /** Opens a screen and closes the panel; does nothing (returns false) once the panel has been closed. */
  const go = (href: string): boolean => {
    if (!openRef.current) {
      return false;
    }
    close();
    void navigate({ href });
    return true;
  };
  const NOT_DONE = 'Not done, because the assistant was closed. Ask again when you need it.';

  const findPatients = async (id: string, plan: Extract<AssistantPlan, { kind: 'search' }>) => {
    update(id, { searching: true });
    try {
      const result = await queryClient.fetchQuery({
        ...patientQueries.search({ search: plan.term, page: 1, pageSize: 6 }),
        staleTime: 10_000,
      });
      const [only] = result.items;
      if (plan.openIfSingleMatch && result.totalCount === 1 && only && openRef.current) {
        update(id, { searching: false, outcome: `Opening ${only.fullName}.` });
        go(`/patients/${only.id}`);
        return;
      }
      update(id, {
        searching: false,
        matches: result.items,
        matchTotal: result.totalCount,
        outcome:
          result.totalCount === 0
            ? 'No patients match. Check the UHID or mobile number, or try part of the name.'
            : undefined,
      });
    } catch (error) {
      update(id, { searching: false, tone: 'error', outcome: assistantErrorMessage(error) });
    }
  };

  const carryOut = async (id: string, plan: AssistantPlan, mode: VoiceMode | null) => {
    switch (plan.kind) {
      case 'navigate':
        if (!go(plan.href)) {
          update(id, { outcome: NOT_DONE });
        }
        break;
      case 'search':
        await findPatients(id, plan);
        break;
      case 'dictate':
        if (!openRef.current) {
          update(id, { outcome: NOT_DONE });
          break;
        }
        setDictation({ patientId: plan.patientId });
        if (mode) {
          capture.start();
        } else {
          update(id, { outcome: 'Voice input is not available here, so type the note below.' });
        }
        break;
      default:
        break;
    }
  };

  const send = async () => {
    const said = text.trim();
    if (!said || busy) {
      return;
    }
    const env = environment();

    if (dictation) {
      // The dictated text becomes a note for the patient it was started for; never for a different patient.
      const plan: AssistantPlan =
        dictation.patientId && dictation.patientId !== contextPatientId
          ? {
              kind: 'message',
              tone: 'warning',
              message: 'The patient on screen has changed. Open that patient’s record again, or stop dictating.',
            }
          : planDictatedNote(said, dictation.patientId ?? contextPatientId, env);
      add({ id: crypto.randomUUID(), said, state: 'answered', reply: plan.message, tone: toneOf(plan), plan });
      if (plan.kind === 'confirm') {
        setDictation(null);
        setText('');
      }
      return;
    }

    const id = crypto.randomUUID();
    setText('');
    add({ id, said, state: 'working' });
    setBusy(true);
    try {
      const answer = await sendAssistantCommand({
        text: said,
        context: { route: contextRoute(pathname), patientId: contextPatientId },
      });
      const plan = planAssistantAction(answer, env);
      update(id, { state: 'answered', reply: plan.message, tone: toneOf(plan), source: answer.source, plan });
      await carryOut(id, plan, voice.mode);
    } catch (error) {
      update(id, { state: 'failed', reply: assistantErrorMessage(error), tone: 'error' });
    } finally {
      setBusy(false);
      inputRef.current?.focus();
    }
  };

  const confirm = (exchange: AssistantExchange) => {
    const plan = exchange.plan;
    if (plan?.kind !== 'confirm' || exchange.outcome) {
      return;
    }
    if (plan.note) {
      const kept = user
        ? keepNoteDraft(
            localStorageOrNull(),
            draftStorageKey(user.id, emrNoteDraftName(plan.note.patientId)),
            plan.note.draft,
          )
        : false;
      if (!kept) {
        update(exchange.id, {
          outcome: 'The note could not be kept on this computer. Copy the text above, then open the patient’s notes.',
          tone: 'warning',
        });
        return;
      }
      notify.info(
        'Your note is ready to review',
        'Choose Write note, then Restore it. Nothing is saved until you choose Save note.',
      );
    }
    update(exchange.id, { outcome: 'Opened for you to review.' });
    go(plan.href);
  };

  const cancel = (exchange: AssistantExchange) => update(exchange.id, { outcome: 'Cancelled. Nothing was changed.' });

  const pickExample = (example: string) => {
    setText(example);
    inputRef.current?.focus();
  };

  const onKeyDown = (event: KeyboardEvent<HTMLTextAreaElement>) => {
    if (event.key !== 'Enter' || event.shiftKey || event.nativeEvent.isComposing) {
      return;
    }
    // In a note, Enter starts a new line and Ctrl+Enter creates the note.
    if (dictation && !(event.ctrlKey || event.metaKey)) {
      return;
    }
    event.preventDefault();
    void send();
  };

  return (
    <OverlayDrawer
      open={open}
      position="end"
      size="medium"
      onOpenChange={(_, data) => !data.open && close()}
      aria-label="Assistant"
    >
      <DrawerHeader>
        <DrawerHeaderTitle
          action={
            <div className={styles.headerActions}>
              {history.length > 0 && (
                <Tooltip content="Clear this conversation" relationship="label">
                  <Button
                    appearance="subtle"
                    icon={<DeleteRegular />}
                    aria-label="Clear this conversation"
                    onClick={() => onHistoryChange([])}
                  />
                </Tooltip>
              )}
              <Button appearance="subtle" icon={<DismissRegular />} aria-label="Close the assistant" onClick={close} />
            </div>
          }
        >
          Assistant
        </DrawerHeaderTitle>
        <Caption1 className={styles.muted}>
          Say or type what you need. The assistant opens screens and fills in details; you check and save.
        </Caption1>
        {status.data && !status.data.enabled && (
          <Caption1 className={styles.muted}>AI help is off, so only everyday commands are understood.</Caption1>
        )}
      </DrawerHeader>

      <DrawerBody>
        {history.length === 0 ? (
          <section className={styles.hints} aria-label="Examples">
            <Subtitle2 as="h2">Try saying</Subtitle2>
            <ul className={styles.hintList}>
              {EXAMPLE_COMMANDS.map((example) => (
                <li key={example.label}>
                  <Button
                    appearance="transparent"
                    size="small"
                    className={styles.hint}
                    onClick={() => pickExample(example.text)}
                  >
                    “{example.label}”
                  </Button>
                </li>
              ))}
            </ul>
            <Caption1 className={styles.muted}>
              Ctrl+Space opens the assistant on any screen. Enter sends, Esc closes.
            </Caption1>
          </section>
        ) : (
          <ol className={styles.log} aria-label="Conversation" aria-live="polite">
            {history.map((exchange) => (
              <ExchangeItem
                key={exchange.id}
                exchange={exchange}
                onConfirm={confirm}
                onCancel={cancel}
                onOpenPatient={(id) => go(`/patients/${id}`)}
              />
            ))}
          </ol>
        )}
        <div ref={endRef} />
      </DrawerBody>

      <DrawerFooter className={styles.footer}>
        {dictation && (
          <MessageBar intent="info" layout="multiline">
            <MessageBarBody>
              {dictation.patientId
                ? 'Dictating a clinical note for the patient on screen.'
                : 'Dictating a clinical note.'}{' '}
              Check the text, then choose Create note.
            </MessageBarBody>
            <MessageBarActions>
              <Button size="small" onClick={() => setDictation(null)}>
                Stop dictating
              </Button>
            </MessageBarActions>
          </MessageBar>
        )}
        <Field
          label={dictation ? 'Note' : 'Command'}
          hint={dictation ? 'Enter starts a new line; Ctrl+Enter creates the note.' : undefined}
        >
          <Textarea
            ref={inputRef}
            value={text}
            onChange={(_, data) => setText(data.value)}
            onKeyDown={onKeyDown}
            autoFocus
            resize="vertical"
            rows={dictation ? 6 : 2}
            maxLength={dictation ? NOTE_MAX_LENGTH : COMMAND_MAX_LENGTH}
            placeholder={dictation ? 'Speak or type the note' : 'For example: today’s appointments'}
          />
        </Field>
        <div className={styles.row}>
          <VoiceLine capture={capture} mode={voice.mode} reason={voice.reason} />
          <div className={styles.buttons}>
            {voice.mode && (
              <MicButton capture={capture} mode={voice.mode} dictating={Boolean(dictation)} disabled={busy} />
            )}
            <Button
              appearance="primary"
              icon={busy ? <Spinner size="tiny" /> : <SendRegular />}
              disabled={!text.trim() || busy}
              onClick={() => void send()}
            >
              {dictation ? 'Create note' : 'Send'}
            </Button>
          </div>
        </div>
      </DrawerFooter>
    </OverlayDrawer>
  );
}

function MicButton({
  capture,
  mode,
  dictating,
  disabled,
}: {
  capture: VoiceCapture;
  mode: VoiceMode;
  dictating: boolean;
  disabled: boolean;
}) {
  if (capture.phase === 'listening' && mode === 'server') {
    return (
      <Button icon={<RecordStopRegular />} onClick={capture.stop} aria-label="Stop recording">
        Stop
      </Button>
    );
  }
  const label = dictating ? 'Dictate the note' : 'Speak a command';
  return (
    <Tooltip content={label} relationship="label">
      <Button
        icon={capture.phase === 'idle' ? <MicRegular /> : <Spinner size="tiny" />}
        aria-label={label}
        onClick={capture.start}
        disabled={disabled || capture.phase !== 'idle'}
      />
    </Tooltip>
  );
}

function VoiceLine({
  capture,
  mode,
  reason,
}: {
  capture: VoiceCapture;
  mode: VoiceMode | null;
  reason: string | null;
}) {
  const styles = useStyles();
  let content: ReactNode = null;
  if (capture.phase === 'starting') {
    content = <Caption1>Starting the microphone…</Caption1>;
  } else if (capture.phase === 'listening') {
    content =
      mode === 'server' ? (
        <Caption1>
          Listening… {capture.seconds} s of {capture.maxSeconds}. Choose Stop when you have finished.
        </Caption1>
      ) : (
        <Caption1>Listening… speak now. It stops when you pause.</Caption1>
      );
  } else if (capture.phase === 'transcribing') {
    content = <Spinner size="extra-tiny" label="Turning speech into text…" labelPosition="after" />;
  } else if (capture.error) {
    content = (
      <>
        <ErrorCircleRegular aria-hidden className={styles.voiceError} />
        <Caption1 className={styles.voiceError}>{capture.error}</Caption1>
      </>
    );
  } else if (!mode && reason) {
    content = <Caption1 className={styles.muted}>{reason}</Caption1>;
  }
  return (
    <div className={styles.voiceLine} aria-live="polite">
      {content}
    </div>
  );
}

function ExchangeItem({
  exchange,
  onConfirm,
  onCancel,
  onOpenPatient,
}: {
  exchange: AssistantExchange;
  onConfirm: (exchange: AssistantExchange) => void;
  onCancel: (exchange: AssistantExchange) => void;
  onOpenPatient: (patientId: string) => void;
}) {
  const styles = useStyles();
  const plan = exchange.plan;
  return (
    <li className={styles.exchange}>
      <div className={styles.said}>
        <Text>{exchange.said}</Text>
      </div>
      {exchange.state === 'working' ? (
        <Spinner size="extra-tiny" label="Working on it…" labelPosition="after" />
      ) : (
        <div
          className={mergeClasses(
            styles.reply,
            exchange.tone === 'warning' && styles.replyWarning,
            exchange.tone === 'error' && styles.replyError,
          )}
        >
          {exchange.source === 'ai' && (
            <Badge appearance="tint" color="brand" size="small" icon={<SparkleRegular />} className={styles.aiBadge}>
              Understood by AI
            </Badge>
          )}
          <span className={styles.replyText}>
            {exchange.tone === 'warning' && <WarningRegular aria-hidden className={styles.toneIcon} />}
            {exchange.tone === 'error' && <ErrorCircleRegular aria-hidden className={styles.toneIcon} />}
            <Text>{exchange.reply}</Text>
          </span>
          {plan?.kind === 'confirm' && !exchange.outcome && (
            <>
              {plan.detail && <Caption1 className={styles.muted}>{plan.detail}</Caption1>}
              <div className={styles.confirmButtons}>
                <Button appearance="primary" size="small" onClick={() => onConfirm(exchange)}>
                  Confirm
                </Button>
                <Button size="small" onClick={() => onCancel(exchange)}>
                  Cancel
                </Button>
              </div>
            </>
          )}
          {exchange.searching && <Spinner size="extra-tiny" label="Searching patients…" labelPosition="after" />}
          {exchange.matches && exchange.matches.length > 0 && (
            <>
              <ul className={styles.matches} aria-label="Matching patients">
                {exchange.matches.map((patient) => (
                  <li key={patient.id}>
                    <Button
                      appearance="subtle"
                      className={styles.match}
                      icon={<PersonRegular />}
                      onClick={() => onOpenPatient(patient.id)}
                    >
                      <span className={styles.matchText}>
                        <Text weight="semibold">{patient.fullName}</Text>
                        <Caption1 className={styles.muted}>
                          {[
                            patient.uhid,
                            patient.age && `${patient.age} ${patient.gender.charAt(0)}`,
                            patient.phoneMasked,
                          ]
                            .filter(Boolean)
                            .join(' · ')}
                        </Caption1>
                      </span>
                    </Button>
                  </li>
                ))}
              </ul>
              {(exchange.matchTotal ?? 0) > exchange.matches.length && (
                <Caption1 className={styles.muted}>
                  Showing {exchange.matches.length} of {exchange.matchTotal}. Say more of the name, or the full mobile
                  number.
                </Caption1>
              )}
            </>
          )}
          {exchange.outcome && <Caption1 className={styles.muted}>{exchange.outcome}</Caption1>}
        </div>
      )}
    </li>
  );
}
