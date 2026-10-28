// The replay's rules (replay.ts), with Node's own test runner: node --test demo/
import assert from 'node:assert/strict';
import { beforeEach, describe, it } from 'node:test';
import { MISSED, Replay, type Answer, type DemoData, type RoleRecording } from './replay.ts';

// The browser globals the replay touches, as plain stand-ins.
const store = new Map<string, string>();
Object.assign(globalThis, {
  window: Object.assign(new EventTarget(), {}),
  location: { href: 'https://demo.invalid/', origin: 'https://demo.invalid', hash: '' },
  sessionStorage: {
    getItem: (key: string) => store.get(key) ?? null,
    setItem: (key: string, value: string) => store.set(key, value),
    removeItem: (key: string) => store.delete(key),
  },
});

const bodies: string[] = [];
const answer = (status: number, body: unknown): Answer => {
  bodies.push(JSON.stringify(body));
  return [status, 'application/json; charset=utf-8', null, 'utf8', bodies.length - 1];
};

function role(name: string, answers: Record<string, Answer>): RoleRecording {
  return {
    role: name,
    userName: name,
    displayName: `The ${name}`,
    roleName: name,
    login: { accessToken: 'demo-access-token', expiresAt: '', user: { userName: name, permissions: [] } },
    desk: {},
    answers,
  };
}

const PATIENTS = { items: [{ fullName: 'Meenakshi Sundaram' }, { fullName: 'Ravi Kumar' }], totalCount: 2 };
const data: DemoData = {
  clockStart: '2026-10-28T05:40:00Z',
  bodies,
  roles: {
    frontoffice: role('frontoffice', {
      'GET /api/v1/patients?page=1&pageSize=25': answer(200, PATIENTS),
      'GET /api/v1/patients/p1': answer(200, { phone: '+91 90000 01001' }),
      'GET /api/v1/masters/lookups/departments': answer(200, [{ code: 'GM' }]),
      'GET /api/v1/queue?departmentId=gm': answer(200, [{ label: 'GM-010' }]),
      'POST /api/v1/emergency/cases/c1/triage/preview': answer(200, { level: 2 }),
      'GET /api/v1/public/doctors': answer(200, [{ name: 'Dr. Arjun Mehta' }]),
    }),
    nurse: role('nurse', {}),
    patient: { ...role('patient', {}), phone: '+919000001021' },
  },
};

async function ask(replay: Replay, method: string, path: string, body: unknown = null) {
  const response = await replay.answer(
    method,
    new URL(path, 'https://demo.invalid'),
    body ? JSON.stringify(body) : null,
  );
  const text = await response.text();
  return { status: response.status, body: text ? JSON.parse(text) : null };
}

