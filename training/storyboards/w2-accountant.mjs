// Wave 2 (front office and OP billing go live): the accountant's day. Deciding what the cashiers asked for, which
// they may not decide themselves: a credit note and a discount above the cashier's limit; then the day book that goes
// to Tally. Recorded as the accountant (Venkatesh Balan) at 11:10 am. Setup, as the cashier (Prakash Iyer, the
// 'billing' account) would have done it earlier: a credit note of ₹200.00 on Meenakshi Sundaram's paid bill, and a 10%
// discount on Ravi Kumar's draft bill.

const nav = { role: 'navigation', name: 'Main navigation' };

const story = {
  id: 'w2-accountant',
  title: 'Accountant: approve credit notes and discounts, post the Tally day book',
  role: 'Accountant',
  roleLabel: 'Accountant',
  user: 'accountant',
  wave: 2,
  audience: 'For the accountants and the accounts staff of Nana Hospitals.',
  intro:
    'Counter staff cannot give money back, or lower a bill beyond their own limit. They ask, and you decide. HC360 then keeps the books in step: every bill, receipt and credit note goes to the day book that is posted to Tally.',
  objectives: [
    'Approve a credit note asked for at the billing counter.',
    'Approve a discount above the limit of the person who asked for it.',
    'Preview the day book and post it to Tally.',
  ],
  modules: ['approvals', 'billing'],
  covers: ['/approvals', '/billing/discount-approvals', '/billing/tally'],
  setup: async ({ api }) => {
    const billing = await api('billing');
    const fo = await api('frontoffice');
    const visits = await fo.get('/api/v1/visits?search=Ravi&pageSize=10');
    const visit = visits.items.find((v) => v.patientName === 'Ravi Kumar');
    const draft = await billing.post('/api/v1/billing/bills', { visitId: visit.id, patientId: null, chargeIds: null });
    await billing.post(`/api/v1/billing/bills/${draft.id}/discount`, {
      billDiscountPercent: 10,
      billDiscountAmount: null,
      lineDiscounts: null,
      reason: 'Retired government servant, as the patient asked',
      version: draft.version,
    });
    const bills = await billing.get('/api/v1/billing/bills?pageSize=50');
    const paid = bills.items.find((b) => b.billNumber === 'NHOB26000002');
    await billing.post(`/api/v1/billing/bills/${paid.id}/credit-notes`, {
      kind: 'CreditNote',
      amount: 200,
      reason: 'Wrong rate charged for the consultation',
      lines: null,
    });
  },
  scenes: [
    {
      title: 'Approve a credit note',
      steps: [
        {
          nav: 'Approvals',
          say: 'Open Approvals.',
          after: 'A credit note of ₹200.00 waits for your decision. Prakash Iyer asked for it, and nobody approves their own request.',
          wait: { role: 'row', name: /Credit note of ₹200.00/ },
        },
        {
          show: { role: 'row', name: /Credit note of ₹200.00/ },
          say: 'Prakash Iyer asks for a credit note on bill NHOB26000002, because the wrong rate was charged for the consultation. Read the reason before you decide.',
          zoom: 1.3,
        },
        {
          click: { role: 'button', name: 'Approve' },
          say: 'Click Approve.',
          after: 'HC360 shows the request and asks for an optional note.',
          wait: { text: /^Approve: credit note/ },
        },
        {
          click: { role: 'button', name: 'Approve', last: true },
          say: 'Click Approve again to confirm. The credit note is made now, exactly as it was asked for.',
          after: 'Nothing is waiting for your decision now.',
          wait: { text: /Nothing is waiting for your decision/ },
        },
      ],
    },
    {
      title: 'Approve a discount',
      steps: [
        {
          click: { within: nav, role: 'button', name: 'Billing' },
          say: 'Open Billing from the menu.',
        },
        {
          nav: 'Discount approvals',
          say: 'Open Discount approvals.',
          after: 'A discount on Ravi Kumar’s draft bill waits for a decision.',
          wait: { role: 'row', name: /Ravi Kumar/ },
        },
        {
          show: { role: 'row', name: /Ravi Kumar/ },
          say: 'Prakash Iyer asks for 10% of ₹1,050.00, which is ₹105.00. It is above Prakash Iyer’s own limit, so it waits for you. Read the reason.',
          zoom: 1.3,
        },
        {
          click: { role: 'button', name: 'Approve' },
          say: 'Click Approve.',
          after: 'HC360 asks why you approve it.',
          wait: { text: /^Approve a discount of/ },
        },
        {
          fill: [{ label: /^Why is it approved/ }, 'Approved for a retired government servant.'],
          say: 'Type why you approve it. Prakash Iyer sees your reason, and it is kept with the bill.',
          zoom: 1.4,
        },
        {
          click: { role: 'button', name: 'Approve discount' },
          say: 'Click Approve discount.',
          after: 'No discount is waiting for a decision now.',
          wait: { text: /No discount is waiting/ },
        },
      ],
    },
    {
      title: 'Preview the day book and post it to Tally',
      steps: [
        {
          nav: 'Tally export',
          say: 'Open Tally export.',
          after: 'The posting log lists the days already posted to Tally.',
          wait: { role: 'table', name: 'Tally posting log' },
        },
        {
          click: { role: 'tab', name: 'Day book preview' },
          say: 'Open Day book preview.',
          wait: { role: 'textbox', name: 'Day' },
        },
        {
          fill: [{ label: 'Day' }, '2026-11-19'],
          say: 'Choose today’s date.',
          after: 'The day book lists 12 vouchers, and the debit and the credit are the same, ₹33,780.25.',
          wait: { text: /12 vouchers/ },
          zoom: 1.3,
        },
        {
          show: { role: 'listitem', has: 'NHCN26000001' },
          say: 'The credit note that you approved is in the day book, with the ledgers it debits and credits.',
          zoom: 1.5,
        },
        {
          click: { role: 'tab', name: 'Posting log' },
          say: 'Open the Posting log.',
          wait: { role: 'button', name: 'Post to Tally' },
        },
        {
          fill: [{ label: 'Post the day book of' }, '2026-11-19'],
          say: 'Choose the day to post.',
          zoom: 1.4,
        },
        {
          click: { role: 'button', name: 'Post to Tally' },
          say: 'Click Post to Tally.',
          after: 'HC360 asks you to confirm. Vouchers already in Tally are updated, not duplicated.',
          wait: { text: /^Post the day book of 19 Nov 2026\?/ },
        },
        {
          click: { role: 'button', name: 'Post to Tally', last: true },
          say: 'Confirm.',
          after: 'Today is listed in the posting log as Waiting to post. It waits until its file is imported into Tally.',
          wait: { role: 'row', name: /^19 Nov 2026.*Waiting to post/ },
        },
      ],
    },
  ],
  recap: [
    'Read the reason of every credit note and discount before you approve it. You cannot approve what you asked for.',
    'An approved credit note and an approved discount are on the record, with your reason.',
    'The day book lists every voucher of the day. Check that the debit and the credit are the same, then post it to Tally.',
  ],
  check: [
    {
      q: 'Why does the discount wait for your approval?',
      a: 'It is above the limit of the person who asked for it. Only an approver who did not ask for it can approve it.',
    },
    {
      q: 'What do you check in the day book before you post it to Tally?',
      a: 'That the debit and the credit are the same, and that the vouchers are the day’s bills, receipts and credit notes.',
    },
  ],
};

export default story;
