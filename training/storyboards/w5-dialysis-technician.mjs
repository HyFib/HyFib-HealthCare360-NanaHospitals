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
    { click: { role: 'button', name: 'Book session' }, say: 'Book.', wait: { role: 'dialog', name: 'Book a dialysis session' } },
    { fill: [{ label: 'Patient' }, 'Irfan'], say: 'P.', wait: { role: 'option', name: /Mohammed Irfan/ } },
    { click: { role: 'option', name: /Mohammed Irfan/ }, say: 'Pick.' },
    { click: { role: 'button', name: 'Book session', last: true }, say: 'Book.', wait: { role: 'button', name: 'Assign machine' } },
    { click: { role: 'button', name: 'Assign machine' }, say: 'Assign.', wait: { role: 'dialog', name: /Assign a machine/ }, after_do: dump('y-c') },
    { click: { role: 'radio', name: /^HD-01/ }, say: 'M.' },
    { click: { within: { role: 'dialog', name: /Assign a machine/ }, role: 'button', name: 'Assign machine' }, say: 'A.', wait: { text: /HD-01/, exact: false } },
    { fill: [{ label: 'Weight before (kg)' }, '64.2'], say: 'W.' },
    { fill: [{ label: 'Systolic BP' }, '138'], say: 'S.' },
    { fill: [{ label: 'Diastolic BP' }, '84'], say: 'D.' },
    { fill: [{ label: 'Pulse (/min)' }, '76'], say: 'P.' },
    { fill: [{ label: 'Temperature (°C)' }, '36.8'], say: 'T.' },
    { click: { role: 'button', name: 'Use 1,700 ml' }, say: 'UF.' },
    { click: { role: 'button', name: 'Save pre-dialysis assessment' }, say: 'Save.', wait: { role: 'button', name: 'Connect and start dialysis' }, after_do: dump('y-d') },
    { click: { role: 'radio', name: /^HD-01/ }, say: 'M2.' },
    { click: { role: 'radio', name: 'Register a new dialyzer (first use)' }, say: 'R.' },
    { fill: [{ label: 'Serial number' }, 'DLZ-482913'], say: 'Serial.' },
    { click: { role: 'button', name: 'Connect and start dialysis' }, say: 'Start.', wait: { role: 'heading', name: 'During dialysis' } },
    { fill: [{ label: 'Systolic', exact: true }, '132'], say: 'r1.' },
    { fill: [{ label: 'Diastolic', exact: true }, '80'], say: 'r2.' },
    { fill: [{ label: 'Pulse', exact: true }, '74'], say: 'r3.' },
    { fill: [{ label: 'Blood flow (ml/min)' }, '300'], say: 'r4.' },
    { click: { role: 'button', name: 'Add reading' }, say: 'add.', wait: { role: 'table', name: 'Intra-dialytic readings' }, after_do: dump('y-f') },
    { fill: [{ label: 'Weight after (kg)' }, '62.6'], say: 'w.' },
    { fill: [{ label: 'Systolic BP', exact: true, last: true }, '128'], say: 's.' },
    { fill: [{ label: 'Diastolic BP', exact: true, last: true }, '78'], say: 'd.' },
    { click: { role: 'button', name: 'Complete dialysis' }, say: 'c.', wait: { role: 'button', name: 'Complete session' } },
    { click: { role: 'button', name: 'Complete session' }, say: 'cc.', wait: { role: 'heading', name: 'Summary' }, after_do: dump('y-g') },
    { nav: 'Dialysis board', say: 'b.', wait: { role: 'heading', name: 'Dialysis board' } },
    { show: { role: 'main' }, say: 'x', after_do: dump('y-h') },
  ] }],
  recap: ['a','b','c'], check: [], next: 'x',
};
