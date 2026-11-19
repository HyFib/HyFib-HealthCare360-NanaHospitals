// Wave 5 (in-patient services): the dialysis technician's day. Mohammed Irfan (a seeded patient, NH26000005) is
// enrolled in the unit with his dry weight, access, schedule and sero status, as the unit would have done at his first
// visit; that is made in setup, as the technician. The video books today's morning session, assigns a machine of his
// infection category, records the pre-dialysis assessment, starts the session, adds an hourly reading, completes it
// and records the machine's disinfection. Recorded as the technician at 11:10 am. The training server runs the
// session in minutes; the narration does not mention how long it lasts.

const nav = { role: 'navigation', name: 'Main navigation' };

export default {
  id: 'w5-dialysis-technician',
  title: 'Dialysis technician: book, run and complete a dialysis session',
  role: 'DialysisTechnician',
  roleLabel: 'Dialysis technician',
  user: 'dialysis',
  wave: 5,
  audience: 'For the dialysis technicians of Nana Hospitals.',
  intro:
    'Patients in the dialysis unit come on a weekly schedule. In HC360 you book each session, give the patient a machine of their own infection category, and record the session from the first weight to the last.',
  objectives: [
    'Book a session and assign a machine of the patient’s infection category.',
    'Record the assessment before dialysis, then start the session.',
    'Record the readings, complete the session and record the disinfection.',
  ],
  modules: ['dialysis'],
  covers: ['/dialysis', '/dialysis/sessions', '/dialysis/machines'],
  setup: async ({ api }) => {
    // The patient's enrolment in the unit (dry weight, access, schedule, sero status), made by the technician.
    const technician = await api('dialysis');
    const frontOffice = await api('frontoffice');
    const found = await frontOffice.get('/api/v1/patients?search=Mohammed%20Irfan&pageSize=5');
    const patient = (found.items ?? found).find((p) => p.fullName.includes('Irfan'));
    await technician.put(`/api/v1/dialysis/patients/${patient.id}`, {
      dryWeightKg: 62.5,
      vascularAccess: 'AVF',
      accessSite: 'Left forearm',
      accessCreatedOn: '2026-06-10',
      scheduleDays: ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday'],
      shift: 'Morning',
      sessionMinutes: 240,
      seroStatus: 'Negative',
      seroTestedOn: '2026-11-02',
      reusePolicy: 'SingleUse',
      maxDialyzerUses: 1,
      nephrologistId: null,
      primaryDiagnosis: 'Chronic kidney disease, stage 5',
      anticoagulationNotes: null,
      isActive: true,
      version: null,
    });
  },
  scenes: [
    {
      title: 'Book the session and assign a machine',
      steps: [
        {
          click: { within: nav, role: 'button', name: 'Dialysis' },
          say: 'Open Dialysis from the menu.',
        },
        {
          nav: 'Dialysis board',
          say: 'Open the Dialysis board.',
          after: 'The board shows today’s machines. One patient, Mohammed Irfan, is due today and is not booked yet.',
          wait: { role: 'heading', name: /Due today, not booked \(1\)/ },
          result: { role: 'heading', name: /Due today, not booked \(1\)/ },
        },
        {
          show: { role: 'heading', name: /Sero-negative machines/ },
          say: 'Each machine is kept for one infection category. A patient uses only a machine of their own category.',
          zoom: 1.3,
        },
        {
          click: { role: 'button', name: 'Book session' },
          say: 'Click Book session.',
          wait: { role: 'dialog', name: 'Book a dialysis session' },
        },
        {
          fill: [{ label: 'Patient' }, 'Irfan'],
          say: 'Search for Mohammed Irfan. Only patients enrolled in the dialysis unit can be booked.',
          wait: { role: 'option', name: /Mohammed Irfan/ },
        },
        {
          click: { role: 'option', name: /Mohammed Irfan/ },
          say: 'Choose him.',
        },
        {
          click: { role: 'button', name: 'Book session', last: true },
          say: 'Keep today and the morning shift, and click Book session.',
          after: 'The session is booked, with no machine yet. HC360 asks for a machine of his category, sero-negative.',
          wait: { role: 'button', name: 'Assign machine' },
        },
        {
          click: { role: 'button', name: 'Assign machine' },
          say: 'Click Assign machine.',
          wait: { role: 'dialog', name: /Assign a machine/ },
        },
        {
          show: { role: 'radiogroup', name: 'Machine' },
          say: 'The machines for other infection categories are greyed out, each with its reason.',
          zoom: 1.2,
        },
        {
          click: { role: 'radio', name: /^HD-01/ },
          say: 'Choose HD-01.',
        },
        {
          click: { within: { role: 'dialog', name: /Assign a machine/ }, role: 'button', name: 'Assign machine' },
          say: 'Click Assign machine.',
          after: 'The session now shows machine HD-01.',
          wait: { text: /machine HD-01/, exact: false },
        },
      ],
    },
    {
      title: 'Before dialysis, and start',
      steps: [
        {
          fill: [{ label: 'Weight before (kg)' }, '64.2'],
          say: 'Weigh Mr. Irfan and write his weight before dialysis: 64.2 kg.',
          zoom: 1.3,
        },
        {
          fill: [{ label: 'Systolic BP' }, '138'],
          say: 'Write the systolic blood pressure, 138.',
          zoom: 1.3,
        },
        {
          fill: [{ label: 'Diastolic BP' }, '84'],
          say: 'Then the diastolic, 84.',
          zoom: 1.3,
        },
        {
          fill: [{ label: 'Pulse (/min)' }, '76'],
          say: 'Then the pulse, 76 a minute.',
          zoom: 1.3,
        },
        {
          fill: [{ label: 'Temperature (°C)' }, '36.8'],
          say: 'And the temperature, 36.8 °C.',
          zoom: 1.3,
        },
        {
          click: { role: 'button', name: 'Use 1,700 ml' },
          say: 'His dry weight is 62.5 kg, so he is 1.7 kg above it. HC360 offers 1,700 ml as the target. The goal is a clinical decision.',
          zoom: 1.3,
        },
        {
          click: { role: 'button', name: 'Save pre-dialysis assessment' },
          say: 'Click Save pre-dialysis assessment.',
          after: 'The figures are saved: 64.2 kg, 138/84 mmHg, and a target of 1,700 ml.',
          wait: { role: 'button', name: 'Connect and start dialysis' },
        },
        {
          click: { role: 'radio', name: /^HD-01/ },
          say: 'In Connect and start, choose the machine. HC360 connects only a free, disinfected machine of his category.',
        },
        {
          click: { role: 'radio', name: 'Register a new dialyzer (first use)' },
          say: 'His dialyzer is single use, so register a new one.',
        },
        {
          fill: [{ label: 'Serial number' }, 'DLZ-482913'],
          say: 'Type the dialyzer’s serial number, or scan it.',
          zoom: 1.3,
        },
        {
          click: { role: 'button', name: 'Connect and start dialysis' },
          say: 'Click Connect and start dialysis.',
          after: 'The session is running. The During dialysis section is open for the hourly readings.',
          wait: { role: 'heading', name: 'During dialysis' },
        },
      ],
    },
    {
      title: 'Readings and completing the session',
      steps: [
        {
          fill: [{ label: 'Systolic', exact: true }, '132'],
          say: 'Every hour, record a reading. Write the systolic pressure, 132.',
          zoom: 1.2,
        },
        {
          fill: [{ label: 'Diastolic', exact: true }, '80'],
          say: 'The diastolic, 80.',
          zoom: 1.2,
        },
        {
          fill: [{ label: 'Pulse', exact: true }, '74'],
          say: 'The pulse, 74.',
          zoom: 1.2,
        },
        {
          fill: [{ label: 'Blood flow (ml/min)' }, '300'],
          say: 'And the blood flow, 300 ml a minute.',
          zoom: 1.2,
        },
        {
          click: { role: 'button', name: 'Add reading' },
          say: 'Click Add reading.',
          after: 'The reading is listed with its time.',
          wait: { role: 'table', name: 'Intra-dialytic readings' },
          result: { role: 'table', name: 'Intra-dialytic readings' },
        },
        {
          fill: [{ label: 'Weight after (kg)' }, '62.6'],
          say: 'When the session ends, write the weight after dialysis: 62.6 kg.',
          zoom: 1.3,
        },
        {
          fill: [{ label: 'Systolic BP', exact: true, last: true }, '128'],
          say: 'Write the blood pressure after, 128 over 78. First the systolic.',
          zoom: 1.3,
        },
        {
          fill: [{ label: 'Diastolic BP', exact: true, last: true }, '78'],
          say: 'Then the diastolic.',
          zoom: 1.3,
        },
        {
          click: { role: 'button', name: 'Complete dialysis' },
          say: 'Click Complete dialysis.',
          wait: { role: 'button', name: 'Complete session' },
        },
        {
          click: { role: 'button', name: 'Complete session' },
          say: 'HC360 records the end time as now, frees the machine and bills the session. Click Complete session.',
          after: 'The summary shows the weight, 64.2 to 62.6 kg, 1,600 ml of fluid removed, and that the session is Billed.',
          wait: { role: 'heading', name: 'Summary' },
          result: { role: 'heading', name: 'Summary' },
        },
      ],
    },
    {
      title: 'Disinfect the machine',
      steps: [
        {
          nav: 'Dialysis board',
          say: 'Open the Dialysis board.',
          after: 'Machine HD-01 now needs disinfection before the next patient.',
          wait: { role: 'button', name: 'Record disinfection' },
          result: { role: 'button', name: 'Record disinfection' },
        },
        {
          click: { role: 'button', name: 'Record disinfection' },
          say: 'After you disinfect the machine, click Record disinfection.',
          wait: { role: 'dialog', name: /Record disinfection of HD-01/ },
        },
        {
          click: { within: { role: 'dialog', name: /Record disinfection of HD-01/ }, role: 'button', name: 'Record disinfection' },
          say: 'Choose the method, and click Record disinfection. HC360 does not connect the next patient to this machine until you do.',
          after: 'HD-01 is recorded as disinfected, and free.',
          wait: { text: 'Disinfected', exact: false },
        },
      ],
    },
  ],
  recap: [
    'Give each patient a machine of their own infection category. HC360 greys out the others.',
    'Record the weight and blood pressure before dialysis first. Then connect, add the hourly readings, and complete.',
    'Record the machine’s disinfection before the next patient.',
  ],
  check: [
    {
      q: 'Why are some machines greyed out when you assign a machine to Mohammed Irfan?',
      a: 'Each machine is kept for one infection category. He is sero-negative, so he can use only the sero-negative machines.',
    },
    {
      q: 'What must you record before you can connect the patient and start dialysis?',
      a: 'The weight, blood pressure and pulse before dialysis. HC360 asks for them first.',
    },
  ],
  next: 'Nurse: care of a patient on the ward',
};
