// Wave 5 (in-patient care goes live): the ward nurse's morning at the nursing station of the female general ward.
// Recording observations (NEWS2) for Fatima Begum, giving her medicines from the medication administration record, and
// the SBAR handover. Recorded as the staff nurse Deepa Raman at 11:10 am. The pharmacy has issued Fatima's medicines to
// the ward earlier in the morning (setup), so that her chart has doses to give.

const nav = { role: 'navigation', name: 'Main navigation' };
const dose = { role: 'dialog', name: /^Ceftriaxone/ };

export default {
  id: 'w5-nurse',
  title: 'Nurse: observations, medicines and handover on the ward',
  role: 'Nurse',
  roleLabel: 'Nurse',
  user: 'nurse',
  desk: true,
  wave: 5,
  audience: 'For the staff nurses of the wards of Nana Hospitals.',
  intro:
    'The nursing station shows your ward, bed by bed. Observations, medicines and handovers are all recorded on the patient’s chart, with your name and the time. This video follows one patient: Fatima Begum, in bed GWF-01.',
  objectives: [
    'Record a patient’s observations, and see her NEWS2 score.',
    'Give a medicine from the medication administration record.',
    'Hand over the patient to the next shift.',
  ],
  modules: ['inpatient'],
  covers: ['/nursing'],
  setup: async ({ api }) => {
    // The pharmacy issued Fatima Begum's in-patient prescription to the ward earlier: the doses appear on her chart.
    const pharmacist = await api('pharmacist');
    const store = (await pharmacist.get('/api/v1/inventory/stores?type=Pharmacy')).find((s) => s.code === 'IPPH');
    const queue = await pharmacist.get('/api/v1/pharmacy/queue?inpatient=true');
    const entry = await pharmacist.get(`/api/v1/pharmacy/queue/${queue.items.find((i) => i.isInpatient).id}?storeId=${store.id}`);
    await pharmacist.post('/api/v1/pharmacy/ip-issues', {
      storeId: store.id,
      wardIndentId: null,
      queueEntryId: entry.id,
      patientId: null,
      admissionId: null,
      visitId: null,
      lines: entry.lines.map((l) => ({
        itemId: l.drugItemId,
        quantity: Math.min(l.requestedQuantity, l.available),
        batchId: null,
        wardIndentLineId: null,
        queueLineId: l.id,
        substitutionReason: null,
        prescriberConsent: false,
      })),
      confirmControlledDrugs: true,
    });
  },
  scenes: [
    {
      title: 'Your ward',
      steps: [
        {
          click: { within: nav, role: 'button', name: 'In-patients' },
          say: 'Open In-patients from the menu.',
        },
        {
          nav: 'Nursing station',
          say: 'Then choose Nursing station.',
          after: 'The station opens on your ward, the female general ward, and lists its patients by bed.',
          wait: { role: 'button', name: /^Bed GWF-01, Fatima Begum/ },
          result: { role: 'navigation', name: 'Patients on the ward' },
        },
        {
          show: { role: 'heading', name: 'NEWS2 escalations waiting for a response' },
          say: 'Fatima Begum’s early warning score was high this morning, and an escalation is waiting for a response. A doctor, or a nurse who did not take the observations, answers it.',
          zoom: 1.3,
        },
        {
          click: { role: 'button', name: /^Bed GWF-01, Fatima Begum/ },
          say: 'Click her bed to open her chart.',
          wait: { role: 'region', name: /^Patient Fatima Begum/ },
        },
        {
          show: { role: 'region', name: /^Patient Fatima Begum/ },
          say: 'Check the name and the UHID on the banner against her wristband. Her allergies are not recorded: ask her, before you give any medicine.',
          zoom: 1.4,
        },
      ],
    },
    {
      title: 'Record observations',
      steps: [
        {
          click: { role: 'tab', name: 'Observations (NEWS2)' },
          say: 'Open the Observations tab.',
          wait: { role: 'form', name: 'Record observations' },
          zoom: 1.2,
        },
        {
          fill: [{ label: 'Respiration' }, '22'],
          say: 'Type her respiration rate.',
          zoom: 1.4,
        },
        {
          fill: [{ label: 'SpO₂' }, '95'],
          say: 'Her oxygen saturation.',
          zoom: 1.4,
        },
        {
          fill: [{ label: 'Systolic BP' }, '112'],
          say: 'Her blood pressure: first the systolic.',
          zoom: 1.4,
        },
        {
          fill: [{ label: 'Diastolic BP' }, '70'],
          say: 'Then the diastolic.',
          zoom: 1.4,
        },
        {
          fill: [{ label: 'Pulse' }, '98'],
          say: 'Her pulse.',
          zoom: 1.4,
        },
        {
          fill: [{ label: 'Temperature' }, '37.8'],
          say: 'And her temperature, in degrees Celsius.',
          zoom: 1.4,
        },
        {
          select: [{ label: 'Consciousness (ACVPU)' }, 'A — Alert'],
          native: true,
          say: 'Choose her level of consciousness. She is alert. She breathes air, so leave the oxygen switch off.',
          zoom: 1.4,
        },
        {
          click: { role: 'button', name: 'Save observations' },
          say: 'Click Save observations. HC360 scores the set. It raises an escalation when the total is 5 or more, or when any one parameter scores 3.',
          wait: { role: 'table', name: 'Observations with NEWS2' },
        },
        {
          show: { role: 'table', name: 'Observations with NEWS2' },
          say: 'The new entry scores NEWS2 4.',
          zoom: 1.2,
        },
      ],
    },
    {
      title: 'Give a medicine',
      steps: [
        {
          click: { role: 'tab', name: 'MAR' },
          say: 'Now open the MAR, the medication administration record.',
          after: 'It lists the doses charted for today: the time, the medicine, the dose and the route.',
          wait: { role: 'table', name: 'Medication administration record' },
          result: { role: 'table', name: 'Medication administration record' },
        },
        {
          click: { within: { role: 'row', name: /Ceftriaxone/ }, role: 'button', name: 'Given' },
          say: 'The ceftriaxone dose is charted for 9:00 pm, so it is not due yet. We open its window only to show the checks. Never give a dose before its time.',
          wait: dose,
        },
        {
          show: { role: 'heading', name: /^Ceftriaxone 1 g Injection/ },
          say: 'For a real dose, check the five rights: the right patient, medicine, dose, route and time. The window shows them all. Read them against the vial and her wristband. The batch number comes from the pharmacy’s issue.',
          zoom: 1.3,
        },
        {
          show: { text: 'Witness (a second nurse, at this screen)' },
          say: 'Ceftriaxone is a Schedule H1 medicine. HC360 treats Schedule H1, Schedule X and NDPS drugs, and high-alert medicines, as needing a second nurse to witness the dose. The witness types their own user name and password here. Record given stays locked until the witness confirms.',
          zoom: 1.3,
        },
      ],
    },
    {
      title: 'Hand over the patient',
      steps: [
        {
          press: 'Escape',
          say: 'Close this window.',
        },
        {
          click: { role: 'tab', name: 'Handover' },
          say: 'At the end of the shift, open the Handover tab.',
          wait: { label: 'S — Situation' },
        },
        {
          fill: [{ label: 'S — Situation' }, 'Fever and cough, day 1 of IV antibiotics. NEWS2 was high at 6:00 am; repeat observations done.'],
          say: 'Write the situation.',
          zoom: 1.3,
        },
        {
          fill: [{ label: 'B — Background' }, 'Pneumonia. Admitted from casualty yesterday evening.'],
          say: 'The background.',
          zoom: 1.3,
        },
        {
          fill: [{ label: 'A — Assessment' }, 'SpO₂ 95%, respiration 22 a minute. Allergies not yet recorded.'],
          say: 'Your assessment.',
          zoom: 1.3,
        },
        {
          fill: [{ label: 'R — Recommendation' }, 'Repeat observations every four hours. Follow the doctor’s response to the escalation.'],
          say: 'And what the next shift should do.',
          zoom: 1.3,
        },
        {
          click: { role: 'button', name: /^Give handover/ },
          say: 'Click Give handover.',
          after: 'The handover is saved on her chart. The receiving nurse acknowledges it, and takes over responsibility for her.',
          wait: { text: /day 1 of IV antibiotics/ },
        },
      ],
    },
  ],
  recap: [
    'Check the name, the UHID and the allergies on the banner before you record or give anything.',
    'Save a full set of observations to see the NEWS2 score. A total of 5 or more, or 3 in any one parameter, raises an escalation.',
    'Check the five rights before you give a dose, and never before its time. Hand over each patient at the end of the shift.',
  ],
  check: [
    {
      q: 'What do you check before you click Given on the MAR?',
      a: 'The five rights: the right patient, medicine, dose, route and time. HC360 shows them in the window, and your name is recorded with the dose.',
    },
    {
      q: 'When does HC360 raise a NEWS2 escalation?',
      a: 'When the total is 5 or more, or when any one parameter scores 3, even if the total is lower. The escalation goes to the ward’s doctor and the nurses in charge, and stays on the station until someone responds.',
    },
  ],
};
