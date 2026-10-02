import { describe, expect, it } from 'vitest';
import type { AssistantCommandResponse } from './api';
import {
  canOpenPath,
  contextRoute,
  isIsoDate,
  noteDraftFor,
  patientIdFromPath,
  planAssistantAction,
  planDictatedNote,
  resolveScreen,
  switchedOffFeature,
  type PlanEnvironment,
} from './intents';

const PATIENT = '3f2b8c1e-4a5d-4e6f-9a7b-1c2d3e4f5a6b';
const OTHER = '9a8b7c6d-5e4f-4a3b-8c2d-1e0f9a8b7c6d';
const DOCTOR = '0b1c2d3e-4f5a-4b6c-9d7e-8f9a0b1c2d3e';

const ROUTES = new Set([
  '/',
  '/billing',
  '/front-office/queue',
  '/scheduling',
  '/scheduling/book',
  '/emr',
  '/emr/patients/$patientId',
  '/patients',
  '/admin/settings',
]);

function env(overrides: Partial<PlanEnvironment> = {}): PlanEnvironment {
  return {
    hasRoute: (path) => ROUTES.has(path),
    canOpen: () => true,
    can: () => true,
    practitionerId: null,
    contextPatientId: null,
    today: '2026-09-28',
    ...overrides,
  };
}

function answer(
  intent: string,
  parameters: Record<string, string>,
  confirmationText: string,
  requiresConfirmation = false,
): AssistantCommandResponse {
  return { intent, parameters, confirmationText, requiresConfirmation, source: 'rules' };
}

describe('patient context', () => {
  it('reads the patient id from patient screens only', () => {
    expect(patientIdFromPath(`/patients/${PATIENT}`)).toBe(PATIENT);
    expect(patientIdFromPath(`/patients/${PATIENT}/edit`)).toBe(PATIENT);
    expect(patientIdFromPath(`/emr/patients/${PATIENT}/discharge/${OTHER}`)).toBe(PATIENT);
    expect(patientIdFromPath(`/dialysis/patients/${PATIENT.toUpperCase()}`)).toBe(PATIENT);
    expect(patientIdFromPath('/patients/register')).toBeNull();
    expect(patientIdFromPath('/patients')).toBeNull();
    expect(patientIdFromPath(`/lab/orders/${PATIENT}`)).toBeNull();
    expect(patientIdFromPath(`/patients/${PATIENT}x`)).toBeNull();
  });

  it('sends the route with record ids replaced', () => {
    expect(contextRoute(`/emr/patients/${PATIENT}/discharge/${OTHER}`)).toBe('/emr/patients/{id}/discharge/{id}');
    expect(contextRoute('/billing')).toBe('/billing');
    expect(contextRoute(`/${'a'.repeat(300)}`)).toHaveLength(200);
  });

  it('accepts only real calendar dates', () => {
    expect(isIsoDate('2026-10-12')).toBe(true);
    expect(isIsoDate('2026-02-30')).toBe(false);
    expect(isIsoDate('12/10/2026')).toBe(false);
    expect(isIsoDate(undefined)).toBe(false);
  });
});

