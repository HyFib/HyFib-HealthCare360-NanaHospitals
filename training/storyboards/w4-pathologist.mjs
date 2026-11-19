// Wave 4 (diagnostics): the pathologist's morning. Verifying and releasing the results the technician entered for
// Fatima Begum (a CRP of 64 mg/L and a CBC), what a critical value asks of you, and the released reports.
// Recorded as the pathologist (Dr. Shalini Rao) at 11:10 am (src/web/demo/story.mjs).

const nav = { role: 'navigation', name: 'Main navigation' };

export default {
  id: 'w4-pathologist',
  title: 'Pathologist: verify and release lab reports',
  role: 'Pathologist',
  roleLabel: 'Pathologist',
  user: 'pathologist',
  wave: 4,
  audience: 'For the pathologists of the Nana Hospitals laboratory.',
  intro:
    'No lab result reaches a doctor until you have verified it. HC360 shows you what the technician entered, flags every value outside its reference range, and releases the report when you sign it off.',
  objectives: [
    'Find the results that wait for you.',
    'Verify and release a report, and know what a critical value asks of you.',
    'Find released reports and the critical call register.',
  ],
  modules: ['lab'],
  covers: ['/lab', '/lab/worklist', '/lab/reports', '/lab/critical-calls'],
  scenes: [
    {
      title: 'What waits for you',
      steps: [
        {
          click: { within: nav, role: 'button', name: 'Laboratory' },
          say: 'Open Laboratory from the menu.',
        },
        {
          nav: 'Lab overview',
          say: 'The lab overview shows today’s work.',
          after: 'Two samples wait for verification. No critical values are waiting for a call.',
          wait: { role: 'button', name: /^Awaiting verification/ },
          result: { role: 'button', name: /^Awaiting verification/ },
        },
        {
          nav: 'Result entry',
          say: 'Open Result entry. Samples with results to verify are listed here.',
          after: 'Both samples are Fatima Begum’s, from the female general ward: a CRP and a CBC.',
          wait: { role: 'table', name: 'Samples in the lab' },
        },
      ],
    },
    {
      title: 'Verify and release the CRP',
      steps: [
        {
          click: { role: 'cell', name: /C-reactive protein/ },
          say: 'Open the CRP sample.',
          wait: { role: 'table', name: /^Results for sample/ },
        },
        {
          show: { role: 'region', name: /^Patient Fatima Begum/ },
          say: 'Check the patient first: Fatima Begum, UHID NH26000010.',
          zoom: 1.4,
        },
        {
          show: { role: 'row', name: /^Select C-reactive protein/ },
          say: 'The technician entered 64.0 mg/L. The reference range is up to 5.0, so HC360 flags it High.',
          zoom: 1.3,
        },
        {
          check: { role: 'checkbox', name: /^Select C-reactive protein/ },
          say: 'You are signing the result. Tick the result you have checked.',
        },
        {
          click: { role: 'button', name: /^Verify and release/ },
          say: 'Click Verify and release. The switch Release after verifying is on, so the report goes to the doctor.',
          after: 'The CRP is verified and released. Only the CBC is left in the list.',
          wait: { role: 'heading', name: 'Samples (1)' },
          result: { role: 'heading', name: 'Samples (1)' },
        },
      ],
    },
    {
      title: 'Verify and release the CBC',
      steps: [
        {
          click: { role: 'cell', name: /Complete blood count/ },
          say: 'Open the CBC sample.',
          wait: { role: 'table', name: /^Results for sample/ },
        },
        {
          show: { role: 'row', name: /^Select Total leucocyte count/ },
          say: 'The white cell count is 14.60, above its range of 4.00 to 11.00. HC360 flags it High. Check every flagged value.',
          zoom: 1.3,
        },
        {
          click: { role: 'button', name: 'Select all entered' },
          say: 'The other values are inside their ranges. Click Select all entered, after you have read them.',
        },
        {
          click: { role: 'button', name: /^Verify and release/ },
          say: 'Click Verify and release.',
          after: 'Both of Fatima Begum’s samples are verified and released.',
          wait: { role: 'heading', name: 'Samples (0)' },
          result: { role: 'heading', name: 'Samples (0)' },
        },
      ],
    },
    {
      title: 'Released reports and critical values',
      steps: [
        {
          nav: 'Lab reports',
          say: 'Open Lab reports.',
          after: 'Fatima Begum’s report is now Released. Ravi Kumar’s and Lakshmi Narayanan’s are still In progress.',
          wait: { role: 'row', name: /NHLB260000001 Fatima Begum.*Released/ },
          result: { role: 'row', name: /NHLB260000001 Fatima Begum.*Released/ },
        },
        {
          nav: 'Critical call register',
          say: 'A critical value is dangerous and cannot wait. HC360 asks you to record the call to the doctor before you verify it.',
          after: 'The Critical call register lists who was told, how and when, with the read-back. It is empty this morning.',
          wait: { role: 'table', name: 'Critical call register' },
        },
      ],
    },
  ],
  recap: [
    'Check the patient, and read each result against its reference range, before you verify.',
    'A critical value needs a call to the doctor before it can be verified.',
    'Released reports and the critical call register are in the Laboratory menu.',
  ],
  check: [
    {
      q: 'The CRP is 64.0 mg/L and the reference range is up to 5.0. What does HC360 show?',
      a: 'It flags the result High. You read it, and verify it if it is right.',
    },
    {
      q: 'Where do you find who was told about a critical value, and when?',
      a: 'In the Critical call register.',
    },
  ],
};
