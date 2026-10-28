/**
 * Answers the staff app's API calls in the browser from what the real server answered while capture.mjs walked each
 * role's screens. Reads are replayed; sign-in, refresh and sign-out work for the demo's roles; anything that would
 * change a record is refused with a "read-only demo" problem, which the app shows as it shows any refusal.
 */

/** One recorded answer: status, content type, content disposition, body encoding, and its body in the pool. */
export type Answer = [
  status: number,
  type: string | null,
  disposition: string | null,
  encoding: 'utf8' | 'base64',
  body: number | null,
];

export interface RoleRecording {
  role: string;
  userName: string;
  displayName: string;
  roleName: string;
  /** The sign-in answer for this role, its token replaced by a stand-in. */
  login: { accessToken: string; expiresAt: string; user: unknown } & Record<string, unknown>;
  /** What the role's workstation keeps in local storage (the queue desk, the ward, the counter). */
  desk: Record<string, string>;
  /** The patient's mobile number (+91…), for the role that is a patient on the portal: they sign in with it. */
  phone?: string;
  answers: Record<string, Answer>;
}

export interface DemoData {
  /** When the day was recorded, as an instant: the demo's clock starts here. */
  clockStart: string;
  roles: Record<string, RoleRecording>;
  bodies: string[];
}

export interface Miss {
  role: string | null;
  request: string;
  screen: string;
}

const ROLE_KEY = 'hc360-demo:role';
const SEARCH = new Set(['search', 'q', 'query', 'term', 'text', 'name', 'uhid', 'phone', 'mobile']);
const DATE = /^\d{4}-\d{2}-\d{2}/;
/**
 * Answers that are the same whoever asks (masters, settings, the system's own details, the public pages anyone may
 * open without signing in), which one role may borrow from another. Everything else is answered only from the role's
 * own recording, so the demo shows each role exactly what the server let it see (a masked phone number stays masked).
 */
const SAME_FOR_EVERYONE = /^\/api\/v1\/(masters|settings|system|print\/letterhead|public)(\/|$)/;

const SIGN_IN = { title: 'Please sign in', code: 'auth.required' };

/** The hospital's date (IST) on the demo's clock, which is a server's default for "today". */
const today = () => new Date(Date.now() + 330 * 60_000).toISOString().slice(0, 10);

export const READ_ONLY = {
  type: 'https://hyfib.com/problems/demo-read-only',
  title: 'This is a read-only demo',
  detail: 'The demo shows a recorded day at a sample hospital, so nothing you change here is saved.',
  code: 'demo.read_only',
};

const NOT_RECORDED = {
  type: 'https://hyfib.com/problems/demo-not-recorded',
  title: 'Not part of the demo recording',
  detail: 'The demo recorded the screens each role opens from its menu and the records they lead to; this one was not.',
  code: 'demo.not_recorded',
};

const WRONG_PASSWORD = {
  title: 'Wrong user name or password',
  detail: 'Sign in as one of the demo roles, such as doctor, nurse or frontoffice; any password works.',
  code: 'auth.invalid_credentials',
};

/** The last ten digits of a mobile number, however it was typed (+91, spaces, dashes). */
const mobile = (phone: string) => phone.replace(/\D/g, '').slice(-10);

/** Raised on window when the demo's role changes: signed in as one, or signed out. */
export const ROLE_CHANGED = 'hc360-demo:role';
/**
 * Raised on window, with the Miss as its detail, when a screen asks for something the walk did not record: the app
 * then shows its own "not found", which the demo's badge explains.
 */
export const MISSED = 'hc360-demo:missed';

/** The role signed in, kept for the tab (a reload stays signed in); in memory where the browser keeps no storage. */
let memoryRole: string | null = null;

export function currentRole(): string | null {
  try {
    return sessionStorage.getItem(ROLE_KEY);
  } catch {
    return memoryRole;
  }
}

export function setCurrentRole(role: string | null): void {
  memoryRole = role;
  try {
    if (role) {
      sessionStorage.setItem(ROLE_KEY, role);
    } else {
      sessionStorage.removeItem(ROLE_KEY);
    }
  } catch {
    // Private windows may refuse storage; the role then lasts until the page is reloaded.
  }
  window.dispatchEvent(new Event(ROLE_CHANGED));
}

