// Wave 2 (front office and OP billing go live): the cashier's day at the OP counter. Opening the cash shift, collecting
// a patient's payment in cash, and the day-end report and closing the shift. Recorded as the cashier (Revathi Selvam)
// at 11:10 am, at OP counter 1. Lakshmi Narayanan's bill of two laboratory tests (₹2,400.00) is unpaid
// (src/web/demo/story.mjs); no other setup.

const nav = { role: 'navigation', name: 'Main navigation' };

const story = {
  id: 'w2-cashier',
  title: 'Cashier: open the shift, collect payments, close the day',
  role: 'Cashier',
  roleLabel: 'Cashier',
  user: 'cashier',
  wave: 2,
  audience: 'For the cashiers at the OP counters of Nana Hospitals.',
  intro:
    'The cashier desk is where the day’s money comes in. HC360 ties every receipt to your shift, so that at the end of the day the cash in your drawer matches the cash it expects.',
  objectives: [
    'Open your cash shift.',
    'Collect a patient’s payment and give the change.',
    'Read the day-end report and close your shift.',
  ],
  modules: ['billing'],
  covers: ['/billing/shifts', '/billing/op', '/billing/day-end'],
  desk: true,
  scenes: [
    {
      title: 'Open your cash shift',
      steps: [
        {
          click: { within: nav, role: 'button', name: 'Billing' },
          say: 'Open Billing from the menu.',
        },
        {
          nav: 'Cash shifts',
          say: 'Open Cash shifts.',
          after: 'You have no open shift. HC360 asks you to open one before you collect or pay out money.',
          wait: { role: 'button', name: 'Open shift' },
        },
        {
          click: { role: 'button', name: 'Open shift' },
          say: 'Click Open shift.',
          wait: { text: 'Open cash shift' },
        },
        {
          fill: [{ label: 'Opening cash in the drawer' }, '2000'],
          say: 'The counter is OP counter 1. Type the change float that you start with in the drawer.',
          zoom: 1.4,
        },
        {
          click: { within: { role: 'dialog', name: 'Open cash shift' }, role: 'button', name: 'Open shift' },
          say: 'Click Open shift.',
          after: 'Your shift is open at OP counter 1, with ₹2,000.00 expected in the drawer.',
          wait: { role: 'button', name: 'Close shift' },
        },
      ],
    },
    {
      title: 'Collect a payment',
      steps: [
        {
          nav: 'OP billing',
          say: 'Open OP billing.',
          wait: { role: 'heading', name: 'OP billing' },
        },
        {
          fill: [{ label: 'Patient' }, 'Lakshmi'],
          say: 'Lakshmi Narayanan is at the counter. Type her name.',
          wait: { role: 'option' },
          zoom: 1.4,
        },
        {
          click: { role: 'option', name: /Lakshmi Narayanan/ },
          say: 'Choose her from the list. Check her UHID and age against her card, because two patients can have the same name.',
          after: 'HC360 warns that she has an unpaid bill of ₹2,400.00.',
          wait: { role: 'button', name: /^Collect ₹2,400/ },
        },
        {
          click: { role: 'button', name: /^Collect ₹2,400/ },
          say: 'Click Collect to open the bill.',
          after: 'The bill lists two laboratory tests, vitamin D and vitamin B12. The balance due is ₹2,400.00.',
          wait: { text: /Balance due/i },
        },
        {
          click: { role: 'button', name: /^Collect ₹2,400/ },
          say: 'Click Collect again.',
          after: 'Cash is chosen, and the amount due is ₹2,400.00.',
          wait: { text: 'Collect payment' },
        },
        {
          fill: [{ label: 'Cash handed over by the patient' }, '2500'],
          say: 'She hands over ₹2,500. Type that amount. HC360 shows the change to give back.',
          after: 'The change to return is ₹100.00.',
          wait: { text: /Change to return/ },
          zoom: 1.4,
        },
        {
          click: { within: { role: 'dialog', name: 'Collect payment' }, role: 'button', name: /^Collect ₹/ },
          say: 'Count the change, then click Collect.',
          after: 'The payment is recorded: ₹2,400.00 received, with a receipt number. The bill is fully paid.',
          wait: { text: 'Payment recorded' },
        },
        {
          click: { role: 'button', name: 'Done' },
          say: 'Click Done.',
          after: 'Your drawer now expects ₹4,400.00, and your shift has one receipt.',
          wait: { text: /1 receipt/ },
        },
      ],
    },
    {
      title: 'Day-end report and closing the shift',
      steps: [
        {
          nav: 'Day-end report',
          say: 'Open the Day-end report.',
          after: 'It shows what the counters collected today, by payment mode and by cashier.',
          wait: { role: 'heading', name: 'Day-end report' },
        },
        {
          show: { role: 'row', name: /Revathi Selvam/ },
          say: 'Your line shows ₹2,400.00 collected and ₹4,400.00 expected cash in your drawer. You have not declared your cash yet.',
          zoom: 1.3,
        },
        {
          nav: 'Cash shifts',
          say: 'Open Cash shifts to close your shift.',
          wait: { role: 'button', name: 'Close shift' },
        },
        {
          click: { role: 'button', name: 'Close shift' },
          say: 'Click Close shift.',
          wait: { text: 'Close shift at OP counter 1' },
        },
        {
          fill: [{ label: /^Cash counted in the drawer/ }, '4400'],
          say: 'Count the cash in the drawer and type it. Closing is final: later collections need a new shift.',
          zoom: 1.4,
        },
        {
          click: { role: 'button', name: 'Close shift', last: true },
          say: 'Click Close shift.',
          after: 'Your shift is Closed. The counted cash equals the expected cash, and HC360 marks it Tallied.',
          wait: { role: 'row', name: /Revathi Selvam.*Tallied.*Closed/ },
        },
      ],
    },
  ],
  recap: [
    'Open your shift before you collect any money. Close it at the end and declare the cash you counted.',
    'Check the patient’s name and UHID, enter the cash handed over, and give the change that HC360 shows.',
    'The day-end report shows what each counter collected and the cash each drawer should hold.',
  ],
  check: [
    {
      q: 'Why does HC360 ask you to open a shift before you collect money?',
      a: 'Every receipt belongs to a shift, so that the cash expected in your drawer can be checked against the cash you count.',
    },
    {
      q: 'The patient hands over ₹2,500 for a bill of ₹2,400. What do you type, and what does HC360 show?',
      a: 'Type ₹2,500 as the cash handed over. HC360 shows ₹100.00 as the change to return.',
    },
  ],
  next: 'Accountant: approve credit notes and discounts, post the Tally day book',
};

export default story;
