import { writeFileSync } from 'node:fs';
const nav = { role: 'navigation', name: 'Main navigation' };
const dump = (name) => async ({ page }) => writeFileSync(`/tmp/claude-0/-home-user-HyFib-HealthCare360-NanaHospitals/4cf56889-54f3-5e4e-8287-5c222ea81477/scratchpad/${name}.txt`, await page.locator('body').ariaSnapshot());
export default {
  id: 'w5-physiotherapist', title: 'Physiotherapist', role: 'Physiotherapist', roleLabel: 'Physiotherapist', user: 'physio', wave: 5,
  audience: 'x', intro: 'x', objectives: ['a', 'b'], modules: ['physio'], covers: ['/physio'],
  setup: async ({ api }) => {
    const doctor = await api('doctor');
    const fo = await api('frontoffice');
    const found = await fo.get('/api/v1/patients?search=Lakshmi%20Narayanan&pageSize=5');
    const patient = (found.items ?? found).find((p) => p.fullName.includes('Lakshmi'));
    const me = doctor.me.practitionerId ?? (await doctor.get('/api/v1/auth/me')).practitionerId;
    await doctor.post('/api/v1/physio/referrals', { patientId: patient.id, visitId: null, admissionId: null, referredByPractitionerId: me, priority: 'Routine', reason: 'Osteoarthritis of both knees: pain on climbing stairs. Strengthen the thigh muscles and improve walking.', sessionsOrdered: 6 });
  },
  scenes: [{ title: 'x', steps: [
    { click: { within: nav, role: 'button', name: 'Physiotherapy' }, say: 'Open.' },
    { nav: 'Physiotherapy worklist', say: 'Open.', wait: { role: 'table', name: 'Physiotherapy sessions' } },
    { nav: 'Physiotherapy referrals', say: 'Open.', wait: { role: 'row', name: /Lakshmi Narayanan/ } },
    { click: { role: 'row', name: /Lakshmi Narayanan/ }, say: 'Open.', wait: { role: 'button', name: 'Record assessment' }, after_do: dump('p-b') },
    { click: { role: 'button', name: 'Record assessment' }, say: 'Open.', wait: { role: 'dialog', name: /Record assessment/ } },
    { fill: [{ label: 'Pain now (0–10)' }, '6'], say: 'Pain.' },
    { fill: [{ label: 'Pain location' }, 'Both knees'], say: 'Loc.' },
    { fill: [{ label: 'Chief complaint' }, 'Pain in both knees on climbing stairs for six months.'], say: 'Complaint.' },
    { fill: [{ label: 'Functional status' }, 'Walks 200 metres. Stairs are painful.'], say: 'Func.' },
    { fill: [{ label: 'Goals' }, 'Climb stairs with less pain.'], say: 'Goals.' },
    { click: { role: 'button', name: 'Save assessment' }, say: 'Save.', wait: { role: 'button', name: 'Write treatment plan' }, after_do: dump('p-c') },
    { click: { role: 'button', name: 'Write treatment plan' }, say: 'Plan.', wait: { role: 'dialog', name: 'Treatment plan' }, after_do: dump('p-d') },
    { click: { role: 'checkbox', name: 'Exercises' }, say: 'Ex.' },
    { click: { role: 'checkbox', name: 'Hot pack' }, say: 'Hot.' },
    { fill: [{ label: 'Exercise programme' }, 'Quadriceps strengthening, twice a day.'], say: 'Prog.' },
    { click: { role: 'button', name: 'Save plan' }, say: 'Save plan.', wait: { role: 'button', name: 'Book sessions' }, after_do: dump('p-e') },
    { click: { role: 'button', name: 'Book sessions' }, say: 'Book.', wait: { role: 'dialog', name: 'Book sessions' } },
    { click: { label: 'First session' }, say: 'First.', after_do: async ({ page }) => { await page.getByLabel('First session').fill('2026-11-19T14:00'); }, },
    { fill: [{ label: 'Number of sessions' }, '6'], say: 'N.' },
    { click: { role: 'button', name: 'Book sessions', last: true }, say: 'Book.', wait: { role: 'button', name: /Record session|Record this session/ }, after_do: dump('p-f') },
  ] }],
  recap: ['a','b','c'], check: [], next: 'x',
};
