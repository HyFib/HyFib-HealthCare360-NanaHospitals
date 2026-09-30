import type { ClinicalNoteType } from '@/modules/emr/api';
import type { AssistantCommandResponse } from './api';

/**
 * What the staff app does with the server's interpretation of a command (pure logic, unit-tested). The server never
 * executes anything: navigation happens straight away, anything with side effects is confirmed first and then opens
 * the normal screen with the values filled in, so the clinician reviews before saving.
 */

/** Example commands shown in the panel (docs/modules/ai.md). `text` is what a click puts in the text box. */
export const EXAMPLE_COMMANDS: readonly { label: string; text: string }[] = [
  { label: 'open patient UHID NH26000123', text: 'open patient UHID ' },
  { label: 'search patient 98765 43210', text: 'search patient ' },
  { label: "today's appointments", text: "today's appointments" },
  { label: 'appointments next Friday', text: 'appointments next Friday' },
  { label: 'book a follow-up in two weeks', text: 'book a follow-up in two weeks' },
  { label: 'create a progress note: …', text: 'create a progress note: ' },
  { label: 'start dictation', text: 'start dictation' },
  { label: 'go to billing', text: 'go to billing' },
];

export interface NoteDraft {
  type: ClinicalNoteType;
  title: string;
  body: string;
}

export interface PendingNote {
  patientId: string;
  draft: NoteDraft;
}

export type ReplyTone = 'info' | 'warning';

export type AssistantPlan =
  /** Open a screen now and close the panel. */
  | { kind: 'navigate'; href: string; message: string }
  /** Ask first; on Confirm open `href` (and keep `note` as the patient's unsaved note on this computer). */
  | { kind: 'confirm'; href: string; message: string; detail: string; note: PendingNote | null }
  /** Find the patient inside the panel (a name or mobile number never goes into the address bar). */
  | { kind: 'search'; term: string; openIfSingleMatch: boolean; message: string }
  /** Start voice capture for a clinical note. */
  | { kind: 'dictate'; patientId: string | null; message: string }
  /** Nothing to do: a question back, "not found", no access or not understood. */
  | { kind: 'message'; message: string; tone: ReplyTone };

export interface PlanEnvironment {
  /** True when the staff app has a screen at this path (router lookup, e.g. "/billing" or "/emr/patients/$patientId"). */
  hasRoute: (path: string) => boolean;
  /** True when the user may open the screen (module manifest permissions; screens without a menu entry are allowed). */
  canOpen: (path: string) => boolean;
  /** True when the user holds the permission. */
  can: (permission: string) => boolean;
  /** The signed-in user's linked doctor, for "my appointments". */
  practitionerId: string | null;
  /** The patient on screen (from the address), used when the answer carries none. */
  contextPatientId: string | null;
  /** Today in IST as yyyy-MM-dd. */
  today: string;
}

const PATIENTS_READ = 'patients.read';
const EMR_WRITE = 'clinical.emr.write';
const APPOINTMENTS = '/scheduling';
const BOOK_APPOINTMENT = '/scheduling/book';
const DOCTOR_WORKLIST = '/emr';
const PATIENT_CHART = '/emr/patients/$patientId';
const NOTE_MAX_LENGTH = 20_000;

