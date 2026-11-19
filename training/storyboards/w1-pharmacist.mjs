// Wave 1 (pharmacy and stores go live first at Nana Hospitals): the pharmacist's day. Dispensing an out-patient
// e-prescription and taking payment, issuing a controlled antibiotic to the ward against an in-patient prescription,
// and the expiry list and statutory registers HC360 keeps. Recorded as the pharmacist at 11:10 am, when the queue has
// seven out-patient prescriptions and one for the ward (src/web/demo/story.mjs).

const nav = { role: 'navigation', name: 'Main navigation' };

export default {
  id: 'w1-pharmacist',
  title: 'Pharmacist: dispense, issue to the ward, keep the registers',
  role: 'Pharmacist',
  roleLabel: 'Pharmacist',
  user: 'pharmacist',
  wave: 1,
  audience: 'For pharmacists of the OP and IP pharmacy at Nana Hospitals.',
  intro:
    'The pharmacy is the first department of Nana Hospitals to go live on HC360. Doctors’ prescriptions reach you as e-prescriptions, and HC360 suggests the batch, prints the bill and keeps the drug registers for you.',
  objectives: [
    'Dispense an out-patient e-prescription and take payment.',
    'Issue an in-patient prescription to the ward, including a controlled drug.',
    'Find the batches that expire soonest, and the drug registers HC360 keeps.',
  ],
  modules: ['pharmacy', 'inventory'],
  covers: ['/pharmacy', '/pharmacy/queue', '/pharmacy/registers'],
  scenes: [
    {
      title: 'Your pharmacy this morning',
      steps: [
        {
          click: { within: nav, role: 'button', name: 'Pharmacy' },
          say: 'Open Pharmacy from the menu.',
        },
        {
          nav: 'Overview',
          say: 'The overview shows today’s work.',
          after: 'Seven out-patient prescriptions are waiting, and one in-patient prescription is to be issued to the wards.',
          wait: { role: 'button', name: /^OP prescriptions waiting/ },
          result: { role: 'button', name: /^OP prescriptions waiting/ },
        },
      ],
    },
    {
      title: 'Dispense an out-patient prescription',
      steps: [
        {
          nav: 'Dispensing queue',
          say: 'Open the dispensing queue.',
          after:
            'Doctors’ e-prescriptions arrive here by themselves, oldest first, with how long each patient has waited. Press F2 to open the next one.',
          wait: { role: 'table', name: 'Out-patient prescriptions' },
        },
        {
          click: { role: 'cell', name: /^Rajendran Pillai/ },
          say: 'Rajendran Pillai is at the counter. Click his prescription.',
          wait: { role: 'heading', name: 'Medicines' },
        },
        {
          show: { role: 'region', name: /^Patient Rajendran Pillai/ },
          say: 'First, check that this is the patient in front of you: the name, and the UHID on his card. Check the allergies. Here, none are recorded, so ask him.',
          zoom: 1.4,
        },
        {
          show: { role: 'row', name: /^Metformin 500 mg Tablet/ },
          say: 'Dr. Arjun Mehta prescribed metformin 500 mg, twice a day for 30 days: 60 tablets.',
          zoom: 1.3,
        },
        {
          show: { role: 'button', name: 'Choose the batch' },
          say: 'HC360 suggests the batch that expires first, and checks that it lasts the whole course. Give that batch, unless you have a reason not to.',
          zoom: 1.6,
        },
        {
          show: { label: 'Scan to check a medicine' },
          say: 'Scan the strip before you hand it over. HC360 checks that it is the medicine the doctor prescribed.',
          zoom: 1.5,
        },
        {
          fill: [{ label: 'Advice given to the patient' }, 'Take one tablet with breakfast and one with dinner, every day.'],
          say: 'Write the advice you gave the patient. It is printed on the bill.',
          zoom: 1.3,
        },
        {
          click: { within: { role: 'group', name: 'Payment 1 mode' }, role: 'button', name: 'UPI' },
          say: 'Mr. Pillai pays by UPI. Choose UPI. The amount is the bill total, ₹120.',
          zoom: 1.5,
        },
        {
          fill: [{ label: 'UPI reference (UTR)' }, '432612345678'],
          say: 'For UPI, type the 12-digit UTR from the payment screen on his phone. HC360 does not dispense until the payment has a reference.',
          zoom: 1.5,
        },
        {
          click: { role: 'button', name: /^Dispense and bill/ },
          say: 'Click Dispense and bill, or press F9.',
          after:
            'Done. HC360 shows the invoice number and the amount paid. Print the receipt and the medicine labels from here.',
          wait: { role: 'dialog', name: 'Dispensed' },
          result: { within: { role: 'dialog', name: 'Dispensed' }, text: /^Invoice/ },
        },
        {
          press: 'Escape',
          say: 'Close this window, or click Next prescription to serve the next patient.',
        },
      ],
    },
    {
      title: 'Issue an in-patient prescription to the ward',
      steps: [
        {
          nav: 'Dispensing queue',
          say: 'In-patient prescriptions are on their own tab in the queue.',
          wait: { role: 'tab', name: /^In-patients/ },
        },
        {
          click: { role: 'tab', name: /^In-patients/ },
          say: 'Open In-patients.',
          after: 'Fatima Begum, on the female general ward, has a prescription marked Controlled.',
          wait: { role: 'table', name: 'In-patient prescriptions' },
        },
        {
          click: { role: 'cell', name: /^Fatima Begum/ },
          say: 'Open it.',
          wait: { role: 'heading', name: 'Medicines' },
        },
        {
          show: { role: 'row', name: /^Ceftriaxone 1 g Injection/ },
          say: 'Ceftriaxone is a controlled antibiotic. Ten doses are prescribed, but only four are in stock, so four are given now. HC360 keeps the rest as still to give.',
          zoom: 1.3,
        },
        {
          check: { role: 'checkbox', name: /I have checked the prescription/ },
          say: 'The Issue to ward button stays locked until you confirm that you have checked the prescription, the patient and the quantity. Tick the box.',
          zoom: 1.5,
        },
        {
          click: { role: 'button', name: /^Issue to ward/ },
          say: 'Click Issue to ward.',
          after:
            'The medicines are charged to Fatima Begum’s in-patient account. The prescription is now partly dispensed: six doses of ceftriaxone are still to give, when stock arrives.',
          wait: { role: 'dialog', name: 'Issued to the ward' },
          result: { within: { role: 'dialog', name: 'Issued to the ward' }, text: /^Issue / },
        },
        {
          press: 'Escape',
          say: 'Close this window.',
        },
      ],
    },
    {
      title: 'Expiry and the drug registers',
      steps: [
        {
          nav: 'Overview',
          say: 'Back on the overview, Expiring soonest lists the stock that expires in the next 90 days.',
          wait: { role: 'heading', name: 'Expiring soonest' },
        },
        {
          show: { role: 'heading', name: 'Expiring soonest' },
          say: 'Sell these first, or return them to the vendor before they expire.',
          zoom: 1.3,
        },
        {
          nav: 'Drug registers',
          say: 'Drug registers holds the statutory registers.',
          after:
            'HC360 writes them by itself, from every sale, issue, receipt and adjustment. Nobody can change them. Schedule H1 drugs, Schedule X drugs and narcotic drugs are on the first tab. Schedule H drugs are on the second.',
          wait: { role: 'heading', name: 'Drug registers' },
        },
      ],
    },
  ],
  recap: [
    'Check the patient and the allergies before you dispense. Give the batch HC360 suggests, and scan the strip.',
    'A controlled drug for the ward needs your confirmation before it can be issued.',
    'Sell or return what expires soonest. The drug registers are written for you.',
  ],
  check: [
    {
      q: 'HC360 suggests a batch that expires in March, but you picked up one that expires in December next year. Which do you give?',
      a: 'The one HC360 suggests, which expires first, as long as it lasts the course. Older stock is used first, so less of it expires on the shelf.',
    },
    {
      q: 'Why will Issue to ward not work for Fatima Begum’s ceftriaxone until you tick the box?',
      a: 'It is a controlled drug. HC360 asks you to confirm that you have checked the prescription, the patient and the quantity.',
    },
  ],
};