function json(status: number, body: unknown, type = 'application/json; charset=utf-8'): Response {
  return new Response(status === 204 ? null : JSON.stringify(body), {
    status,
    headers: { 'content-type': type, 'x-hc360-demo': 'answered' },
  });
}

const problem = (status: number, body: object) =>
  json(status, { status, ...body }, 'application/problem+json; charset=utf-8');

function bytesOf(base64: string): Uint8Array<ArrayBuffer> {
  const text = atob(base64);
  const bytes = new Uint8Array(text.length);
  for (let i = 0; i < text.length; i++) {
    bytes[i] = text.charCodeAt(i);
  }
  return bytes;
}

/** The items of a list answer: the array itself, or a page's `items`. */
function itemsOf(value: unknown): unknown[] | null {
  if (Array.isArray(value)) return value;
  if (value && typeof value === 'object' && Array.isArray((value as { items?: unknown }).items)) {
    return (value as { items: unknown[] }).items;
  }
  return null;
}

function withItems(value: unknown, items: unknown[]): unknown {
  if (Array.isArray(value)) return items;
  const page = value as Record<string, unknown>;
  return { ...page, items, totalCount: items.length, totalPages: items.length > 0 ? 1 : 0 };
}

const mentions = (item: unknown, words: string) => JSON.stringify(item).toLowerCase().includes(words.toLowerCase());

/** How alike two queries are: each parameter the request shares counts for, each one it lacks or differs on against. */
function likeness(wanted: URLSearchParams, recorded: URLSearchParams): number {
  let score = 0;
  for (const [name, value] of wanted) {
    score += recorded.get(name) === value ? 2 : 0;
  }
  for (const [name, value] of recorded) {
    if (wanted.get(name) !== value) score -= 1;
  }
  return score;
}

export class Replay {
  readonly misses: Miss[] = [];
  /** Calls being answered, and when the last one was asked or answered: verify.mjs waits for the screen to settle. */
  inflight = 0;
  lastActivity = 0;
  private readonly byPath = new Map<string, { role: string; query: URLSearchParams; key: string }[]>();
  private data: DemoData = { clockStart: '', roles: {}, bodies: [] };
  private readonly ready: Promise<void>;

  /** `data` arrives once the page has loaded the files that hold it; calls made before then wait for it. */
  constructor(data: Promise<DemoData>) {
    this.ready = data.then((loaded) => {
      this.data = loaded;
      for (const [role, recording] of Object.entries(loaded.roles)) {
        for (const key of Object.keys(recording.answers)) {
          const [method, target] = splitKey(key);
          const url = new URL(target, 'https://demo.invalid');
          const path = `${method} ${url.pathname}`;
          const list = this.byPath.get(path) ?? [];
          list.push({ role, query: url.searchParams, key });
          this.byPath.set(path, list);
        }
      }
    });
  }

  get roles(): RoleRecording[] {
    return Object.values(this.data.roles);
  }

  roleByUserName(userName: string): RoleRecording | undefined {
    const name = userName.trim().toLowerCase();
    return this.roles.find((r) => r.userName.toLowerCase() === name || r.role === name);
  }

  /** The demo's patient with this mobile number, who signs in to the portal with it. */
  roleByPhone(phone: string): RoleRecording | undefined {
    const number = mobile(phone);
    return number.length === 10 ? this.roles.find((r) => r.phone && mobile(r.phone) === number) : undefined;
  }

  /** The sign-in answer for a role, valid for the rest of the demo day. */
  private signedIn(recording: RoleRecording): Response {
    const expiresAt = new Date(Date.now() + 12 * 3600_000).toISOString();
    return json(200, { ...recording.login, expiresAt });
  }

