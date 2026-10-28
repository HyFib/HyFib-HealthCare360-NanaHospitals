/**
 * Who the demo lets you be, and where each one's part of the recorded morning shows: the roles' guided tours. Each
 * place is a screen of that role's own menu; build.mjs refuses to pack a demo in which a place was not recorded for
 * its role, and verify.mjs opens every one. What the notes say is what story.mjs made happen.
 */

export interface Place {
  label: string;
  path: string;
  /** What the screen shows of the morning. */
  note: string;
}

export interface Persona {
  role: string;
  does: string;
  places: Place[];
}

/** The patient the demo lets you be, on the patient portal (capture.mjs signs in as them): seen this morning. */
export const PORTAL_PATIENT = 'Rajendran Pillai';

export const PERSONAS: Persona[] = [
  {
    role: 'frontoffice',
    does: 'Registers patients, books appointments and runs the OPD queue.',
    places: [
      {
        label: 'Queue',
        path: '/front-office/queue',
        note: 'Vimala Raghavan is with the doctor; Anand Joseph and Farah Sheikh are waiting.',
      },
      {
        label: "Today's appointments",
        path: '/scheduling',
        note: 'Joseph Mathew moved to 11:30; two appointments called off for patients already seen or admitted.',
      },
      {
        label: 'Find patient',
        path: '/patients',
        note: 'Everyone seen today; open one for their visits, bills and documents.',
      },
    ],
  },
  {
    role: 'doctor',
    does: 'Sees outpatients in General Medicine and does the ward round.',
    places: [
      {
        label: 'My worklist',
        path: '/emr',
        note: 'Vimala Raghavan is in with you; results are back for Meenakshi, Rajendran and Karthik.',
      },
      {
        label: 'Admissions',
        path: '/inpatient/admissions',
        note: 'Fatima Begum in GWF-01, admitted from casualty last evening with pneumonia; open her stay.',
      },
      {
        label: 'ER board',
        path: '/emergency',
        note: 'Senthil Kumar in casualty, ESI 2 after a fall from a two-wheeler, under your care.',
      },
    ],
  },
  {
    role: 'nurse',
    does: 'Runs the female general ward and takes vitals in the OPD.',
    places: [
      {
        label: 'Nursing station',
        path: '/nursing',
        note: 'The female ward: Fatima Begum in GWF-01, the medication round (nothing due this hour) and the handover sheet.',
      },
      {
        label: 'ER board',
        path: '/emergency',
        note: 'Senthil Kumar, triaged ESI 2 (emergent) after a fall from a two-wheeler; he came in by 108 ambulance.',
      },
    ],
  },
  {
    role: 'lab',
    does: 'Draws samples and enters results.',
    places: [
      {
        label: 'Sample collection',
        path: '/lab/collection',
        note: "Ravi Kumar's and Lakshmi Narayanan's tests, waiting for them to be drawn.",
      },
      {
        label: 'Result entry',
        path: '/lab/worklist',
        note: "Fatima Begum's ward bloods, entered and waiting for the pathologist.",
      },
      { label: 'Lab orders', path: '/lab/orders', note: "The morning's orders, from the OPD and the ward." },
    ],
  },
  {
    role: 'pathologist',
    does: 'Verifies and releases lab reports.',
    places: [
      {
        label: 'Result entry',
        path: '/lab/worklist',
        note: "Fatima Begum's CRP and CBC wait for your verification; open the CRP for its result, 64 mg/L.",
      },
      {
        label: 'Lab reports',
        path: '/lab/reports',
        note: "Meenakshi Sundaram's fever work-up and Rajendran Pillai's diabetes panel, released this morning.",
      },
    ],
  },
  {
    role: 'radiographer',
    does: 'Takes the X-rays and scans.',
    places: [
      {
        label: 'Imaging worklist',
        path: '/radiology',
        note: "Karthik Subramanian's and Fatima Begum's chest X-rays, taken this morning.",
      },
    ],
  },
  {
    role: 'radiologist',
    does: 'Reports the imaging.',
    places: [
      {
        label: 'Reporting worklist',
        path: '/radiology/reporting',
        note: "Fatima Begum's portable chest X-ray waits for your report.",
      },
    ],
  },
  {
    role: 'pharmacist',
    does: 'Dispenses prescriptions and keeps the stock.',
    places: [
      {
        label: 'Dispensing queue',
        path: '/pharmacy/queue',
        note: "The OPD's prescriptions, Rajendran Pillai's metformin and Lakshmi Narayanan's medicines among them.",
      },
      {
        label: 'Overview',
        path: '/pharmacy',
        note: "Prescriptions waiting, the day's takings and the batches that expire soonest.",
      },
    ],
  },
  {
    role: 'billing',
    does: 'Bills outpatients, settles in-patient accounts and closes the day.',
    places: [
      {
        label: 'Cashier desk',
        path: '/billing',
        note: "Lakshmi Narayanan's OP bill is due; the morning's collections so far.",
      },
      {
        label: 'Bills',
        path: '/billing/bills',
        note: "Today's bills, Sunita Devi's final in-patient bill among them, paid by card when she went home.",
      },
      {
        label: 'In-patient accounts',
        path: '/billing/ip',
        note: "Fatima Begum's running account, open since she was admitted last evening.",
      },
      {
        label: 'Day-end report',
        path: '/billing/day-end',
        note: 'Cash, card and UPI collected today, by mode and by cashier.',
      },
    ],
  },
  {
    role: 'hospitaladmin',
    does: 'Watches the hospital: its numbers, beds and reports.',
    places: [
      {
        label: 'Executive dashboard',
        path: '/analytics',
        note: 'The month so far: OP visits, admissions, occupancy, lab turnaround, revenue and collections.',
      },
      {
        label: 'Bed board',
        path: '/inpatient/beds',
        note: 'Every ward and bed: Fatima Begum in GWF-01, and one bed being cleaned for the next patient.',
      },
    ],
  },
  {
    role: 'admin',
    does: 'Runs the system: users, roles, settings, backups and the audit log.',
    places: [
      {
        label: 'Users',
        path: '/admin/users',
        note: 'Every member of staff, with their roles, two-step sign-in and last sign-in.',
      },
      {
        label: 'Audit log',
        path: '/admin/audit',
        note: 'Every sign-in, record opened and change this morning, in a chain you can verify.',
      },
      {
        label: 'System health',
        path: '/admin/system',
        note: 'What the server checks about itself. This demo machine fails several checks (disk, time server, messages, PDFs, backup key), as a new install would.',
      },
    ],
  },
];

