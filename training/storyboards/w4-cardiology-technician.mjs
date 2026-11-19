// Wave 4 (diagnostics): the cardiology technician's day. Starting a 12-lead ECG ordered by the doctor, attaching the
// machine's tracing, and completing the study so that it reaches the cardiologist. Recorded as Balaji Ramasamy at
// 11:10 am, when Vimala Raghavan's ECG, ordered at 11:05 am, is waiting (src/web/demo/story.mjs).

const nav = { role: 'navigation', name: 'Main navigation' };
const dialog = (name) => ({ role: 'dialog', name });

// A small picture of an ECG tracing, as a machine would export it (the file store checks that it is a real image).
const TRACING = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAoAAAADICAIAAAD3O8dcAAAGmElEQVR42u3dW3baShAFUMTKUDT/EdVcOh/ChGAhROvRD/b+uMtx0kbSoe6RZBuGFHEBAE6WIlI2a6211lprrbU2y9UpCACcTwEDgAIGAAUMAChgAFDAAIACBgAFDAAoYABQwACAAgYABQwAChgAUMAAoIABAAUMAAoYAFDAAFClIUU4CgBwthSRsllrrbXWWmuttVncggaAAhQwAChgAFDAAIACBgAFDAAoYABQwADAG38q375hGKYPUkrSkoIUpCAFuknBFTAAKGAAUMAAgAIGAAUMAChgAFDAAEDLBXz/ZS+kIAUHQQp0loIrYAAocTKRIurduHG8XC4p4v6BwKQgBSlIQQqdpJAiUraD19628OGDcx7X2se1L1NwrMyCWXCszMKGtW5BA0ABChgAFDAAKGAAQAEDgAIGABQwAChgAEABA4ACBgAUMAAo4P9N7zl1f8FPpCAFh0IKUugpBVfAAKCAAUABAwAKGAAU8Lmmb7lP335HClKQghToIAVXwABQwJAiKt2ycbxcLvfNe/ojUpCCIyMFKbSdQopI2Y5ce7/JMPPHWre5v7VLKThWZsEsOFZmYcNat6ABoAAFDAAKGAAUMACggAFAAQMA/RWwd/6qIoXpd+ykYBakYBbMgitgAFDAAIACBgAFDAAoYABQwACggB0CAFDAL02/+zX9HhhSkIIUpEDrKbgCBgAFDAAKGAA4yJAiatys6ZVX/9+22U8iBSkgBSk0uUspImU7bO1t2159sspt7m/tmxQcK7NgFsyCWdiw1i1oACigxgL2/mtSQApSoPsUXAEDgAIGAAUMAChgAFDAAIACBgAFDAAoYABQwACAAgYABQwACrhC06uATq8Ier7pcUs9uhSkIAUpSKGzFFwBf3KUx9EzvnwK/r9jFqRgFrqYBQX8yanWz3s+e8YXP+GVglnALLQ+C9UVcIXvOfV4WL/kLcluKfw8saRgFsyCFMzC7im4Al57lJ8OsVNOKUhBClKQwpYUhh3P7IZxTBHTf7d8kYXzzeW/PeRAzz3i9MnazoulIAUpSEEKLaWQIlK2h7XPX/aTtY+Wl++7zW89787c/h7xuNlrZ1LIetzl47xqx/fb34VDPfNXZz03zIJZMAtmYeMs7HwLuqcfCnh1k6H+PZWCFKQgBSnUn8J13226dPdDAQu7U9ueSkEKUpCCFBpK4XrcNjV9svPRxle1p88p/HxbQgolUzALZkEKZuGIAv79MK3fcFi+yVDnnkpBClKQghTaSuF60DZ1cNN/5Z2EGvZUCv/2tNxVjhTMglmQwkd7ej1umxo91hlbW8mezqfQ5isW9ZaCWTALUjAL+xbw2zOs5r7xvv4mQz17+vaZ0V4K91+naygFs2AWzIJZ+HBPd7gCXrNNbZ3sbHl+lNpTKUhBClKQQlsp/Dn6+wQppZVv2ZG9e5WkuH5Pa9625lOIqPaNesyCFMzCN8/C7325bv/qNd8MOfk0p9SeSkEKUpCCFGpOYfYrXDe+fNf6LXj7EmsbT1j2esmx7Tmd//J7ex2iPVM45Xm19cmwa0ZmwSyYBbPwUX/X9W5Ib99z6hbJw23zCt+mqnVrU3gYDCmYBbMgBbPwqVoK2PNVCkhBCnxVCt4PGAAUMAAo4JO55yMFpCAFvieFtq+A/byDFJCCFGg0hWu1hw8pIAUp0HEKw/bfM9tza+4vfPp6q6Z/M/2Dx4+RghSkIAUptJTCob8Uf8Ta22b/+rjmbe5v7fsUHCuzYBYcK7OwyE9BA0ABDRewn3eQAlKQAu2m4AoYABQwAChgAEABA4ACLs3PO0gBKUiBplNwBQwAChgAFHCd3OSRAlKQAh2k4AoYABSwE08pIAUp8B0puAIGAAUMAAoYAFDAAKCAy5le6wQpIAUp0HQKroABoMR5Q4pobIvHcfqguS3v6nkjBSkgBSlslCJSNmuttdZaa621Notb0ABQgAIGAAUMAAoYAFDAAKCAAQAFDAAKGABQwACggAEABQwAChgAFDAAoIABQAEDAAoYABQwAKCAAaBKQ4pwFADgbCkiZbPWWmuttdZaa7O4BQ0ABShgAFDAAKCAAQAFDAAKGABQwACggAEABQwAChgAUMAAoIABQAEDAAoYABQwAKCAAUABAwAKGACqNKQIRwEAzpYiUjZrrbXWWmuttTaLW9AAUIACBgAFDAAKGABQwACggAEABQwAChgAUMAAoIABAAUMAAoYABQwAKCAAUABAwAKGAAUMACggAGgSn8BK4OxjZvGqHQAAAAASUVORK5CYII=', 'base64');

