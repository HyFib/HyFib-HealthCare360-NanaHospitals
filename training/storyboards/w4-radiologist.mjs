// Wave 4 (diagnostics): the radiologist's reporting. Opening Fatima Begum's portable chest X-ray from the reporting
// worklist, writing the report, and signing it off for release, with what HC360 asks for a critical finding.
// Recorded as the radiologist (Dr. Vikram Shetty) at 11:10 am (src/web/demo/story.mjs). The findings written here are
// fictional: the training server holds no image.

const nav = { role: 'navigation', name: 'Main navigation' };

export default {
  id: 'w4-radiologist',
  title: 'Radiologist: report and sign off imaging studies',
  role: 'Radiologist',
  roleLabel: 'Radiologist',
  user: 'radiologist',
  wave: 4,
  audience: 'For the radiologists of the Nana Hospitals imaging department.',
  intro:
    'When the radiographer completes a scan, the study waits for you on the reporting worklist. You write the report, sign it, and HC360 releases it to the doctor who ordered it.',
  objectives: [
    'Find the studies that wait for a report.',
    'Write a report and sign it off for release.',
    'Know what HC360 asks for a critical finding.',
  ],
  modules: ['radiology'],
  covers: ['/radiology', '/radiology/reporting'],
  scenes: [
    {
      title: 'Studies waiting for your report',
      steps: [
        {
          click: { within: nav, role: 'button', name: 'Radiology' },
          say: 'Open Radiology from the menu.',
        },
        {
          nav: 'Reporting worklist',
          say: 'Open the Reporting worklist.',
          after: 'One study waits: Fatima Begum’s chest X-ray, taken on the ward. Its report is Not started.',
          wait: { role: 'table', name: 'Studies waiting for a report' },
        },
      ],
    },
    {
      title: 'Write the report',
      steps: [
        {
          click: { role: 'cell', name: /^Fatima Begum/ },
          say: 'Click the study to open it.',
          wait: { role: 'textbox', name: 'Findings' },
        },
        {
          show: { role: 'region', name: /^Patient Fatima Begum/ },
          say: 'Check the patient first: Fatima Begum, UHID NH26000010. A report on the wrong patient is a danger.',
          zoom: 1.4,
        },
        {
          show: { role: 'heading', name: 'Images' },
          say: 'The images come from PACS. Open them in the PACS viewer to read them. HC360 links a study with its images a few minutes after they reach PACS.',
          zoom: 1.2,
        },
        {
          fill: [{ label: 'Technique' }, 'Chest radiograph, AP view, portable.'],
          say: 'Write the technique.',
          zoom: 1.3,
        },
        {
          fill: [{ label: 'Comparison' }, 'None available.'],
          say: 'Write the comparison.',
          zoom: 1.3,
        },
        {
          fill: [
            { label: 'Findings' },
            'Patchy air-space opacity in the right lower zone with air bronchograms. No pleural effusion. Heart size is normal.',
          ],
          say: 'Write what you see in the findings.',
          zoom: 1.3,
        },
        {
          fill: [{ label: 'Impression' }, 'Right lower zone pneumonia, in keeping with the clinical diagnosis.'],
          say: 'Then the impression: your conclusion, in one line, for the doctor who reads only that.',
          zoom: 1.3,
        },
        {
          show: { role: 'switch', name: /^Critical finding/ },
          say: 'Switch on Critical finding only if the doctor must know at once. HC360 then asks you to call the treating doctor before you sign off. Here, it stays off.',
          zoom: 1.3,
        },
        {
          click: { role: 'button', name: 'Sign and release' },
          say: 'Release now is on. Click Sign and release. Your name and the time are written on the report.',
          after: 'The report is signed by Dr. Vikram Shetty and released. A signed report is not edited: to correct it, add an addendum.',
          wait: { text: /signed by Dr\. Vikram Shetty/ },
          result: { role: 'button', name: 'Addendum' },
        },
      ],
    },
    {
      title: 'The report reaches the doctor',
      steps: [
        {
          nav: 'Imaging worklist',
          say: 'Open the Imaging worklist.',
          after: 'Fatima Begum’s X-ray is now Released, like Karthik Subramanian’s chest X-ray before it.',
          wait: { role: 'row', name: /Fatima Begum.*Released/ },
          result: { role: 'row', name: /Fatima Begum.*Released/ },
        },
      ],
    },
  ],
  recap: [
    'Check the patient, and the images, before you write.',
    'Sign and release puts the report in the doctor’s hands.',
    'A critical finding needs a call to the treating doctor before sign-off.',
  ],
  check: [
    {
      q: 'You find something that cannot wait. What do you switch on in the report?',
      a: 'Critical finding. HC360 then asks you to call the treating doctor before you sign off.',
    },
    {
      q: 'What does the switch Release now do?',
      a: 'When it is on, signing the report also releases it to the doctor.',
    },
  ],
  next: 'Shared video: find a patient and follow their visit',
};
