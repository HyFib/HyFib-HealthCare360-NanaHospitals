// Shared video 3 of 3, for the 22 roles whose menu has Find patient, Today's visits, Patients in hospital and the
// Queue (training/curriculum/role-menus.json): finding the right patient, reading their record, and seeing where
// every patient is this morning. Recorded as the front office executive (Anitha Krishnan) at 11:10 am.

const nav = { role: 'navigation', name: 'Main navigation' };

export default {
  id: '02-find-patient',
  title: 'Find a patient and follow their visit',
  role: '*',
  roleLabel: 'All patient-facing staff',
  user: 'frontoffice',
  desk: true,
  wave: 0,
  audience:
    'For everyone who looks after patients or their bills: front office, doctors, nurses, laboratory, imaging, pharmacy, billing and others.',
  intro:
    'Before you can care for a patient, bill them or give them medicine, you must find the right patient. This video shows how to find a patient safely, how to read their record, and how to see where every patient is in the hospital right now.',
  objectives: [
    'Find a patient by UHID, mobile number or name, and check that it is the right person.',
    'Read the patient’s record: the banner, the journey and the tabs.',
    'See today’s visits, where each patient is, and the queue.',
  ],
  modules: ['patients', 'front-office'],
  covers: ['/patients', '/patients/:id', '/front-office/visits', '/front-office/in-hospital', '/front-office/queue'],
  scenes: [
    {
      title: 'Find the right patient',
      steps: [
        {
          click: { within: nav, role: 'button', name: 'Patients' },
          say: 'Open Patients from the menu.',
        },
        {
          nav: 'Find patient',
          say: 'Then choose Find patient.',
          after: 'The newest patients are at the top. Mobile numbers are hidden in this list, to protect the patient’s privacy.',
          wait: { role: 'table', name: 'Patients' },
        },
        {
          fill: [{ role: 'searchbox', name: 'Search patients' }, 'NH26000012'],
          say: 'Search by UHID, mobile number, ABHA number or name. The UHID is the surest: it is printed on the patient’s card.',
          after: 'One patient matches: Rajendran Pillai.',
          wait: { role: 'row', name: /NH26000012/ },
          result: { role: 'row', name: /NH26000012/ },
          zoom: 1.3,
        },
        {
          show: { role: 'row', name: /NH26000012/ },
          say: 'Before you open a record, always check two things: the name, and the age or the mobile number. Two patients can have the same name.',
          zoom: 1.3,
        },
        {
          click: { role: 'cell', name: 'Rajendran Pillai' },
          say: 'Click the patient to open their record.',
          wait: { role: 'region', name: /^Patient Rajendran Pillai/ },
        },
      ],
    },
    {
      title: 'Read the patient’s record',
      steps: [
        {
          show: { role: 'region', name: /^Patient Rajendran Pillai/ },
          say: 'The banner at the top shows who this is: name, UHID, age and sex, blood group and mobile number. It stays at the top of every screen for this patient.',
          zoom: 1.4,
        },
        {
          show: { role: 'heading', name: 'Journey' },
          focus: { role: 'heading', name: 'Journey' },
          say: 'The journey shows where the patient is in today’s visit or stay: registered, seen by the doctor, tests, pharmacy and billing.',
        },
        {
          show: { role: 'tablist' },
          say: 'The tabs hold the patient’s visits, consents, identity, portal access and documents.',
          zoom: 1.4,
        },
        {
          show: { role: 'heading', name: 'Privacy' },
          say: 'Patient information is private. Open a record only when your work needs it. Every record you open is written in the audit trail, with your name and the time.',
          zoom: 1.4,
        },
      ],
    },
    {
      title: 'Where every patient is now',
      steps: [
        {
          click: { within: nav, role: 'button', name: 'Front office' },
          say: 'Now open Front office.',
        },
        {
          nav: "Today's visits",
          say: 'Today’s visits lists every out-patient visit of the day, with its token, doctor and status.',
          wait: { role: 'heading', level: 1 },
        },
        {
          nav: 'Patients in hospital',
          say: 'Patients in hospital shows everyone in the hospital now: out-patients, casualty, and the patients in a bed.',
          wait: { role: 'table', name: 'Patients in hospital' },
        },
        {
          show: { role: 'row', name: /Vimala Raghavan/ },
          say: 'Each circle is a stage of the visit. Vimala Raghavan is with the doctor now, for the last seven minutes.',
          zoom: 1.3,
        },
        {
          show: { role: 'row', name: /Fatima Begum/ },
          say: 'Fatima Begum is admitted in the female general ward, bed GWF-01.',
          zoom: 1.3,
        },
        {
          nav: 'Queue',
          say: 'The queue shows the tokens of one department, or one doctor.',
          wait: { text: 'Now serving' },
        },
        {
          show: { text: 'Now serving' },
          focus: { role: 'button', name: /^Call next patient/ },
          say: 'Now serving is the token with the doctor: GM-009, Vimala Raghavan.',
          zoom: 1.3,
        },
        {
          show: { role: 'heading', name: /^Waiting/ },
          say: 'The waiting list is in token order, with the time each patient checked in and how long they have waited. The waiting-area screen changes by itself when a token is called.',
          zoom: 1.3,
        },
      ],
    },
  ],
  recap: [
    'Search by UHID, mobile number or name. Check two things before you open a record: the name, and the age or mobile.',
    'The banner shows who the patient is, on every screen. Open a record only when your work needs it.',
    'Patients in hospital shows where every patient is now, and how long they have waited.',
  ],
  check: [
    {
      q: 'Two patients named Lakshmi appear in the search. How do you choose the right one?',
      a: 'Check a second identifier: the UHID on her card, her age, or her mobile number. Never choose by name alone.',
    },
    {
      q: 'Can you open a patient’s record out of curiosity, if you have access?',
      a: 'No. Open a record only when your work needs it. Every record opened is written in the audit trail.',
    },
  ],
  next: 'Your role’s own video, in the order your department goes live',
};
