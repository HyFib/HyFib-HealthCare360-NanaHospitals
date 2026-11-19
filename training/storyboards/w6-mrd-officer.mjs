// Wave 6 (back office): the medical records officer's day. Coding the diagnosis of a discharged patient (ICD-10),
// checking a chart for gaps before it is completed, recording a request for a patient file, and the registers the
// medical records department keeps (medico-legal cases, births, deaths). Recorded as the MRD officer (Gowri Shankar)
// at 11:10 am, when Sunita Devi has just been discharged and waits in the coding queue.

const nav = { role: 'navigation', name: 'Main navigation' };

export default {
  id: 'w6-mrd-officer',
  title: 'Medical records officer: code, complete charts, keep the registers',
  role: 'MrdOfficer',
  roleLabel: 'Medical records officer',
  user: 'mrd',
  wave: 6,
  audience: 'For the medical records officer and coders of Nana Hospitals.',
  intro:
    'When a patient goes home, the record is not finished. You code the diagnosis, check that the chart is complete, and answer requests for files. HC360 keeps the queues and the statutory registers for you.',
  objectives: [
    'Code the diagnosis of a discharged patient with ICD-10.',
    'Check a chart for gaps, and see what the doctor still has to complete.',
    'Record a request for a patient’s file, and find the medico-legal, birth and death registers.',
  ],
  modules: ['mrd'],
  covers: ['/mrd', '/mrd/coding', '/mrd/records', '/mrd/requests', '/emergency/mlc', '/mrd/births', '/mrd/deaths'],
  scenes: [
    {
      title: 'Code a discharged patient',
      steps: [
        {
          click: { within: nav, role: 'button', name: 'Medical records' },
          say: 'Open Medical records from the menu.',
        },
        {
          nav: 'Overview',
          say: 'The overview shows today’s work.',
          after: 'One discharged episode is waiting for coding. No chart is overdue.',
          wait: { role: 'button', name: /^Waiting for coding/ },
          result: { role: 'button', name: /^Waiting for coding/ },
        },
        {
          nav: 'Coding queue',
          say: 'Open the Coding queue.',
          after: 'Sunita Devi, discharged this morning, is the only episode to code.',
          wait: { role: 'table', name: 'Coding queue' },
        },
        {
          click: { role: 'cell', name: /^Sunita Devi/ },
          say: 'Open Sunita Devi’s episode.',
          wait: { role: 'heading', name: /^Coding · Sunita Devi/ },
        },
        {
          show: { role: 'heading', name: /^Doctors.{1,3} diagnoses/  },
          say: 'On the right are the diagnoses the doctors recorded. HC360 offers them as suggestions, so you do not retype them.',
          zoom: 1.3,
        },
        {
          click: { role: 'button', name: 'Add A09.9' },
          say: 'Click the plus sign to add A09.9, gastroenteritis.',
          after: 'The code is in the list, marked as the principal diagnosis, An episode has exactly one principal diagnosis.',
          wait: { role: 'table', name: 'Codes' },
        },
        {
          click: { role: 'button', name: /^Save codes/ },
          say: 'Click Save codes, or press Control and S.',
          after: 'The episode is now Coded. A second coder must verify it.',
          wait: { text: 'A different coder must verify your coding.' },
        },
        {
          show: { role: 'button', name: 'Verify' },
          say: 'HC360 says a different coder must verify your coding. Verified coding is used for insurance claims and statistics, so a second coder checks it.',
          zoom: 1.5,
        },
      ],
    },
    {
      title: 'Check the chart',
      steps: [
        {
          nav: 'Patient charts',
          say: 'Open Patient charts.',
          after: 'Charts are completed within 7 days of discharge. Sunita Devi’s is due on 26 Nov 2026.',
          wait: { role: 'table', name: 'Patient charts' },
        },
        {
          click: { role: 'cell', name: /^Sunita Devi/ },
          say: 'Open Sunita Devi’s chart.',
          wait: { role: 'heading', name: /^Chart · Sunita Devi/ },
        },
        {
          click: { role: 'button', name: 'Check for gaps' },
          say: 'Click Check for gaps. HC360 looks for missing notes, diagnoses and discharge summaries.',
          after: 'One deficiency is open: Diagnosis missing, assigned to Dr. Arjun Mehta. It is on his worklist, and the chart cannot be completed until it is resolved.',
          wait: { role: 'heading', name: /^Deficiencies \(1 open\)/ },
          result: { role: 'table', name: 'Deficiencies' },
        },
      ],
    },
    {
      title: 'Record requests and the registers',
      steps: [
        {
          nav: 'Record requests',
          say: 'Open Record requests.',
          wait: { role: 'heading', name: 'Record requests' },
        },
        {
          click: { role: 'button', name: 'New request' },
          say: 'A department, an insurer, a court or the patient asks for a file. Click New request.',
          wait: { role: 'dialog', name: 'New record request' },
        },
        {
          fill: [{ label: 'Patient' }, 'Fatima'],
          say: 'Find the patient.',
          wait: { role: 'option', name: /Fatima Begum/ },
        },
        {
          click: { role: 'option', name: /Fatima Begum/ },
          say: 'Choose Fatima Begum.',
        },
        {
          fill: [{ label: 'Requested by' }, 'Karthikeyan Murugan'],
          say: 'Type who is asking for the file.',
          zoom: 1.3,
        },
        {
          fill: [{ label: 'Department' }, 'Insurance desk'],
          say: 'And their department.',
          zoom: 1.3,
        },
        {
          select: [{ label: 'Purpose' }, 'Insurance claim'],
          native: true,
          say: 'Choose the purpose. The purpose decides how long the file may stay out.',
          zoom: 1.3,
        },
        {
          click: { role: 'button', name: 'Record request' },
          say: 'Click Record request.',
          after: 'The request is in the list for Fatima Begum, with its status.',
          wait: { role: 'row', name: /Fatima Begum/ },
        },
        {
          click: { within: nav, role: 'button', name: 'Emergency' },
          say: 'The medico-legal case register is under Emergency.',
        },
        {
          nav: 'MLC register',
          say: 'Open the MLC register.',
          after: 'It lists medico-legal cases and when the police were told. This morning it is empty: no case has been marked as medico-legal.',
          wait: { role: 'heading', name: 'MLC register' },
        },
        {
          click: { within: nav, role: 'button', name: 'Medical records' },
          say: 'Back in Medical records, the birth and death registers are kept the same way.',
        },
        {
          nav: 'Births',
          say: 'Open Births.',
          after: 'Every birth in the month is listed. Each must be reported to the Registrar within 21 days.',
          wait: { role: 'heading', name: 'Birth register' },
        },
      ],
    },
  ],
  recap: [
    'Code each discharged episode with exactly one principal diagnosis. A second coder verifies it.',
    'Check for gaps, so that every deficiency is with the doctor before the chart is completed.',
    'Record every file request. The birth and death registers go to the Registrar within 21 days.',
  ],
  check: [
    {
      q: 'Why can you not verify the coding you saved yourself?',
      a: 'Verified coding is used for insurance claims and statistics, so a second coder checks it.',
    },
    {
      q: 'Within how many days of birth or death is each case reported to the Registrar?',
      a: 'Within 21 days.',
    },
  ],
  next: 'CRM executive: leads, follow-up calls and campaigns',
};
