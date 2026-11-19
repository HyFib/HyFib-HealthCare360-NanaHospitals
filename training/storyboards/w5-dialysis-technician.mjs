import { writeFileSync } from 'node:fs';
const nav = { role: 'navigation', name: 'Main navigation' };
const dump = (name) => async ({ page }) => writeFileSync(`/tmp/claude-0/-home-user-HyFib-HealthCare360-NanaHospitals/4cf56889-54f3-5e4e-8287-5c222ea81477/scratchpad/${name}.txt`, await page.locator('body').ariaSnapshot());
export default {
  id: 'w5-dialysis-technician', title: 'Dialysis technician', role: 'DialysisTechnician', roleLabel: 'Dialysis technician', user: 'dialysis', wave: 5,
  audience: 'x', intro: 'x', objectives: ['a', 'b'], modules: ['dialysis'], covers: ['/dialysis'],
  setup: async ({ api }) => {
    const tech = await api('dialysis');
    const fo = await api('frontoffice');
    const found = await fo.get('/api/v1/patients?search=Mohammed%20Irfan&pageSize=5');
    const patient = (found.items ?? found).find((p) => p.fullName.includes('Irfan'));
    await tech.put(`/api/v1/dialysis/patients/${patient.id}`, { dryWeightKg: 62.5, vascularAccess: 'AVF', accessSite: 'Left forearm', accessCreatedOn: '2026-06-10', scheduleDays: ['Monday', 'Wednesday', 'Friday', 'Thursday'], shift: 'Morning', sessionMinutes: 240, seroStatus: 'Negative', seroTestedOn: '2026-11-02', reusePolicy: 'SingleUse', maxDialyzerUses: 1, nephrologistId: null, primaryDiagnosis: 'Chronic kidney disease, stage 5', anticoagulationNotes: null, isActive: true, version: null });
  },
  scenes: [{ title: 'x', steps: [
    { click: { within: nav, role: 'button', name: 'Dialysis' }, say: 'Open.' },
    { nav: 'Dialysis board', say: 'Open.', wait: { role: 'heading', name: /Due today, not booked \(1\)/ } },
    { show: { role: 'heading', name: 'Dialysis board' }, say: 'x', after_do: dump('y-a') },
    { click: { role: 'button', name: 'Book session' }, say: 'Book.', wait: { role: 'dialog', name: 'Book a dialysis session' } },
    { fill: [{ label: 'Patient' }, 'Irfan'], say: 'P.', wait: { role: 'option', name: /Mohammed Irfan/ } },
    { click: { role: 'option', name: /Mohammed Irfan/ }, say: 'Pick.' },
    { click: { role: 'button', name: 'Book session', last: true }, say: 'Book.', wait: { role: 'heading', name: /Waiting for a machine \(1\)/ } },
    { show: { role: 'heading', name: 'Dialysis board' }, say: 'x', after_do: dump('y-b') },
  ] }],
  recap: ['a','b','c'], check: [], next: 'x',
};
