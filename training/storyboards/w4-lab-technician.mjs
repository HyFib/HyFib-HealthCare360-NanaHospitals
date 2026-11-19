// Wave 4 (diagnostics): the laboratory technician's day. Collecting samples (and why an unpaid order is refused),
// receiving the tubes in the lab, and entering a result. Recorded as the lab technician (Suresh Babu) at 11:10 am,
// when three tubes wait for phlebotomy and two samples are in the lab (src/web/demo/story.mjs).

const nav = { role: 'navigation', name: 'Main navigation' };

export default {
  id: 'w4-lab-technician',
  title: 'Lab technician: collect samples, receive them, enter results',
  role: 'LabTechnician',
  roleLabel: 'Lab technician',
  user: 'lab',
  wave: 4,
  audience: 'For laboratory technicians and phlebotomists of the Nana Hospitals laboratory.',
  intro:
    'In the laboratory, HC360 follows every tube from the patient’s arm to the released report. You collect the samples, receive them in the lab and enter the results. The pathologist then verifies and releases them.',
  objectives: [
    'Collect the samples of an order, and know why an unpaid order is refused.',
    'Receive the tubes in the lab by scanning their labels.',
    'Enter a result on the result sheet.',
  ],
  modules: ['lab'],
  covers: ['/lab', '/lab/collection', '/lab/receive', '/lab/worklist'],
  // Ravi Kumar pays at the billing counter first (the morning's story leaves him unpaid), so that his tubes can be drawn.
  setup: async ({ api }) => {
    const frontOffice = await api('frontoffice');
    const visits = await frontOffice.get('/api/v1/visits?search=Ravi%20Kumar&pageSize=5');
    const visit = visits.items.find((v) => v.patientName === 'Ravi Kumar');
    const billing = await api('billing');
    const shift = await billing.response('GET', '/api/v1/billing/shifts/current');
    if (shift.status === 204) await billing.post('/api/v1/billing/shifts/open', { counter: 'OP counter 1', openingFloat: 2000 });
    const draft = await billing.post('/api/v1/billing/bills', { visitId: visit.id, patientId: null, chargeIds: null });
    const bill = await billing.post(`/api/v1/billing/bills/${draft.id}/finalize`, { version: draft.version });
    await billing.post('/api/v1/billing/receipts', {
      billId: bill.id,
      payments: [
        {
          mode: 'Cash',
          amount: bill.dueAmount,
          reference: null,
          cardLast4: null,
          cardNetwork: null,
          chequeNumber: null,
          chequeBank: null,
          chequeDate: null,
        },
      ],
      notes: null,
    });
  },
  scenes: [
    {
      title: 'Your laboratory this morning',
      steps: [
        {
          click: { within: nav, role: 'button', name: 'Laboratory' },
          say: 'Open Laboratory from the menu.',
        },
        {
          nav: 'Lab overview',
          say: 'The lab overview shows today’s work.',
          after: 'Three tubes wait to be collected. Two samples are in the lab, with results to verify.',
          wait: { role: 'button', name: /^To collect/ },
          result: { role: 'button', name: /^To collect/ },
        },
      ],
    },
    {
      title: 'Collect the samples',
      steps: [
        {
          nav: 'Sample collection',
          say: 'Open Sample collection.',
          after: 'Each patient with tubes to draw has a card. Ravi Kumar has two tubes. Lakshmi Narayanan has one.',
          wait: { role: 'region', name: /^Lakshmi Narayanan/ },
        },
        {
          click: { role: 'button', name: 'Collect 1 tube' },
          say: 'Lakshmi Narayanan comes first. Click Collect 1 tube.',
          wait: { role: 'dialog', name: /^Collect samples/ },
        },
        {
          show: { role: 'dialog', name: /^Collect samples/ },
          say: 'First check that this is the right patient: the name and the UHID. Two patients can have the same name.',
          zoom: 1.3,
        },
        {
          click: { role: 'button', name: 'Collect and print labels' },
          say: 'Try to collect.',
          after: 'HC360 refuses. Payment pending: Send the patient to the billing counter, then collect.',
          wait: { role: 'group', name: 'Payment pending' },
          result: { role: 'group', name: 'Payment pending' },
          zoom: 1.3,
        },
        {
          click: { role: 'button', name: 'Cancel' },
          say: 'An out-patient must pay before the tubes are drawn. Cancel, and send Mrs. Narayanan to the billing counter.',
        },
        {
          click: { role: 'button', name: 'Collect 2 tubes' },
          say: 'Ravi Kumar has paid. Click Collect 2 tubes.',
          wait: { role: 'dialog', name: /^Collect samples/ },
        },
        {
          show: { role: 'group', name: 'Tubes to collect' },
          say: 'One EDTA tube for the CBC, and one plain tube for the dengue test. Check the patient’s name and UHID against his wristband or card.',
          zoom: 1.3,
        },
        {
          click: { role: 'button', name: 'Collect and print labels' },
          say: 'Draw the blood, then click Collect and print labels.',
          after: 'The two tubes are collected. Stick each printed label on its tube before the patient leaves.',
          wait: { role: 'dialog', name: 'Samples collected' },
          result: { role: 'dialog', name: 'Samples collected' },
        },
        {
          click: { role: 'button', name: 'Done' },
          say: 'Click Done.',
        },
      ],
    },
    {
      title: 'Receive the tubes and enter a result',
      steps: [
        {
          nav: 'Receive samples',
          say: 'When the tubes reach the lab, open Receive samples.',
          wait: { role: 'textbox', name: 'Scan to receive' },
        },
        {
          fill: [{ role: 'textbox', name: 'Scan to receive' }, '1000000003'],
          say: 'Scan the label of each tube. Here, type the barcode of the dengue tube.',
        },
        {
          press: 'Enter',
          say: 'Press Enter.',
          after: 'The tube is received. It now appears on the Result entry worklist.',
          wait: { text: /1000000003/ },
        },
        {
          nav: 'Result entry',
          say: 'Open Result entry.',
          wait: { role: 'textbox', name: 'Scan sample' },
        },
        {
          fill: [{ role: 'textbox', name: 'Scan sample' }, '1000000003'],
          say: 'Scan the same tube to open its result sheet.',
        },
        {
          press: 'Enter',
          say: 'Press Enter.',
          after: 'The sheet shows the test, its reference range, and the clinical notes the doctor wrote.',
          wait: { role: 'table', name: /^Results for sample/ },
        },
        {
          select: [{ role: 'combobox', name: 'Dengue NS1 antigen result' }, 'Negative'],
          native: true,
          say: 'Choose the result the test shows. Here, Negative.',
          zoom: 1.3,
        },
        {
          click: { role: 'button', name: 'Save results' },
          say: 'Click Save results. You can also press Control S.',
          after: 'The result is saved as Entered. The pathologist verifies it next.',
          wait: { text: 'Entered', exact: true },
        },
      ],
    },
  ],
  recap: [
    'Check the name and the UHID before every draw.',
    'An out-patient’s order must be paid before you collect.',
    'Scan each tube to receive it, then enter the results.',
  ],
  check: [
    {
      q: 'HC360 says Payment pending when you collect. What do you do?',
      a: 'Send the patient to the billing counter, then collect once the order is paid.',
    },
    {
      q: 'Why must you stick the label on the tube before the patient leaves?',
      a: 'The label carries the patient’s name and UHID. A tube without it can be mixed up with another patient’s.',
    },
  ],
};
