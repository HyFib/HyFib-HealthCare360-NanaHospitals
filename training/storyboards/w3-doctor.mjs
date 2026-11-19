// Wave 3 (doctors go live after the pharmacy and the front office): an out-patient consultation from the worklist to
// the signed prescription, and reading a released result. Recorded as Dr. Arjun Mehta (General Medicine) at 11:10 am,
// with Vimala Raghavan in consultation and Anand Joseph and Farah Sheikh waiting (src/web/demo/story.mjs).

const nav = { role: 'navigation', name: 'Main navigation' };
const row = (token) => ({ role: 'row', name: new RegExp(`^${token}`) });
const signDialog = { role: 'dialog', name: 'Sign this encounter?' };

export default {
  id: 'w3-doctor',
  title: 'Doctor: see a patient, prescribe and read results',
  role: 'Doctor',
  roleLabel: 'Doctor',
  user: 'doctor',
  desk: true,
  wave: 3,
  audience: 'For the consultants and residents of Nana Hospitals who see patients in the OPD.',
  intro:
    'Your patients reach you from the front desk with a token. In HC360 you write the whole consultation in one note: the history, the diagnosis, the tests and the prescription. The prescription goes to the pharmacy when you sign it.',
  objectives: [
    'Open the next waiting patient, and write the consultation note with a coded diagnosis.',
    'Order a test, and write an e-prescription with HC360’s safety check.',
    'Sign the note, finish the visit, and read a result that has come back.',
  ],
  modules: ['emr'],
  covers: ['/emr'],
  scenes: [
    {
      title: 'Open the next patient',
      steps: [
        {
          click: { within: nav, role: 'button', name: "Doctor's desk" },
          say: 'Open Doctor’s desk from the menu.',
        },
        {
          nav: 'My worklist',
          say: 'Then choose My worklist.',
          after: 'Vimala Raghavan is with you now: her note is open. Anand Joseph and Farah Sheikh are waiting, with their tokens.',
          wait: { role: 'table', name: "Today's queue" },
          result: { role: 'table', name: "Today's queue" },
        },
        {
          click: { within: row('GM-010'), role: 'button', name: 'Call to the room' },
          say: 'Anand Joseph is next. Click Call, to ask him to come in.',
          after: 'He is now in service, and his token shows on the waiting-area screen.',
          wait: { within: row('GM-010'), role: 'button', name: 'Finish' },
          result: row('GM-010'),
        },
        {
          click: { within: row('GM-010'), role: 'button', name: 'Start' },
          say: 'Click Start to open his consultation note.',
          wait: { role: 'heading', name: 'Complaint and history' },
        },
        {
          show: { role: 'region', name: /^Patient Anand Joseph/ },
          say: 'First, check that this is the patient in front of you: the name, and the UHID. Two patients can have the same name. Allergies are not recorded for him, so ask him.',
          zoom: 1.4,
        },
      ],
    },
    {
      title: 'Write the consultation',
      steps: [
        {
          fill: [
            { label: 'History of present illness' },
            'Sore throat and fever since yesterday evening, with pain on swallowing. No cough.',
          ],
          say: 'The complaint is filled in from the front desk. Write the history in his words.',
          zoom: 1.3,
        },
        {
          fill: [{ label: 'Temperature (°F)' }, '100.8'],
          say: 'No vitals are recorded for him yet. Type his temperature.',
          zoom: 1.4,
        },
        {
          fill: [{ label: 'Pulse (per min)' }, '92'],
          say: 'His pulse.',
          zoom: 1.4,
        },
        {
          fill: [{ label: 'SpO₂ (%)' }, '98'],
          say: 'And his oxygen saturation.',
          zoom: 1.4,
        },
        {
          click: { role: 'button', name: 'Record vitals' },
          say: 'Click Record vitals. Values outside the usual range for his age are flagged as you type.',
          wait: { text: 'Vitals recorded' },
        },
        {
          fill: [{ label: 'Examination' }, 'Throat congested, tonsils enlarged without pus. Chest clear.'],
          say: 'Write your examination.',
          zoom: 1.3,
        },
        {
          fill: [{ label: 'Assessment' }, 'Acute pharyngitis.'],
          say: 'And your assessment.',
          zoom: 1.3,
        },
        {
          fill: [{ label: 'Add diagnosis' }, 'pharyngitis'],
          say: 'Add the diagnosis. Type a code, or words.',
          wait: { role: 'option', name: /^J02\.9 Acute pharyngitis/ },
          zoom: 1.3,
        },
        {
          click: { role: 'option', name: /^J02\.9 Acute pharyngitis/ },
          say: 'Choose the ICD-10 code. Medical records need a coded diagnosis. The first primary diagnosis prints on the prescription.',
          after: 'J02.9, acute pharyngitis, is added as the primary diagnosis.',
          wait: { role: 'heading', name: 'Diagnoses (1)' },
          result: { role: 'list', name: 'Diagnoses of this encounter' },
          zoom: 1.3,
        },
      ],
    },
    {
      title: 'Order a test and prescribe',
      steps: [
        {
          fill: [{ label: 'Add test or procedure' }, 'CBC'],
          say: 'Now order a blood count. Type at least two letters of the test.',
          wait: { role: 'option', name: /^Complete blood count/ },
          zoom: 1.3,
        },
        {
          click: { role: 'option', name: /^Complete blood count/ },
          say: 'Choose it. The price is shown.',
          wait: { role: 'button', name: /^Place order/ },
          zoom: 1.3,
        },
        {
          click: { role: 'button', name: /^Place order/ },
          say: 'Click Place order. It goes straight to the laboratory and to billing.',
          after: 'The order has a number, and the blood count is pending.',
          wait: { role: 'region', name: /^Order NHOR/ },
          zoom: 1.3,
        },
        {
          fill: [{ label: 'Medicine 1' }, 'paracetamol'],
          say: 'Now the prescription. Type the medicine.',
          wait: { role: 'option', name: /^Paracetamol 500 mg Tablet/ },
          zoom: 1.3,
        },
        {
          click: { role: 'option', name: /^Paracetamol 500 mg Tablet/ },
          say: 'Choose paracetamol 500 mg. The stock in the pharmacy is shown.',
          wait: { role: 'textbox', name: /dose$/ },
          zoom: 1.3,
        },
        {
          fill: [{ role: 'textbox', name: /dose$/ }, '1 tab'],
          say: 'Type the dose.',
          zoom: 1.3,
        },
        {
          select: [{ role: 'combobox', name: /frequency$/ }, 'TDS – three times a day'],
          native: true,
          say: 'Choose how often: three times a day.',
          zoom: 1.3,
        },
        {
          click: { role: 'button', name: 'Check safety' },
          say: 'Click Check safety. HC360 checks the allergies, the current medicines, interactions, duplicates and the maximum dose.',
          after:
            'No problem was found. If it finds one, signing is blocked until you change the prescription, or give your clinical reason. Every override is recorded.',
          wait: { text: 'Safety check passed' },
          zoom: 1.3,
        },
        {
          click: { role: 'button', name: 'Sign prescription' },
          say: 'Click Sign prescription.',
          after: 'The prescription is signed, and sent to the pharmacy.',
          wait: { text: /^Prescription .* signed$/ },
        },
      ],
    },
    {
      title: 'Finish the visit and read a result',
      steps: [
        {
          click: { role: 'button', name: 'Sign encounter' },
          say: 'Now sign the consultation note.',
          wait: signDialog,
        },
        {
          show: signDialog,
          say: 'HC360 lists what is missing: a diagnosis, an unsigned prescription. Signing locks the note. Later corrections are recorded as amendments, with a reason.',
          zoom: 1.4,
        },
        {
          click: { within: signDialog, role: 'button', name: 'Sign encounter' },
          say: 'Click Sign encounter.',
          wait: { text: 'Signed' },
        },
        {
          nav: 'My worklist',
          say: 'Go back to the worklist.',
          wait: { role: 'table', name: "Today's queue" },
        },
        {
          click: { within: row('GM-010'), role: 'button', name: 'Finish' },
          say: 'Click Finish. The consultation is over, and the queue is free for the next patient.',
          after: 'Anand Joseph’s note is signed and his token is closed.',
          wait: { within: row('GM-010'), role: 'button', name: 'Open note' },
          result: row('GM-010'),
        },
        {
          show: { role: 'heading', name: 'Results to review (3)' },
          say: 'Below the queue are the results that have come back. Critical results are listed first.',
          zoom: 1.3,
        },
        {
          click: { role: 'button', name: /^X-ray chest PA view Karthik Subramanian/ },
          say: 'Open the chest X-ray of Karthik Subramanian.',
          wait: { role: 'dialog', name: /X-ray chest/ },
        },
        {
          click: { within: { role: 'dialog', name: /X-ray chest/ }, role: 'button', name: 'Acknowledge' },
          say: 'Read the report. Then click Acknowledge, to record that you have seen it.',
          after: 'The X-ray leaves the list: two results are still to review.',
          wait: { role: 'heading', name: 'Results to review (2)' },
        },
      ],
    },
  ],
  recap: [
    'Check the name, the UHID and the allergies before you start. Write the history, the vitals, the examination and a coded diagnosis.',
    'Order tests in the note. Run Check safety before you sign a prescription, and give a clinical reason for any override.',
    'Sign the note, click Finish for the token, and acknowledge each result after you read it.',
  ],
  check: [
    {
      q: 'Check safety finds a problem that blocks signing. What can you do?',
      a: 'Change the prescription, or give your clinical reason for prescribing anyway. HC360 records every override and it is reviewed.',
    },
    {
      q: 'Why do you add an ICD-10 code to the diagnosis?',
      a: 'Medical records, reports and billing need a coded diagnosis. The first primary diagnosis also prints on the prescription.',
    },
  ],
  next: 'Nurse: observations, medicines and handover on the ward',
};