  async answer(method: string, url: URL, body: string | null): Promise<Response> {
    await this.ready;
    const role = currentRole();
    const recording = role ? this.data.roles[role] : undefined;
    const path = url.pathname;

    if (path === '/health/live' || path === '/health/ready') {
      return new Response('Healthy', { status: 200, headers: { 'content-type': 'text/plain' } });
    }
    if (path.startsWith('/api/v1/auth/')) {
      const action = path.slice('/api/v1/auth/'.length);
      if (action === 'login') {
        const { userName = '' } = (body ? JSON.parse(body) : {}) as { userName?: string };
        const chosen = this.roleByUserName(userName);
        if (!chosen) return problem(401, WRONG_PASSWORD);
        setCurrentRole(chosen.role);
        return this.signedIn(chosen);
      }
      if (action === 'refresh' || action === 'unlock' || action === 'unlock/webauthn') {
        return recording ? this.signedIn(recording) : problem(401, { title: 'Please sign in again' });
      }
      if (action === 'logout') {
        setCurrentRole(null);
        return json(204, null);
      }
      if (action === 'lock') return json(204, null);
      if (action === 'me' && recording) return json(200, recording.login.user);
      // The portal: a code "sent" to the demo patient's number, and any code accepted for it.
      if (action === 'otp/request') {
        const { phone = '' } = (body ? JSON.parse(body) : {}) as { phone?: string };
        const patient = this.roleByPhone(phone);
        if (!patient?.phone) {
          const numbers = this.roles.filter((r) => r.phone).map((r) => mobile(r.phone ?? ''));
          return problem(422, {
            title: 'Not a demo patient',
            detail: `In the demo, sign in with ${numbers.join(' or ')}; any code works.`,
            code: 'demo.not_a_patient',
          });
        }
        const number = mobile(patient.phone);
        return json(200, {
          challengeId: `demo-${patient.role}`,
          expiresAt: new Date(Date.now() + 10 * 60_000).toISOString(),
          maskedPhone: `+91 ${number.slice(0, 2)}XXX XX${number.slice(-3)}`,
        });
      }
      if (action === 'otp/verify') {
        const { challengeId = '' } = (body ? JSON.parse(body) : {}) as { challengeId?: string };
        const patient = this.data.roles[challengeId.replace(/^demo-/, '')];
        if (!patient?.phone) return problem(401, { title: 'Ask for a new code', code: 'auth.otp_invalid' });
        setCurrentRole(patient.role);
        return this.signedIn(patient);
      }
    }

    // The portal's check against automated sign-ins is a proof of work; the demo does not ask for one.
    if (path === '/api/v1/public/challenge') {
      return json(200, { required: false, challenge: null, bits: 0, expiresAt: null });
    }

    if (path === '/api/v1/settings/public') {
      // The demo never locks the screen for being left alone: there is no PIN to unlock it with.
      const recorded = this.find(role, 'GET', url);
      const settings = recorded ? (JSON.parse(this.text(recorded.answer)) as Record<string, unknown>) : {};
      return json(200, { ...settings, 'ui.idleLockMinutes': 0 });
    }

    const found = this.find(role, method, url);
    if (found) return this.respond(found.answer, found.adjust);
    // Signed out (the app may still be asking for what its last screen showed): the server answers "sign in".
    if (!recording) return problem(401, SIGN_IN);
    if (method !== 'GET') return problem(403, READ_ONLY);
    const screen = (window as { HC360_DEMO_HERE?: () => string }).HC360_DEMO_HERE?.() ?? '/';
    const miss: Miss = { role, request: `${method} ${path}${url.search}`, screen };
    this.misses.push(miss);
    window.dispatchEvent(new CustomEvent(MISSED, { detail: miss }));
    return problem(404, NOT_RECORDED);
  }

  private text(answer: Answer): string {
    return answer[4] === null ? '' : (this.data.bodies[answer[4]] ?? '');
  }

  private respond(answer: Answer, adjust?: (value: unknown) => unknown): Response {
    const [status, type, disposition, encoding] = answer;
    const headers = new Headers({ 'x-hc360-demo': 'recorded' });
    if (type) headers.set('content-type', type);
    if (disposition) headers.set('content-disposition', disposition);
    if (status === 204 || status === 304 || answer[4] === null) {
      return new Response(null, { status, headers });
    }
    if (encoding === 'base64') {
      return new Response(bytesOf(this.text(answer)), { status, headers });
    }
    const text = this.text(answer);
    if (adjust && (type ?? '').includes('json')) {
      return new Response(JSON.stringify(adjust(JSON.parse(text))), { status, headers });
    }
    return new Response(text, { status, headers });
  }

