// Wave 4 (diagnostics): the radiographer's day. Reading the imaging worklist, taking a new X-ray from the moment the
// patient arrives to the completed scan, and seeing where the studies go next. Recorded as the radiographer
// (Arun Prasad) at 11:10 am (src/web/demo/story.mjs). Both of the morning's X-rays are already taken, so the doctor's
// order for one more (Farah Sheikh, waiting in the OPD) is placed in setup.

const nav = { role: 'navigation', name: 'Main navigation' };
const farah = { role: 'row', name: /Farah Sheikh/ };

export default {
  id: 'w4-radiographer',
  title: 'Radiographer: the imaging worklist and taking an X-ray',
  role: 'Radiographer',
  roleLabel: 'Radiographer',
  user: 'radiographer',
  wave: 4,
  audience: 'For radiographers and technologists of the Nana Hospitals imaging department.',
  intro:
    'Doctors’ imaging orders reach you on the imaging worklist. You take the study, record what you did, and the radiologist reports it. HC360 keeps every step, with your name and the time.',
  objectives: [
    'Read the imaging worklist and find the studies that need you.',
    'Take a study from patient arrived to completed scan.',
    'See where a completed study goes next.',
  ],
  modules: ['radiology'],
  covers: ['/radiology'],
  setup: async ({ api }) => {
    const doctor = await api('doctor');
    const frontOffice = await api('frontoffice');
    const visits = await frontOffice.get('/api/v1/visits?search=Farah%20Sheikh&pageSize=5');
    const visit = visits.items.find((v) => v.patientName === 'Farah Sheikh');
    const services = await doctor.get('/api/v1/masters/lookups/services?category=Radiology&search=RAD-XR-CHEST');
    const xray = services.find((s) => s.code === 'RAD-XR-CHEST');
    await doctor.post('/api/v1/clinical/orders', {
      patientId: visit.patientId,
      encounterId: null,
      visitId: visit.id,
      admissionId: null,
      priority: 'Routine',
      clinicalNotes: 'Chest X-ray PA view',
      items: [{ serviceItemId: xray.id, notes: null }],
      orderSetId: null,
      confirmDuplicates: true,
    });
  },
  scenes: [
    {
      title: 'The imaging worklist',
      steps: [
        {
          click: { within: nav, role: 'button', name: 'Radiology' },
          say: 'Open Radiology from the menu.',
        },
        {
          nav: 'Imaging worklist',
          say: 'Open the Imaging worklist.',
          after: 'Today’s studies are listed with their status. A new study for Farah Sheikh is Ordered. Two are already taken.',
          wait: { role: 'table', name: 'Imaging studies' },
        },
        {
          show: farah,
          say: 'Dr. Arjun Mehta ordered a chest X-ray for Farah Sheikh. It waits for you.',
          zoom: 1.3,
        },
      ],
    },
    {
      title: 'Take the X-ray',
      steps: [
        {
          click: { within: farah, role: 'button', name: 'Patient arrived' },
          say: 'When the patient reaches the department, ask her name and check her UHID, then click Patient arrived.',
          after: 'The next step for this study is Start scan.',
          wait: { within: farah, role: 'button', name: 'Start scan' },
          result: farah,
        },
        {
          click: { within: farah, role: 'button', name: 'Start scan' },
          say: 'Position the patient, then click Start scan.',
          wait: { within: farah, role: 'button', name: 'Complete scan' },
        },
        {
          click: { within: farah, role: 'button', name: 'Complete scan' },
          say: 'When the image is taken, click Complete scan.',
          wait: { role: 'dialog', name: /^Complete scan/ },
        },
        {
          fill: [{ label: 'Radiation dose' }, 'Single PA exposure'],
          say: 'Record the radiation dose.',
          zoom: 1.3,
        },
        {
          fill: [{ label: 'Technologist notes' }, 'Patient stood well for the exposure'],
          say: 'Add a note for the radiologist, if there is something to say.',
          zoom: 1.3,
        },
        {
          click: { within: { role: 'dialog', name: /^Complete scan/ }, role: 'button', name: 'Complete scan' },
          say: 'Click Complete scan.',
          after: 'The status is now Awaiting report: the study is on the radiologist’s reporting worklist.',
          wait: { within: farah, text: 'Awaiting report' },
          result: farah,
        },
      ],
    },
    {
      title: 'Where the studies go next',
      steps: [
        {
          show: { role: 'row', name: /Fatima Begum/ },
          say: 'Fatima Begum’s portable X-ray, taken on the ward, is Awaiting report. The radiologist writes the report next.',
          zoom: 1.3,
        },
      ],
    },
  ],
  recap: [
    'Check the patient’s name and UHID before every study.',
    'Record each step: arrived, started, completed.',
    'A completed scan goes to the radiologist’s reporting worklist.',
  ],
  check: [
    {
      q: 'Why do you click Patient arrived before you start the scan?',
      a: 'HC360 records each step with your name and the time, so the study can be followed from order to report.',
    },
    {
      q: 'Where does a study go when you click Complete scan?',
      a: 'To the radiologist’s reporting worklist, to be reported.',
    },
  ],
  next: 'Radiologist: report and sign off studies',
};
