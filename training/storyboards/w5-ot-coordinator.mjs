// Wave 5 (in-patient services): the OT coordinator's morning. Reading the OT calendar, booking an urgent minor
// operation for a patient seen in casualty (Senthil Kumar, fall from a two-wheeler, ESI 2), and following the case
// through the day-of-surgery steps and the pre-operative checklist. Recorded as Jayanthi Kannan at 11:10 am, when all
// three theatres are free and nothing is waiting for a slot (src/web/demo/story.mjs).

const nav = { role: 'navigation', name: 'Main navigation' };

export default {
  id: 'w5-ot-coordinator',
  title: 'OT coordinator: read the calendar, book a surgery, follow the checklists',
  role: 'OtCoordinator',
  roleLabel: 'OT coordinator',
  user: 'theatre',
  wave: 5,
  audience: 'For the operation theatre coordinators and OT in-charges of Nana Hospitals.',
  intro:
    'The operation theatre is part of the in-patient wave. HC360 holds the theatres, the calendar and every surgery record. You book the operations, and HC360 stops a double booking before it happens.',
  objectives: [
    'Read the OT calendar: the theatres, the day’s cases and what waits for a slot.',
    'Book a surgery: the patient, the procedure, the side, the team and the theatre time.',
    'Follow a case through the day-of-surgery steps and the pre-operative checklist.',
  ],
  modules: ['ot', 'patients'],
  covers: ['/ot', '/ot/book', '/ot/bookings'],
  scenes: [
    {
      title: 'The OT calendar',
      steps: [
        {
          click: { within: nav, role: 'button', name: 'Operation theatre' },
          say: 'Open Operation theatre from the menu.',
        },
        {
          nav: 'OT calendar',
          say: 'Then choose OT calendar.',
          after: 'Today is Thursday, 19 November. The calendar shows each theatre with its cases.',
          wait: { role: 'heading', name: 'OT calendar' },
        },
        {
          show: { role: 'group', name: /^OT-3 Minor OT/ },
          say: 'The hospital has three theatres: OT-1 and OT-2 are main theatres, and OT-3 is the minor theatre. No case is booked yet today.',
          zoom: 1.2,
        },
        {
          show: { role: 'heading', name: /^Waiting for a slot/ },
          say: 'Requests from doctors’ orders wait here for a theatre and a time, emergencies first. Nothing is waiting now.',
          zoom: 1.3,
        },
      ],
    },
    {
      title: 'Book a surgery',
      steps: [
        {
          nav: 'Book surgery',
          say: 'Senthil Kumar came to casualty after a fall from a two-wheeler. His wound needs debridement and stitches. Open Book surgery.',
          wait: { role: 'heading', name: 'Book surgery', level: 1 },
        },
        {
          fill: [{ label: 'Patient' }, 'NH26000013'],
          say: 'Find the patient by UHID.',
          wait: { role: 'option', name: /^Senthil Kumar/ },
        },
        {
          click: { role: 'option', name: /^Senthil Kumar/ },
          say: 'Check the name and the age before you choose. Two patients can have the same name.',
          zoom: 1.3,
        },
        {
          fill: [{ label: 'Planned procedure' }, 'Wound debridement and suturing'],
          say: 'Type the planned procedure in words. For a major operation, choose it from the service catalogue, so that its charge is posted.',
          zoom: 1.3,
        },
        {
          click: { role: 'radio', name: 'No side (midline or not applicable)' },
          say: 'Choose the operative side. It is checked again at the WHO Time-out, to prevent surgery on the wrong side.',
          zoom: 1.3,
        },
        {
          fill: [{ label: 'Diagnosis' }, 'Lacerated wound after a fall from a two-wheeler'],
          say: 'Write the diagnosis.',
          zoom: 1.3,
        },
        {
          fill: [{ label: 'Operating surgeon' }, 'Meera'],
          say: 'Search for the operating surgeon.',
          wait: { role: 'option', name: 'Dr. Meera Pillai' },
        },
        {
          click: { role: 'option', name: 'Dr. Meera Pillai' },
          say: 'Choose Dr. Meera Pillai, General Surgery.',
          zoom: 1.3,
        },
        {
          select: [{ label: 'Anaesthesia' }, 'Local (given by the surgeon)'],
          native: true,
          say: 'Choose the anaesthesia. For local anaesthesia given by the surgeon, no anaesthetist is needed.',
          zoom: 1.3,
        },
        {
          click: { role: 'radio', name: 'Urgent' },
          say: 'Set the priority to Urgent. Emergency and urgent cases are listed first everywhere.',
          zoom: 1.3,
        },
        {
          select: [{ label: 'Theatre' }, 'OT-3 · Minor OT'],
          native: true,
          say: 'Give the slot now. Choose the minor theatre, OT-3.',
          zoom: 1.3,
        },
        {
          fill: [{ label: 'Start time' }, '12:00'],
          say: 'Type the start time. It is the hospital’s time.',
          zoom: 1.3,
        },
        {
          click: { role: 'button', name: '30 min' },
          say: 'Choose the expected duration. The case takes 30 minutes.',
          after: 'The summary shows 12:00 pm to 12:30 pm, and that nothing else is booked for this theatre, surgeon or patient.',
          wait: { text: /Nothing else is booked that day for this theatre, surgeon or patient/, exact: false },
          zoom: 1.2,
        },
        {
          click: { within: { role: 'complementary' }, role: 'button', name: 'Book surgery' },
          say: 'Click Book surgery. HC360 checks for a double booking before it saves.',
          after: 'The surgery is booked, with a booking number. It is Scheduled, and Urgent.',
          wait: { role: 'heading', name: 'Wound debridement and suturing', level: 1 },
        },
      ],
    },
    {
      title: 'The day of surgery',
      steps: [
        {
          show: { role: 'navigation', name: 'Day of surgery steps' },
          say: 'The day-of-surgery steps follow the case. Theatre and time are booked. Next come the pre-operative checklist, the WHO Sign-in, the Time-out and the Sign-out.',
          zoom: 1.2,
        },
        {
          click: { role: 'button', name: /^Next to do Pre-operative checklist/ },
          say: 'Open the pre-operative checklist.',
          after: 'Ten items, such as signed consent, the identity band and the allergy check. None is checked yet.',
          wait: { role: 'heading', name: 'Pre-operative checklist' },
        },
        {
          show: { role: 'list', name: 'Pre-operative checklist' },
          say: 'The ward or pre-op nurse marks each item done, or not applicable where it is allowed. Your name and the time are recorded with every change.',
          zoom: 1.1,
        },
        {
          nav: 'OT calendar',
          say: 'Back on the OT calendar, the case now stands in OT-3 at 12:00 pm.',
          wait: { role: 'button', name: /Wound debridement and suturing/ },
        },
      ],
    },
  ],
  recap: [
    'The OT calendar shows each theatre, the day’s cases, and the requests that wait for a slot.',
    'To book a surgery, give the patient, procedure, side, surgeon, anaesthesia, theatre and time. HC360 stops a double booking.',
    'The booked case carries its checklists: the pre-operative checklist, then the WHO Sign-in, Time-out and Sign-out.',
  ],
  check: [
    {
      q: 'Why does the booking form ask for the operative side?',
      a: 'It is checked again at the WHO Time-out, before the first cut, to prevent surgery on the wrong side.',
    },
    {
      q: 'You choose local anaesthesia given by the surgeon. Do you need to name an anaesthetist?',
      a: 'No. An anaesthetist is needed for every technique except local and topical anaesthesia given by the surgeon.',
    },
  ],
};