describe('the replay', () => {
  let replay: Replay;
  beforeEach(async () => {
    store.clear();
    replay = new Replay(Promise.resolve(data));
    await ask(replay, 'POST', '/api/v1/auth/login', { userName: 'frontoffice', password: 'anything' });
  });

  it('signs in as a demo role by its user name, with any password, and refuses other names', async () => {
    assert.equal(store.get('hc360-demo:role'), 'frontoffice');
    const refused = await ask(replay, 'POST', '/api/v1/auth/login', { userName: 'mallory', password: 'x' });
    assert.equal(refused.status, 401);
  });

  it("answers the role's own recording, whatever the order of the query", async () => {
    const answered = await ask(replay, 'GET', '/api/v1/patients?pageSize=25&page=1');
    assert.deepEqual(answered.body, PATIENTS);
  });

  it('filters a recorded list for a search the walk did not make', async () => {
    const answered = await ask(replay, 'GET', '/api/v1/patients?page=1&pageSize=25&search=meena');
    assert.deepEqual(
      answered.body.items.map((p: { fullName: string }) => p.fullName),
      ['Meenakshi Sundaram'],
    );
    assert.equal(answered.body.totalCount, 1);
  });

  it('answers a list for another day or a later page with nothing, and today as recorded', async () => {
    const otherDay = await ask(replay, 'GET', '/api/v1/queue?departmentId=gm&date=2026-10-27');
    assert.deepEqual(otherDay.body, []);
    const today = await ask(replay, 'GET', '/api/v1/queue?departmentId=gm&date=2026-10-28');
    assert.deepEqual(today.body, [{ label: 'GM-010' }]);
    const later = await ask(replay, 'GET', '/api/v1/patients?page=2&pageSize=25');
    assert.deepEqual(later.body.items, []);
  });

  it("lends masters to every role, but never one role's view of a record to another", async () => {
    await ask(replay, 'POST', '/api/v1/auth/login', { userName: 'nurse', password: 'x' });
    const masters = await ask(replay, 'GET', '/api/v1/masters/lookups/departments');
    assert.deepEqual(masters.body, [{ code: 'GM' }]);
    const record = await ask(replay, 'GET', '/api/v1/patients/p1');
    assert.equal(record.status, 404);
    assert.equal(record.body.code, 'demo.not_recorded');
    assert.deepEqual(
      replay.misses.map((m) => m.request),
      ['GET /api/v1/patients/p1'],
    );
  });

  it('tells the page when a screen asks for something the walk did not record', async () => {
    const told: unknown[] = [];
    const listen = (event: Event) => told.push((event as CustomEvent).detail);
    window.addEventListener(MISSED, listen);
    await ask(replay, 'GET', '/api/v1/patients/p2');
    window.removeEventListener(MISSED, listen);
    assert.deepEqual(told, [{ role: 'frontoffice', request: 'GET /api/v1/patients/p2', screen: '/' }]);
  });

  it('replays a recorded read that is a POST, and refuses every write', async () => {
    const preview = await ask(replay, 'POST', '/api/v1/emergency/cases/c1/triage/preview', { pulse: 118 });
    assert.deepEqual(preview.body, { level: 2 });
    const write = await ask(replay, 'POST', '/api/v1/visits', { patientId: 'p1' });
    assert.equal(write.status, 403);
    assert.equal(write.body.code, 'demo.read_only');
  });

  it('signs the demo patient in to the portal with any code, and refuses other numbers', async () => {
    const other = await ask(replay, 'POST', '/api/v1/auth/otp/request', { phone: '98765 43210' });
    assert.equal(other.status, 422);
    assert.match(other.body.detail, /9000001021/);
    const sent = await ask(replay, 'POST', '/api/v1/auth/otp/request', { phone: '90000 01021' });
    assert.equal(sent.status, 200);
    const verified = await ask(replay, 'POST', '/api/v1/auth/otp/verify', {
      challengeId: sent.body.challengeId,
      code: '000000',
    });
    assert.equal(verified.status, 200);
    assert.equal(store.get('hc360-demo:role'), 'patient');
    const check = await ask(replay, 'POST', '/api/v1/public/challenge', { action: 'otp', subject: null });
    assert.equal(check.body.required, false);
  });

  it('answers a signed-out page as the server does: public pages for anyone, the rest "sign in"', async () => {
    await ask(replay, 'POST', '/api/v1/auth/logout');
    const told: unknown[] = [];
    const listen = (event: Event) => told.push((event as CustomEvent).detail);
    window.addEventListener(MISSED, listen);
    const record = await ask(replay, 'GET', '/api/v1/patients/p1');
    const doctors = await ask(replay, 'GET', '/api/v1/public/doctors');
    window.removeEventListener(MISSED, listen);
    assert.equal(record.status, 401);
    assert.deepEqual(doctors.body, [{ name: 'Dr. Arjun Mehta' }]);
    assert.deepEqual(told, []);
  });

  it('turns the idle screen lock off and signs out', async () => {
    const settings = await ask(replay, 'GET', '/api/v1/settings/public');
    assert.equal(settings.body['ui.idleLockMinutes'], 0);
    await ask(replay, 'POST', '/api/v1/auth/logout');
    assert.equal(store.get('hc360-demo:role'), undefined);
    const refresh = await ask(replay, 'POST', '/api/v1/auth/refresh');
    assert.equal(refresh.status, 401);
  });
});
