// Wave 4 (diagnostics): the cardiologist's reporting. Reading the ECG the technician recorded, entering the
// measurements and the conclusion, and signing and releasing the report. Recorded as Dr. Ramesh Venkatesan at
// 11:10 am. Setup does the technician's part first (start, attach the tracing, complete) for Vimala Raghavan's ECG,
// ordered at 11:05 am (src/web/demo/story.mjs).

const nav = { role: 'navigation', name: 'Main navigation' };

const TRACING = Buffer.from('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', 'base64');

export default {
  id: 'w4-cardiologist',
  title: 'Cardiologist: report an ECG and sign it',
  role: 'Cardiologist',
  roleLabel: 'Cardiologist',
  user: 'cardiologist',
  wave: 4,
  audience: 'For the cardiologists who report ECG, echo and stress tests at Nana Hospitals.',
  intro:
    'When the technician completes a cardiac study, it reaches your reporting worklist. You read the tracing, enter the measurements and your conclusion, and sign. The report then goes to the treating doctor and the patient’s record.',
  objectives: [
    'Open the cardiology reporting worklist and choose a study.',
    'Read the tracing and write the ECG report.',
    'Sign the report and release it to the treating doctor.',
  ],
  modules: ['cardiology', 'patients'],
  covers: ['/cardiology', '/cardiology/reporting'],
  setup: async ({ api }) => {
    // What the technician did earlier this morning: record the ECG, attach the tracing, complete the study.
    const tech = await api('cardiotech');
    const studies = await tech.get('/api/v1/cardiology/studies?pageSize=50');
    const study = studies.items.find((s) => s.status === 'Ordered' && /Vimala/.test(s.patientName));
    await tech.post(`/api/v1/cardiology/studies/${study.id}/start`, { machine: 'BPL Cardiart 9108' });
    const form = new FormData();
    form.append('file', new Blob([TRACING], { type: 'image/png' }), 'ecg-vimala-raghavan.png');
    form.append('category', 'cardiology-device');
    form.append('patientId', study.patientId);
    const response = await fetch('https://localhost:7360/api/v1/files', {
      method: 'POST',
      headers: { Authorization: `Bearer ${tech.token}`, 'X-HC360-Client': 'web', 'Idempotency-Key': crypto.randomUUID() },
      body: form,
    });
    const file = await response.json();
    await tech.post(`/api/v1/cardiology/studies/${study.id}/files`, { fileIds: [file.id] });
    await tech.post(`/api/v1/cardiology/studies/${study.id}/complete`, { notes: 'Resting ECG recorded. Patient comfortable.' });
  },
  scenes: [
    {
      title: 'Your reporting worklist',
      steps: [
        {
          click: { within: nav, role: 'button', name: 'Cardiology' },
          say: 'Open Cardiology from the menu.',
        },
        {
          nav: 'Cardiology reporting',
          say: 'Then choose Cardiology reporting.',
          after: 'These are the studies the technicians have completed. STAT studies come first, then the study that has waited longest.',
          wait: { role: 'table', name: 'Cardiology studies waiting for a report' },
        },
        {
          show: { role: 'row', name: /^Vimala Raghavan/ },
          say: 'Vimala Raghavan’s ECG is waiting. The report column says Not started.',
          zoom: 1.3,
        },
      ],
    },
    {
      title: 'Read the study and write the report',
      steps: [
        {
          click: { role: 'cell', name: /^Vimala Raghavan/ },
          say: 'Click the study to open it.',
          wait: { role: 'region', name: /^Patient Vimala Raghavan/ },
        },
        {
          show: { role: 'region', name: /^Patient Vimala Raghavan/ },
          say: 'Check the patient first: the name and the UHID. A report on the wrong patient can lead to the wrong treatment.',
          zoom: 1.4,
        },
        {
          show: { role: 'heading', name: 'Study' },
          say: 'The study shows why the doctor asked for it: exertional chest tightness for two weeks. It also shows the technician’s notes.',
          zoom: 1.2,
        },
        {
          click: { role: 'button', name: /^ecg-vimala-raghavan\.png/ },
          say: 'The tracing is under Device output. Click it to look at it.',
          wait: { role: 'dialog' },
        },
        {
          press: 'Escape',
          say: 'Close the tracing.',
        },
        {
          fill: [{ role: 'spinbutton', name: 'Heart rate (/min)' }, '78'],
          say: 'Enter the measurements from the tracing. The heart rate is 78 a minute.',
          zoom: 1.4,
        },
        {
          fill: [{ role: 'spinbutton', name: 'PR interval (ms)' }, '164'],
          say: 'The PR interval is 164 milliseconds.',
          zoom: 1.4,
        },
        {
          fill: [{ role: 'spinbutton', name: 'QRS duration (ms)' }, '90'],
          say: 'The QRS duration is 90 milliseconds.',
          zoom: 1.4,
        },
        {
          fill: [{ role: 'spinbutton', name: 'QT interval (ms)' }, '380'],
          say: 'The QT interval is 380 milliseconds.',
          zoom: 1.4,
        },
        {
          click: { role: 'button', name: /^Bazett/ },
          say: 'HC360 calculates the corrected QT from the QT interval and the heart rate. Click Bazett to fill it in.',
          wait: { label: 'QTc (ms)' },
          zoom: 1.4,
        },
        {
          click: { role: 'button', name: 'Sinus rhythm' },
          say: 'Choose the rhythm with one click.',
          zoom: 1.4,
        },
        {
          click: { role: 'button', name: 'Normal' },
          say: 'Choose the axis the same way.',
          zoom: 1.4,
        },
        {
          fill: [{ role: 'textbox', name: 'ST-T changes' }, 'No ST segment or T wave changes.'],
          say: 'Describe any ST and T wave changes.',
          zoom: 1.4,
        },
        {
          fill: [{ role: 'textbox', name: 'Conclusion' }, 'Normal resting 12-lead ECG. Sinus rhythm at 78 a minute.'],
          say: 'Write your conclusion.',
          zoom: 1.4,
        },
        {
          fill: [
            { role: 'textbox', name: 'Recommendations' },
            'A normal resting ECG does not rule out exertional angina. Proceed with the treadmill test as the doctor plans.',
          ],
          say: 'Write your recommendation for the treating doctor.',
          zoom: 1.4,
        },
      ],
    },
    {
      title: 'Sign and release',
      steps: [
        {
          show: { role: 'switch', name: /^Critical finding/ },
          say: 'If the tracing shows a critical finding, turn on this switch. HC360 then makes you record the call to the treating doctor before you can sign. Here, there is none.',
          zoom: 1.4,
        },
        {
          click: { role: 'button', name: 'Sign and release' },
          say: 'Click Sign and release. Release now is on, so the treating doctor sees the report at once.',
          after: 'The report is signed with your name, and released.',
          wait: { text: 'Released' },
        },
      ],
    },
  ],
  recap: [
    'The reporting worklist lists completed studies. STAT studies come first.',
    'Check the patient, look at the tracing, and enter the measurements and your conclusion.',
    'A critical finding needs a call to the treating doctor before you sign. Sign and release sends the report on.',
  ],
  check: [
    {
      q: 'Which studies appear on the Cardiology reporting worklist?',
      a: 'The studies that a technician has completed, with the tracing attached. STAT studies come first.',
    },
    {
      q: 'The tracing shows a critical finding. What must you do before you sign?',
      a: 'Turn on the critical finding switch and record the call to the treating doctor. HC360 asks for it before you can sign.',
    },
  ],
};