describe('screens', () => {
  it('opens screens the app has, mapping the server names this app spells differently', () => {
    const has = (path: string) => ROUTES.has(path);
    expect(resolveScreen('/billing', has)).toEqual({ path: '/billing' });
    expect(resolveScreen('/billing/', has)).toEqual({ path: '/billing' });
    expect(resolveScreen('/queue', has)).toEqual({ path: '/front-office/queue' });
    expect(resolveScreen('/appointments', has)).toEqual({ path: '/scheduling' });
    expect(resolveScreen('/appointments/new', has)).toEqual({ path: '/scheduling/book' });
    expect(resolveScreen('/', has)).toEqual({ path: '/' });
  });

  it('never opens unknown or unsafe routes', () => {
    const has = (path: string) => ROUTES.has(path);
    expect(resolveScreen('/reports', has)).toHaveProperty('problem');
    expect(resolveScreen('/notifications', has)).toEqual({ problem: expect.stringContaining('bell') });
    expect(resolveScreen('https://evil.example/billing', has)).toHaveProperty('problem');
    expect(resolveScreen('//evil.example', has)).toHaveProperty('problem');
    expect(resolveScreen('/patients/$patientId', has)).toHaveProperty('problem');
    expect(resolveScreen('', has)).toHaveProperty('problem');
    expect(resolveScreen(undefined, has)).toHaveProperty('problem');
  });

  it('checks the menu permission of a screen', () => {
    const links = [
      { to: '/billing', permission: 'billing.read' },
      { to: '/scheduling/book', permission: 'scheduling.appointments.book' },
      { to: '/scheduling/book', permission: ['front.desk'] },
    ];
    const has = (permission: string | readonly string[] | undefined) =>
      permission === 'front.desk' || (Array.isArray(permission) && permission.includes('front.desk'));
    expect(canOpenPath('/billing', links, has)).toBe(false);
    expect(canOpenPath('/scheduling/book', links, has)).toBe(true);
    expect(canOpenPath('/', links, has)).toBe(true);
  });

  it('keeps a screen closed while its feature is switched off', () => {
    const links = [
      { to: '/telehealth', permission: 'telehealth.sessions.read', feature: 'telehealth' as const },
      { to: '/rpm', permission: 'rpm.read' },
    ];
    const videoOff = (feature: string | undefined) => feature !== 'telehealth';
    expect(canOpenPath('/telehealth', links, () => true, videoOff)).toBe(false);
    expect(canOpenPath('/rpm', links, () => true, videoOff)).toBe(true);
    expect(canOpenPath('/telehealth', links, () => true)).toBe(true);
    expect(switchedOffFeature('/telehealth', links, videoOff)).toBe('telehealth');
    expect(switchedOffFeature('/rpm', links, videoOff)).toBeNull();
    expect(switchedOffFeature('/telehealth', links, () => true)).toBeNull();
  });
});