export default {
  id: 'w4-cardiology-technician',
  title: 'Cardiology technician: record the ECG and send it for reporting',
  role: 'CardiologyTechnician',
  roleLabel: 'Cardiology technician',
  user: 'cardiotech',
  wave: 4,
  audience: 'For the technicians who record ECG, echo and treadmill tests at the cardiology unit of Nana Hospitals.',
  intro:
    'Doctors order cardiac tests in HC360, and the orders reach you as a worklist. You record the test, attach what the machine produced, and complete the study. The cardiologist then reports from your tracing.',
  objectives: [
    'Find the studies waiting for you, and check the patient.',
    'Start a recording and attach the tracing from the machine.',
    'Complete the study so that it reaches the cardiologist.',
  ],
  modules: ['cardiology', 'patients'],
  covers: ['/cardiology'],
  scenes: [
    {
      title: 'Your worklist',
      steps: [
        {
          nav: 'Cardiology',
          say: 'Open Cardiology from the menu.',
          after: 'This is today’s list of ECG, echo and stress tests. Vimala Raghavan’s ECG, ordered at 11:05 am, is waiting.',
          wait: { role: 'table', name: 'Cardiology studies' },
        },
        {
          show: { role: 'row', name: /^11:05 am ordered Vimala Raghavan/ },
          say: 'Each row shows the patient, the test and its status. This one is Ordered. Nobody has recorded it yet.',
          zoom: 1.3,
        },
      ],
    },
    {
      title: 'Record the ECG',
      steps: [
        {
          click: { role: 'cell', name: /^Vimala Raghavan/ },
          say: 'Click the patient to open the study.',
          wait: { role: 'region', name: /^Patient Vimala Raghavan/ },
        },
        {
          show: { role: 'region', name: /^Patient Vimala Raghavan/ },
          say: 'Before you connect the electrodes, check that this is the patient on your couch. Ask her name, and check the UHID on her card.',
          zoom: 1.4,
        },
        {
          click: { role: 'button', name: 'Start recording' },
          say: 'When the patient is ready, click Start recording.',
          wait: dialog(/^Start 12-lead ECG/),
        },
        {
          fill: [{ label: 'Machine' }, 'BPL Cardiart 9108'],
          say: 'Type the machine you use. It is printed on the report, so that every tracing can be traced to its machine.',
          zoom: 1.4,
        },
        {
          click: { within: dialog(/^Start 12-lead ECG/), role: 'button', name: 'Start recording' },
          say: 'Click Start recording.',
          after: 'The status is now Recording, with your name as the technician.',
          wait: { role: 'button', name: 'Attach files' },
        },
        {
          click: { role: 'button', name: 'Attach files' },
          say: 'Record the ECG on the machine. Then attach what the machine produced. Click Attach files.',
          wait: dialog(/^Attach the tracing/),
        },
        {
          show: { css: 'input[type=file]' },
          say: 'Choose the files from the machine. HC360 accepts PDF, images, DICOM and SCP-ECG files.',
          after_do: async ({ page }) => {
            await page
              .getByLabel('Device output files')
              .setInputFiles({ name: 'ecg-vimala-raghavan.png', mimeType: 'image/png', buffer: TRACING });
          },
          wait: { text: /ecg-vimala-raghavan\.png/, exact: false },
          zoom: 1.4,
        },
        {
          click: { within: dialog(/^Attach the tracing/), role: 'button', name: 'Attach' },
          say: 'Click Attach.',
          after: 'The tracing is stored with the patient’s record, and appears under Device output.',
          wait: { role: 'button', name: /^ecg-vimala-raghavan\.png/ },
        },
      ],
    },
    {
      title: 'Complete the study',
      steps: [
        {
          click: { role: 'button', name: 'Complete study' },
          say: 'The study cannot be completed without a tracing. Now click Complete study.',
          wait: dialog(/^Complete study/),
        },
        {
          fill: [{ label: 'Technician notes' }, 'Resting ECG recorded. Patient comfortable, no pain during the test.'],
          say: 'Write anything the cardiologist should know about the recording.',
          zoom: 1.4,
        },
        {
          click: { within: dialog(/^Complete study/), role: 'button', name: 'Complete study' },
          say: 'Click Complete study.',
          after: 'The status is now Awaiting report. The study is on the cardiologist’s reporting worklist.',
          wait: { text: 'Awaiting report' },
        },
      ],
    },
  ],
  recap: [
    'Check the patient’s name and UHID before you record.',
    'Start the recording, and type the machine you use.',
    'Attach the machine’s files, then complete the study. The cardiologist reports from your tracing.',
  ],
  check: [
    {
      q: 'Why does HC360 ask for the machine when you start a recording?',
      a: 'It is printed on the report, so that every tracing can be traced to the machine that made it.',
    },
    {
      q: 'You click Complete study, but HC360 asks you to attach the device output first. Why?',
      a: 'The cardiologist reports from the tracing. A study without a tracing cannot be reported.',
    },
  ],
};