/** The recorded morning, in the order it happened (IST), up to 11:10 am, the moment the demo shows. */
export const MORNING: { time: string; what: string }[] = [
  {
    time: '9:03',
    what: 'Ward round: Fatima Begum, admitted last evening with pneumonia; Sunita Devi fit for discharge.',
  },
  {
    time: '9:10',
    what: "The OPD's first tokens are seen in turn: Ravi Kumar's fever, Lakshmi Narayanan's knees, Aarav, Priya and Mohammed.",
  },
  { time: '9:56', what: 'Meenakshi Sundaram, three days of fever: CRP and CBC back at 10:43, medicines dispensed.' },
  { time: '10:09', what: 'Rajendran Pillai, diabetes review: HbA1c 8.4%; metformin waiting at the pharmacy.' },
  { time: '10:21', what: "Karthik Subramanian's chest X-ray, reported: right upper zone consolidation." },
  { time: '10:47', what: 'Sunita Devi goes home, her bill settled by card.' },
  { time: '10:58', what: 'Senthil Kumar in casualty after a fall from a two-wheeler, triage level 2.' },
  { time: '11:05', what: 'Vimala Raghavan with the doctor now, chest discomfort on walking: ECG ordered.' },
  { time: '11:10', what: "Anand Joseph and Farah Sheikh waiting; Fatima Begum's bloods and X-ray waiting for review." },
];
