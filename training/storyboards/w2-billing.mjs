// Wave 2 (front office and OP billing): the billing executive's morning at OP counter 1. Collecting an out-patient bill
// that is due, with a UPI payment and its UTR; opening an advance on an in-patient's running account; reading the
// day-end report. Recorded as the billing executive (Prakash Iyer) at 11:10 am, with the cash shift already open.

const nav = { role: 'navigation', name: 'Main navigation' };
const pay = { role: 'dialog', name: 'Collect payment' };
const advance = { role: 'dialog', name: 'Collect IP advance' };

export default {
  id: 'w2-billing',
  title: 'Billing: collect a bill, take an advance, read the day-end report',
  role: 'Billing',
  roleLabel: 'Billing executive',
  user: 'billing',
  desk: true,
  wave: 2,
  audience: 'For billing executives at the OP and IP billing counters of Nana Hospitals.',
  intro:
    'Every rupee the hospital earns passes through a billing counter. In HC360, each payment is recorded in your cash shift, against one bill, with a receipt. This video shows the three jobs of a billing morning.',
  objectives: [
    'Collect payment for an out-patient bill, including a UPI payment.',
    'Collect an advance on an in-patient’s running account.',
    'Read the day-end report to see what the counter has collected.',
  ],
  modules: ['billing'],
  covers: ['/billing/op', '/billing/ip', '/billing/day-end'],
  scenes: [
    {
      title: 'Collect an out-patient bill',
      steps: [
        {
          click: { within: nav, role: 'button', name: 'Billing' },
          say: 'Open Billing from the menu.',
        },
        {
          nav: 'OP billing',
          say: 'Then choose OP billing.',
          after: 'Your shift is open at OP counter 1. Every payment you take is recorded in this shift.',
          wait: { text: 'Shift open' },
          result: { role: 'status', name: /Shift open/ },
        },
        {
          fill: [{ label: 'Patient' }, 'Lakshmi'],
          say: 'Lakshmi Narayanan is at the counter. Type her name in the Patient box.',
          wait: { role: 'option', name: /Lakshmi Narayanan/ },
        },
        {
          click: { role: 'option', name: /Lakshmi Narayanan/ },
          say: 'Choose her from the list.',
          wait: { role: 'heading', name: 'Unpaid bills' },
        },
        {
          show: { role: 'region', name: /^Patient Lakshmi Narayanan/ },
          say: 'Check the name and the UHID against her card, so that you bill the right patient. HC360 warns that she has ₹2,400 unpaid on one bill.',
          zoom: 1.4,
        },
      ],
    },
  ],
  recap: ['a', 'b', 'c'],
  check: [{ q: 'q', a: 'a' }, { q: 'q', a: 'a' }],
  next: 'x',
};