  /**
   * The recorded answer for a request: this role's, exactly or with its query in another order; then the same path
   * with the nearest query. A nearer query that searched for something else is filtered to the words searched for; one
   * for another day, or another page, is an empty list. Only answers the same for everyone come from other roles.
   */
  private find(
    role: string | null,
    method: string,
    url: URL,
  ): { answer: Answer; adjust?: (value: unknown) => unknown } | null {
    const key = `${method} ${url.pathname}${url.search}`;
    const mine = role ? this.data.roles[role] : undefined;
    if (mine?.answers[key]) return { answer: mine.answers[key] };
    const shared = SAME_FOR_EVERYONE.test(url.pathname);
    const candidates = (this.byPath.get(`${method} ${url.pathname}`) ?? []).filter((c) => c.role === role || shared);
    const sorted = [...url.searchParams].sort().toString();
    for (const candidate of candidates) {
      if ([...candidate.query].sort().toString() === sorted) {
        return { answer: this.data.roles[candidate.role]!.answers[candidate.key]! };
      }
    }
    if (method !== 'GET' || candidates.length === 0) return null;

    let best: (typeof candidates)[number] | null = null;
    let bestScore = -Infinity;
    for (const candidate of candidates) {
      const score = likeness(url.searchParams, candidate.query) + (candidate.role === role ? 0.5 : 0);
      if (score > bestScore) {
        best = candidate;
        bestScore = score;
      }
    }
    const answer = this.data.roles[best!.role]!.answers[best!.key]!;
    const recorded = best!.query;
    const searched: string[] = [];
    let elsewhere = false;
    for (const [name, value] of url.searchParams) {
      const was = recorded.get(name);
      // A first page, or today, asked for in so many words is what the recording got by leaving them out.
      if (was === value || (was === null && (value === today() || (name === 'page' && value === '1')))) continue;
      if (SEARCH.has(name) && value.trim()) searched.push(value.trim());
      if (DATE.test(value) || name === 'page') elsewhere = true;
    }
    return {
      answer,
      adjust: (value) => {
        let items = itemsOf(value);
        if (!items) return value;
        if (elsewhere) return withItems(value, []);
        for (const words of searched) {
          items = items.filter((item) => mentions(item, words));
        }
        return searched.length > 0 ? withItems(value, items) : value;
      },
    };
  }
}

function splitKey(key: string): [string, string] {
  const space = key.indexOf(' ');
  return [key.slice(0, space), key.slice(space + 1)];
}

/** Puts the replay in front of the network for the API, the health checks and the realtime hubs. */
export function installReplay(replay: Replay): void {
  const network = window.fetch.bind(window);
  window.fetch = async (input: RequestInfo | URL, init?: RequestInit) => {
    const request = input instanceof Request ? input : null;
    const url = new URL(request?.url ?? String(input), location.href);
    const ours =
      url.origin === location.origin &&
      (url.pathname.startsWith('/api/') || url.pathname.startsWith('/health/') || url.pathname.startsWith('/hubs/'));
    if (!ours) {
      return network(input, init);
    }
    const signal = init?.signal ?? request?.signal;
    if (signal?.aborted) {
      throw signal.reason ?? new DOMException('Aborted', 'AbortError');
    }
    const method = (init?.method ?? request?.method ?? 'GET').toUpperCase();
    const body = typeof init?.body === 'string' ? init.body : null;
    replay.inflight++;
    replay.lastActivity = performance.now();
    try {
      // Answered on a later turn, as a network answer would be, so the screens show what they show while loading.
      await new Promise((resolve) => setTimeout(resolve, 15));
      return await replay.answer(method, url, body);
    } finally {
      replay.inflight--;
      replay.lastActivity = performance.now();
    }
  };
}