describe('planAssistantAction', () => {
  it('navigates straight to a screen', () => {
    expect(
      planAssistantAction(answer('navigate', { route: '/billing', label: 'Billing' }, 'Opening Billing.'), env()),
    ).toEqual({
      kind: 'navigate',
      href: '/billing',
      message: 'Opening Billing.',
    });
    expect(
      planAssistantAction(answer('navigate', { route: '/queue', label: 'Queue' }, 'Opening Queue.'), env()),
    ).toMatchObject({ kind: 'navigate', href: '/front-office/queue' });
  });

  it('says when a screen is switched off rather than that the user may not open it', () => {
    const plan = planAssistantAction(
      answer('navigate', { route: '/billing', label: 'Billing' }, 'Opening Billing.'),
      env({ canOpen: () => false, switchedOff: () => 'Video consultations are turned off.' }),
    );
    expect(plan).toEqual({ kind: 'message', tone: 'info', message: 'Video consultations are turned off.' });
  });

  it('explains when the user may not open the screen', () => {
    const plan = planAssistantAction(
      answer('navigate', { route: '/billing', label: 'Billing' }, 'Opening Billing.'),
      env({ canOpen: () => false }),
    );
    expect(plan).toEqual({
      kind: 'message',
      tone: 'warning',
      message: expect.stringContaining('You do not have access to Billing'),
    });
  });

  it('opens a patient found by UHID, by id only', () => {
    const plan = planAssistantAction(
      answer(
        'open_patient',
        { uhid: 'NH26000123', patientFound: 'true', patientId: PATIENT },
        'Opening patient NH26000123.',
      ),
      env(),
    );
    expect(plan).toEqual({ kind: 'navigate', href: `/patients/${PATIENT}`, message: 'Opening patient NH26000123.' });
  });

  it('says so when no patient has the UHID', () => {
    const plan = planAssistantAction(
      answer(
        'open_patient',
        { uhid: 'NH26999999', patientFound: 'false' },
        'No patient was found with UHID NH26999999.',
      ),
      env(),
    );
    expect(plan).toEqual({ kind: 'message', tone: 'warning', message: 'No patient was found with UHID NH26999999.' });
  });

  it('ignores a patient id that is not an id', () => {
    const plan = planAssistantAction(
      answer('open_patient', { patientId: '../admin' }, 'Opening the current patient’s record.'),
      env(),
    );
    expect(plan.kind).toBe('message');
  });

  it('searches patients inside the panel instead of the address bar', () => {
    expect(
      planAssistantAction(
        answer('search_patient', { phone: '+919876543210' }, 'Searching patients with mobile number +919876543210.'),
        env(),
      ),
    ).toEqual({
      kind: 'search',
      term: '+919876543210',
      openIfSingleMatch: false,
      message: 'Searching patients with mobile number +919876543210.',
    });
    expect(
      planAssistantAction(
        answer(
          'search_patient',
          { query: 'Ravi Sharma', openIfSingleMatch: 'true' },
          'Searching patients for “Ravi Sharma”.',
        ),
        env(),
      ),
    ).toMatchObject({
      kind: 'search',
      term: 'Ravi Sharma',
      openIfSingleMatch: true,
    });
    expect(
      planAssistantAction(answer('search_patient', {}, 'Which patient? Say the UHID, mobile number or name.'), env()),
    ).toEqual({
      kind: 'message',
      tone: 'info',
      message: 'Which patient? Say the UHID, mobile number or name.',
    });
    expect(
      planAssistantAction(answer('search_patient', { query: 'Ravi' }, 'Searching.'), env({ can: () => false })).kind,
    ).toBe('message');
  });

  it('lists appointments for the date, the doctor’s own when asked', () => {
    expect(
      planAssistantAction(
        answer('list_appointments', { date: '2026-10-02' }, 'Showing appointments for Fri, 2 Oct 2026.'),
        env(),
      ),
    ).toMatchObject({
      kind: 'navigate',
      href: '/scheduling?date=2026-10-02',
    });
    expect(
      planAssistantAction(
        answer('list_appointments', { date: '2026-09-28', scope: 'mine' }, 'Showing today’s appointments.'),
        env({ practitionerId: DOCTOR }),
      ),
    ).toMatchObject({ href: `/scheduling?date=2026-09-28&practitionerId=${DOCTOR}` });
    expect(
      planAssistantAction(answer('list_appointments', { date: 'soon' }, 'Showing today’s appointments.'), env()),
    ).toMatchObject({ href: '/scheduling?date=2026-09-28' });
  });

  it('falls back to the doctor worklist, or explains there is no access', () => {
    const noScheduling = env({ canOpen: (path) => path !== '/scheduling' });
    expect(
      planAssistantAction(answer('list_appointments', { date: '2026-10-02' }, 'Showing appointments.'), noScheduling),
    ).toMatchObject({ href: '/emr?date=2026-10-02' });
    expect(
      planAssistantAction(
        answer('list_appointments', { date: '2026-10-02' }, 'Showing appointments.'),
        env({ canOpen: () => false }),
      ),
    ).toMatchObject({
      kind: 'message',
      tone: 'warning',
    });
  });

  it('confirms a follow-up, then opens booking with the patient and date filled in', () => {
    const plan = planAssistantAction(
      answer(
        'book_follow_up',
        { days: '14', date: '2026-10-12', interval: '2 weeks', patientId: PATIENT },
        'Book a follow-up in 2 weeks, on Mon, 12 Oct 2026?',
        true,
      ),
      env(),
    );
    expect(plan).toMatchObject({
      kind: 'confirm',
      message: 'Book a follow-up in 2 weeks, on Mon, 12 Oct 2026?',
      href: `/scheduling/book?patientId=${PATIENT}&date=2026-10-12`,
      note: null,
    });
  });

  it('does not offer to book without a patient or a date', () => {
    expect(
      planAssistantAction(
        answer(
          'book_follow_up',
          { days: '14', date: '2026-10-12', interval: '2 weeks' },
          'Book a follow-up in 2 weeks? Open the patient first.',
          true,
        ),
        env(),
      ),
    ).toEqual({ kind: 'message', tone: 'info', message: 'Book a follow-up in 2 weeks? Open the patient first.' });
    expect(
      planAssistantAction(
        answer('book_follow_up', { patientId: PATIENT }, 'When should the follow-up be?', true),
        env(),
      ).kind,
    ).toBe('message');
    expect(
      planAssistantAction(
        answer('book_follow_up', { date: '2026-10-12', patientId: PATIENT }, 'Book?', true),
        env({ canOpen: (path) => path !== '/scheduling/book' }),
      ),
    ).toMatchObject({ kind: 'message', tone: 'warning' });
  });

  it('confirms a note, then opens the chart’s notes with the text kept for review', () => {
    const plan = planAssistantAction(
      answer(
        'create_note',
        { noteType: 'procedure', text: 'Suturing done under LA.', patientId: PATIENT },
        'Create a procedure note?',
        true,
      ),
      env(),
    );
    expect(plan).toEqual({
      kind: 'confirm',
      message: 'Create a procedure note?',
      href: `/emr/patients/${PATIENT}?tab=notes`,
      detail: expect.stringContaining('Restore it'),
      note: {
        patientId: PATIENT,
        draft: { type: 'Procedure', title: 'Procedure note', body: 'Suturing done under LA.' },
      },
    });
  });

  it('opens the notes without a draft when nothing was dictated', () => {
    const plan = planAssistantAction(
      answer('create_note', { noteType: 'progress', patientId: PATIENT }, 'Create a progress note?', true),
      env(),
    );
    expect(plan).toMatchObject({ kind: 'confirm', note: null, href: `/emr/patients/${PATIENT}?tab=notes` });
  });

  it('refuses a note without a patient or without permission', () => {
    expect(
      planAssistantAction(
        answer('create_note', { text: 'x' }, 'Create a clinical note? Open the patient first.', true),
        env(),
      ),
    ).toEqual({
      kind: 'message',
      tone: 'info',
      message: 'Create a clinical note? Open the patient first.',
    });
    expect(
      planAssistantAction(
        answer('create_note', { patientId: PATIENT }, 'Create a clinical note?', true),
        env({ can: () => false }),
      ),
    ).toMatchObject({
      kind: 'message',
      tone: 'warning',
    });
  });

  it('starts dictation for the patient on screen', () => {
    expect(
      planAssistantAction(answer('start_dictation', {}, 'Starting dictation.'), env({ contextPatientId: PATIENT })),
    ).toEqual({
      kind: 'dictate',
      patientId: PATIENT,
      message: 'Starting dictation.',
    });
  });

  it('shows the server’s words for unknown commands, and a fallback when it sent none', () => {
    expect(planAssistantAction(answer('unknown', {}, 'Sorry, I did not understand.'), env())).toEqual({
      kind: 'message',
      tone: 'info',
      message: 'Sorry, I did not understand.',
    });
    expect(planAssistantAction(answer('order_pizza', {}, ''), env())).toMatchObject({
      kind: 'message',
      message: expect.stringContaining('did not understand'),
    });
  });

  it('asks first whenever the server says confirmation is needed', () => {
    expect(
      planAssistantAction(answer('navigate', { route: '/billing', label: 'Billing' }, 'Open Billing?', true), env()),
    ).toEqual({
      kind: 'confirm',
      href: '/billing',
      message: 'Open Billing?',
      detail: '',
      note: null,
    });
  });
});

describe('notes', () => {
  it('maps spoken note types to EMR note types', () => {
    expect(noteDraftFor('SOAP', ' BP stable ')).toEqual({
      type: 'ProgressNote',
      title: 'Progress note',
      body: 'BP stable',
    });
    expect(noteDraftFor('referral', null)).toEqual({ type: 'Referral', title: 'Referral', body: '' });
    expect(noteDraftFor('nursing', 'x').type).toBe('Other');
    expect(noteDraftFor(undefined, 'x')).toEqual({ type: 'ProgressNote', title: '', body: 'x' });
    expect(noteDraftFor(null, 'a'.repeat(25_000)).body).toHaveLength(20_000);
  });

  it('turns dictated text into a confirmed note for the patient', () => {
    expect(planDictatedNote('  Fever for three days.  ', PATIENT, env())).toMatchObject({
      kind: 'confirm',
      note: { patientId: PATIENT, draft: { type: 'ProgressNote', body: 'Fever for three days.' } },
    });
    expect(planDictatedNote('   ', PATIENT, env()).kind).toBe('message');
    expect(planDictatedNote('Fever.', null, env()).kind).toBe('message');
  });
});
