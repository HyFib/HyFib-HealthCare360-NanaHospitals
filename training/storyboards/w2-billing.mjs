// Wave 2 (front office and OP billing): the billing executive's morning at OP counter 1. Collecting an out-patient bill
// that is due, with a UPI payment and its UTR; taking an advance on an in-patient's running account; reading the
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
        {
          click: { role: 'button', name: 'Collect ₹2,400.00' },
          say: 'Click Collect on her unpaid bill, NHOB26000001.',
          wait: { role: 'table', name: 'Services on this bill' },
        },
        {
          show: { role: 'table', name: 'Services on this bill' },
          say: 'The bill has two services, the vitamin D test and the vitamin B12 test. Check them with the patient. The total is ₹2,400.',
          zoom: 1.3,
        },
        {
          click: { within: { role: 'complementary', name: 'Bill totals and actions' }, role: 'button', name: 'Collect ₹2,400.00' },
          say: 'Click Collect again.',
          wait: pay,
        },
        {
          click: { within: { role: 'group', name: 'Add a payment mode' }, role: 'button', name: 'UPI' },
          say: 'Lakshmi Narayanan pays by UPI. Choose UPI. The amount is the whole balance, ₹2,400.',
          wait: { within: pay, label: 'UPI reference (UTR)' },
          zoom: 1.4,
        },
        {
          fill: [{ within: pay, label: 'UPI reference (UTR)' }, '432612345679'],
          say: 'Type the 12-digit UTR from the payment screen on her phone. It lets the accounts team match this payment to the bank statement.',
          zoom: 1.4,
        },
        {
          click: { within: pay, role: 'button', name: 'Collect ₹2,400.00' },
          say: 'Click Collect.',
          after:
            'HC360 records the payment: ₹2,400 received, with its receipt number, and the bill is fully paid. Print the receipt from here, and give it to the patient.',
          wait: { role: 'heading', name: 'Payment recorded' },
          result: { text: '₹2,400.00 received' },
        },
        {
          click: { role: 'button', name: 'Done' },
          say: 'Then click Done.',
          wait: { role: 'table', name: 'Services on this bill' },
        },
      ],
    },
    {
      title: 'Take an advance for an in-patient',
      steps: [
        {
          nav: 'In-patient accounts',
          say: 'Fatima Begum is in the female general ward. Her family brings an advance. Open In-patient accounts.',
          wait: { role: 'cell', name: /^Fatima Begum/ },
        },
        {
          click: { role: 'cell', name: /^Fatima Begum/ },
          say: 'Open her account.',
          wait: { role: 'heading', name: /^Fatima Begum · NHIP26000001/ },
        },
        {
          show: { role: 'table', name: 'Running bill by category' },
          say: 'The running bill adds every charge as it is posted: laboratory, imaging, the bed, nursing and the doctor’s visit. So far it is ₹4,050, and no advance is held.',
          zoom: 1.2,
        },
        {
          click: { role: 'button', name: 'Collect advance' },
          say: 'Click Collect advance.',
          wait: advance,
        },
        {
          fill: [{ within: advance, label: 'Amount' }, '5000'],
          say: 'A family member pays ₹5,000 in cash. Type the amount. Cash is already chosen.',
          zoom: 1.4,
        },
        {
          fill: [{ within: advance, label: 'Note on the receipt' }, 'Advance for the ward stay'],
          say: 'You may write a note for the receipt.',
          zoom: 1.4,
        },
        {
          click: { within: advance, role: 'button', name: 'Collect ₹5,000.00' },
          say: 'Click Collect.',
          after:
            'The advance is received, with its receipt number. It is held on Fatima Begum’s account, and is set against her final bill when she goes home.',
          wait: { role: 'heading', name: 'Advance received' },
          result: { text: '₹5,000.00 advance received' },
        },
        {
          click: { role: 'button', name: 'Done' },
          say: 'Click Done.',
          wait: { role: 'table', name: 'Running bill by category' },
        },
      ],
    },
    {
      title: 'Read the day-end report',
      steps: [
        {
          nav: 'Day-end report',
          say: 'Now open the Day-end report.',
          after: 'It shows what the counters collected today.',
          wait: { role: 'heading', name: 'Day-end report' },
        },
        {
          show: { text: 'IP advances collected' },
          say: 'The top of the report shows the net collection, and the IP advances collected. Fatima Begum’s ₹5,000 is counted here.',
          zoom: 1.3,
        },
        {
          show: { role: 'table', name: 'Collections by payment mode' },
          say: 'Collections are listed by payment mode: cash, card and UPI. The UPI line now shows ₹3,950, with Lakshmi Narayanan’s ₹2,400.',
          zoom: 1.3,
        },
        {
          show: { role: 'table', name: 'Collections by cashier' },
          say: 'And by cashier. Your shift is still open, so the figures can still change, and the expected cash is what the drawer should hold. At the end of your shift, close it with Close shift.',
          zoom: 1.3,
        },
      ],
    },
  ],
  recap: [
    'Check the name and the UHID before you bill. Every payment goes into your shift, with a receipt.',
    'For UPI, type the 12-digit UTR. An advance for an in-patient is held on her account until the final bill.',
    'The day-end report shows the collections by payment mode and by cashier.',
  ],
  check: [
    {
      q: 'A patient pays by UPI. What must you type before you collect, and why?',
      a: 'The 12-digit UTR from the payment screen. It lets the accounts team match the payment to the bank statement.',
    },
    {
      q: 'Fatima Begum’s family brings ₹5,000 for her stay. Where do you record it?',
      a: 'In In-patient accounts: open her account and click Collect advance. The advance is held on her account, and set against her final bill.',
    },
  ],
};