const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const UUID_ANYWHERE = /[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/gi;
/** Every patient screen is ".../patients/$patientId/..." (/patients, /emr/patients, /dialysis/patients, …). */
const PATIENT_IN_PATH = /(?:^|\/)patients\/([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})(?=\/|$)/i;
const SAFE_PATH = /^\/[A-Za-z0-9/_-]*$/;

const NOT_UNDERSTOOD =
  'Sorry, I did not understand. Try “today’s appointments”, “go to billing” or “open patient UHID …”.';

/**
 * The server's screen list (NavigationTargets) uses a few paths this app names differently. A route the app does not
 * have is never opened.
 */
const ROUTE_ALIASES: Readonly<Record<string, string>> = {
  '/queue': '/front-office/queue',
  '/appointments': '/scheduling',
  '/appointments/new': '/scheduling/book',
};

const NOT_A_SCREEN: Readonly<Record<string, string>> = {
  '/notifications': 'Your notifications are under the bell at the top right of the screen.',
};

/** Spoken note types (AssistantIntentParser) → EMR note type and title. */
const NOTE_TYPES: Readonly<Record<string, { type: ClinicalNoteType; title: string }>> = {
  clinical: { type: 'ProgressNote', title: '' },
  progress: { type: 'ProgressNote', title: 'Progress note' },
  soap: { type: 'ProgressNote', title: 'Progress note' },
  consultation: { type: 'ProgressNote', title: 'Consultation note' },
  case: { type: 'ProgressNote', title: 'Case note' },
  nursing: { type: 'Other', title: 'Nursing note' },
  procedure: { type: 'Procedure', title: 'Procedure note' },
  operative: { type: 'Procedure', title: 'Operative note' },
  referral: { type: 'Referral', title: 'Referral' },
  discharge: { type: 'Other', title: 'Discharge note' },
};

export function isUuid(value: string | null | undefined): value is string {
  return typeof value === 'string' && UUID.test(value);
}

/** The patient on screen, taken from the address: "/patients/{id}", "/emr/patients/{id}/…". */
export function patientIdFromPath(pathname: string): string | null {
  return PATIENT_IN_PATH.exec(pathname)?.[1]?.toLowerCase() ?? null;
}

/** The route sent as context, with record ids replaced so no identifier travels beyond `patientId` (max 200 characters). */
export function contextRoute(pathname: string): string {
  return pathname.replace(UUID_ANYWHERE, '{id}').slice(0, 200);
}

/** A real calendar date written as yyyy-MM-dd. */
export function isIsoDate(value: string | null | undefined): value is string {
  if (typeof value !== 'string' || !/^\d{4}-\d{2}-\d{2}$/.test(value)) {
    return false;
  }
  const date = new Date(`${value}T00:00:00Z`);
  return !Number.isNaN(date.getTime()) && date.toISOString().startsWith(value);
}

/** Screens with a menu entry need one of that entry's permissions; a screen without one (Home) is allowed. */
export function canOpenPath(
  path: string,
  links: readonly { to: string; permission?: string | readonly string[] }[],
  has: (permission: string | readonly string[] | undefined) => boolean,
): boolean {
  const entries = links.filter((link) => link.to === path);
  return entries.length === 0 || entries.some((link) => has(link.permission));
}

/** Maps a server route to a screen of this app, or explains why it cannot be opened. */
export function resolveScreen(
  route: string | null | undefined,
  hasRoute: (path: string) => boolean,
): { path: string } | { problem: string } {
  const raw = (route ?? '').trim();
  const path = raw.length > 1 ? raw.replace(/\/+$/, '') : raw;
  if (!path || !SAFE_PATH.test(path) || path.includes('//')) {
    return { problem: 'That screen is not available here.' };
  }
  const special = NOT_A_SCREEN[path];
  if (special) {
    return { problem: special };
  }
  const candidates = [path, ROUTE_ALIASES[path]].filter((candidate): candidate is string => Boolean(candidate));
  const found = candidates.find((candidate) => hasRoute(candidate));
  return found ? { path: found } : { problem: 'That screen is not available in this version of HealthCare360.' };
}

/** The EMR note the clinician will review: type and title from the spoken note type, body from the dictated text. */
export function noteDraftFor(noteType: string | null | undefined, text: string | null | undefined): NoteDraft {
  const known = NOTE_TYPES[(noteType ?? '').trim().toLowerCase()] ?? { type: 'ProgressNote' as const, title: '' };
  return { type: known.type, title: known.title, body: (text ?? '').trim().slice(0, NOTE_MAX_LENGTH) };
}

function info(message: string): AssistantPlan {
  return { kind: 'message', message, tone: 'info' };
}

function noAccess(what: string): AssistantPlan {
  return {
    kind: 'message',
    tone: 'warning',
    message: `You do not have access to ${what}. Ask your administrator if you need it for your work.`,
  };
}

function planNote(patientId: string | null, draft: NoteDraft, message: string, env: PlanEnvironment): AssistantPlan {
  if (!patientId) {
    return info(message);
  }
  if (!env.can(EMR_WRITE)) {
    return { kind: 'message', tone: 'warning', message: 'You do not have permission to write clinical notes.' };
  }
  if (!env.hasRoute(PATIENT_CHART)) {
    return info('Open the patient’s chart to write the note.');
  }
  const hasText = draft.body.length > 0;
  return {
    kind: 'confirm',
    message,
    href: `/emr/patients/${patientId}?tab=notes`,
    note: hasText ? { patientId, draft } : null,
    detail: hasText
      ? 'The patient’s notes open with your text kept on this computer. Choose Write note, then Restore it, to review and save.'
      : 'The patient’s notes open so you can write it.',
  };
}

function planFor(intent: string, p: Record<string, string>, said: string, env: PlanEnvironment): AssistantPlan {
  switch (intent) {
    case 'navigate': {
      const screen = resolveScreen(p.route, env.hasRoute);
      if ('problem' in screen) {
        return info(screen.problem);
      }
      return env.canOpen(screen.path)
        ? { kind: 'navigate', href: screen.path, message: said }
        : noAccess(p.label?.trim() || 'that screen');
    }

    case 'open_patient': {
      const patientId = isUuid(p.patientId) ? p.patientId : null;
      if (!patientId) {
        // "No patient was found with UHID …" (patientFound = "false") or a question back.
        return { kind: 'message', message: said, tone: p.patientFound === 'false' ? 'warning' : 'info' };
      }
      return env.can(PATIENTS_READ)
        ? { kind: 'navigate', href: `/patients/${patientId}`, message: said }
        : noAccess('patient records');
    }

    case 'search_patient': {
      const term = (p.phone ?? p.query ?? '').trim();
      if (!term) {
        return info(said);
      }
      return env.can(PATIENTS_READ)
        ? { kind: 'search', term, openIfSingleMatch: p.openIfSingleMatch === 'true', message: said }
        : noAccess('patient records');
    }

    case 'list_appointments': {
      const date = isIsoDate(p.date) ? p.date : env.today;
      if (env.hasRoute(APPOINTMENTS) && env.canOpen(APPOINTMENTS)) {
        const search = new URLSearchParams({ date });
        if (p.scope === 'mine' && env.practitionerId) {
          search.set('practitionerId', env.practitionerId);
        }
        return { kind: 'navigate', href: `${APPOINTMENTS}?${search.toString()}`, message: said };
      }
      if (env.hasRoute(DOCTOR_WORKLIST) && env.canOpen(DOCTOR_WORKLIST)) {
        return {
          kind: 'navigate',
          href: `${DOCTOR_WORKLIST}?${new URLSearchParams({ date }).toString()}`,
          message: said,
        };
      }
      return noAccess('appointments');
    }

    case 'book_follow_up': {
      const patientId = isUuid(p.patientId) ? p.patientId : null;
      const date = isIsoDate(p.date) ? p.date : null;
      if (!patientId || !date) {
        // The server asks when ("follow-up in 2 weeks") or says to open the patient first.
        return info(said);
      }
      if (!env.hasRoute(BOOK_APPOINTMENT) || !env.canOpen(BOOK_APPOINTMENT)) {
        return noAccess('appointment booking');
      }
      return {
        kind: 'confirm',
        message: said,
        detail: 'The booking screen opens with this patient and date filled in. Choose the doctor and time, then save.',
        href: `${BOOK_APPOINTMENT}?${new URLSearchParams({ patientId, date }).toString()}`,
        note: null,
      };
    }

    case 'create_note':
      return planNote(isUuid(p.patientId) ? p.patientId : null, noteDraftFor(p.noteType, p.text), said, env);

    case 'start_dictation':
      return { kind: 'dictate', patientId: isUuid(p.patientId) ? p.patientId : env.contextPatientId, message: said };

    default:
      // "unknown" (including the quota message) and any intent this version does not know.
      return info(said);
  }
}

export function planAssistantAction(answer: AssistantCommandResponse, env: PlanEnvironment): AssistantPlan {
  const said = answer.confirmationText?.trim() || NOT_UNDERSTOOD;
  const plan = planFor(answer.intent, answer.parameters ?? {}, said, env);
  // Anything the server marks as needing confirmation is never done without asking.
  if (answer.requiresConfirmation && plan.kind === 'navigate') {
    return { kind: 'confirm', href: plan.href, message: plan.message, detail: '', note: null };
  }
  return plan;
}

/** "Start dictation" → the dictated (and edited) text becomes a clinical note for the patient on screen. */
export function planDictatedNote(text: string, patientId: string | null, env: PlanEnvironment): AssistantPlan {
  const body = text.trim();
  if (!body) {
    return info('Dictate or type the note first.');
  }
  if (!patientId) {
    return info('Open the patient’s record first, then create the note.');
  }
  return planNote(
    patientId,
    { type: 'ProgressNote', title: '', body: body.slice(0, NOTE_MAX_LENGTH) },
    'Create a clinical note with this text?',
    env,
  );
}
