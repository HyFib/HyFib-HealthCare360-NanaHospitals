const nav = { role: 'navigation', name: 'Main navigation' };

const story = {
  id: 'w2-accountant',
  title: 'Accountant: draft',
  role: 'Accountant',
  roleLabel: 'Accountant',
  user: 'accountant',
  wave: 2,
  audience: 'x',
  intro: 'x',
  objectives: ['a', 'b'],
  modules: ['billing'],
  covers: ['/billing/refunds'],
  setup: async ({ api }) => {
    const billing = await api('billing');
    const fo = await api('frontoffice');
    const visits = await fo.get('/api/v1/visits?search=Ravi&pageSize=10');
    const visit = visits.items.find((v) => v.patientName === 'Ravi Kumar');
    const draft = await billing.post('/api/v1/billing/bills', { visitId: visit.id, patientId: null, chargeIds: null });
    const outcome = await billing.post(`/api/v1/billing/bills/${draft.id}/discount`, {
      billDiscountPercent: 10,
      billDiscountAmount: null,
      lineDiscounts: null,
      reason: 'Retired government servant, as the patient asked',
      version: draft.version,
    });
    console.log('discount', JSON.stringify(outcome));
    const bills = await billing.get('/api/v1/billing/bills?pageSize=50');
    const paid = bills.items.find((b) => b.billNumber === 'NHOB26000002');
    const note = await billing.post(`/api/v1/billing/bills/${paid.id}/credit-notes`, {
      kind: 'CreditNote',
      amount: 200,
      reason: 'Wrong rate charged for the consultation',
      lines: null,
    });
    console.log('credit note', JSON.stringify(note));
  },
  scenes: [
    {
      title: 'Approvals',
      steps: [
        { nav: 'Approvals', say: 'Open Approvals.', wait: { role: 'heading', name: 'Approvals' } },
        { click: { role: 'button', name: 'Approve' }, say: 'approve credit', wait: { role: 'dialog' } },
        { click: { within: { role: 'dialog' }, role: 'button', name: /Approve/ }, say: 'approve credit 2' },
        { click: { within: nav, role: 'button', name: 'Billing' }, say: 'Open Billing.' },
        { nav: 'Refunds', say: 'Open Refunds.', wait: { role: 'heading', name: 'Refunds' } },
        { nav: 'Discount approvals', say: 'Open Discount approvals.', wait: { role: 'row', name: /Ravi Kumar/ } },
        { click: { role: 'button', name: 'Approve' }, say: 'approve discount' },
        { nav: 'Tally export', say: 'Open Tally export.', wait: { role: 'heading', name: 'Tally export' } },
        { click: { role: 'tab', name: 'Day book preview' }, say: 'Preview.' },
        { fill: [{ label: 'Day' }, '2026-11-19'], say: 'today' },
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
