const nav = { role: 'navigation', name: 'Main navigation' };

const story = {
  id: 'w2-cashier',
  title: 'Cashier: draft',
  role: 'Cashier',
  roleLabel: 'Cashier',
  user: 'cashier',
  wave: 2,
  audience: 'x',
  intro: 'x',
  objectives: ['a', 'b'],
  modules: ['billing'],
  covers: ['/billing/shifts'],
  desk: true,
  scenes: [
    {
      title: 'Open the shift',
      steps: [
        { click: { within: nav, role: 'button', name: 'Billing' }, say: 'Open Billing.' },
        { nav: 'Cash shifts', say: 'Open Cash shifts.', wait: { role: 'heading', name: 'Cash shifts' } },
        { click: { role: 'button', name: 'Open shift' }, say: 'Open shift.', wait: { role: 'dialog' } },
        { fill: [{ label: 'Opening cash in the drawer' }, '2000'], say: 'float' },
        { click: { within: { role: 'dialog', name: 'Open cash shift' }, role: 'button', name: 'Open shift' }, say: 'open', wait: { role: 'button', name: /Close shift/ } },
      ],
    },
    {
      title: 'Collect',
      steps: [
        { nav: 'OP billing', say: 'Open OP billing.', wait: { role: 'heading', name: 'OP billing' } },
        { fill: [{ label: 'Patient' }, 'Lakshmi'], say: 'patient', wait: { role: 'option' } },
        { click: { role: 'option', name: /Lakshmi Narayanan/ }, say: 'pick', wait: { role: 'button', name: /^Collect ₹2,400/ } },
        { click: { role: 'button', name: /^Collect ₹2,400/ }, say: 'collect', wait: { role: 'dialog' } },
      ],
    },
  ],
  recap: ['a', 'b', 'c'],
  check: [{ q: 'q', a: 'a' }, { q: 'q', a: 'a' }],
  next: 'x',
};
// DEVTAIL-BEGIN
import { appendFileSync as __a, writeFileSync as __w } from 'node:fs';
const __story = story;
if (process.env.HC360_DUMP) {
  __w(process.env.HC360_DUMP, '');
  let n = 0;
  __story.scenes = __story.scenes.map((sc) => ({
    ...sc,
    steps: sc.steps.map((st) => ({
      ...st,
      after_do: async (t) => {
        if (st.after_do) await st.after_do(t);
        await t.quiet(3000);
        const dlg = t.page.locator('[role="dialog"]:visible');
        const root = (await dlg.count()) ? dlg.first() : t.page.locator('#main, main').first();
        __a(process.env.HC360_DUMP, `\n##### step ${++n} ${JSON.stringify(st.say ?? '').slice(0, 80)} ${t.page.url()}\n${await root.ariaSnapshot().catch((e) => String(e))}\n`);
      },
    })),
  }));
}
export default __story;
// DEVTAIL-END
