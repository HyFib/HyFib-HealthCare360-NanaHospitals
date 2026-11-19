// Wave 2 (front office and OP billing): the front desk's core work. Catching a returning patient before a second UHID
// is made, registering a new patient, checking her in for a token, and calling the next token. Recorded as the front
// office executive (Anitha Krishnan) at 11:10 am, with two patients waiting for Dr. Arjun Mehta.

const nav = { role: 'navigation', name: 'Main navigation' };
const checkIn = { role: 'dialog', name: /^Check in/ };

export default {
  id: 'w2-front-office',
  title: 'Front office: register a patient and give a token',
  role: 'FrontOffice',
  roleLabel: 'Front office',
  user: 'frontoffice',
  desk: true,
  wave: 2,
  audience: 'For front office executives at the registration and OPD desks of Nana Hospitals.',
  intro:
    'Every patient’s visit to Nana Hospitals starts at the front desk. One patient must have one UHID, for life: that is how doctors see the whole history. This video shows how to make sure of that, and how to give a token.',
  objectives: [
    'Check for an existing record before you register a patient.',
    'Register a new patient and issue the UHID.',
    'Check the patient in for a token, and call the next token from the queue.',
  ],
  modules: ['patients', 'front-office'],
  covers: ['/patients/register', '/front-office/queue'],
  scenes: [
    {
      title: 'Check before you register',
      steps: [
        {
          click: { within: nav, role: 'button', name: 'Patients' },
          say: 'A patient says she has not been here before. Open Patients from the menu.',
        },
        {
          nav: 'Register patient',
          say: 'Then choose Register patient.',
          after: 'A UHID is issued when you save. Before that, HC360 looks for an existing record of the same person.',
          wait: { role: 'heading', name: 'Register patient' },
        },
        {
          fill: [{ label: 'First name' }, 'Lakshmi'],
          say: 'Type the first name.',
          zoom: 1.4,
        },
        {
          fill: [{ label: 'Last name / surname' }, 'Narayanan'],
          say: 'Then the last name.',
          zoom: 1.4,
        },
        {
          fill: [{ label: 'Mobile number' }, '9000001016'],
          say: 'Then the mobile number.',
          after: 'Possible matches shows a patient who is likely the same person.',
          wait: { text: 'Likely same' },
          result: { role: 'heading', name: 'Possible matches' },
          zoom: 1.3,
        },
        {
          show: { text: 'Likely same' },
          say: 'Lakshmi Narayanan already has a UHID. She was seen this morning. Do not register her again: open her existing record, and check her in there.',
          zoom: 1.5,
        },
      ],
    },
    {
      title: 'Register a new patient',
      steps: [
        {
          fill: [{ label: 'First name' }, 'Kalaivani'],
          say: 'This patient is new: Kalaivani Murugesan. Type her first name.',
          zoom: 1.4,
        },
        {
          fill: [{ label: 'Last name / surname' }, 'Murugesan'],
          say: 'Her last name.',
          zoom: 1.4,
        },
        {
          fill: [{ label: 'Mobile number' }, '9000002001'],
          say: 'And her mobile number. It is also how she signs in to the patient portal.',
          after: 'No existing patient matches, so she is new.',
          wait: { text: 'No existing patient matches.' },
          result: { text: 'No existing patient matches.' },
          zoom: 1.3,
        },
        {
          check: { role: 'radio', name: 'Female' },
          say: 'Choose her gender.',
          zoom: 1.4,
        },
        {
          fill: [{ label: 'Date of birth' }, '1988-06-14'],
          say: 'Type her date of birth. If she does not know it, choose Age only, and type her age.',
          zoom: 1.4,
        },
        {
          check: { role: 'switch', name: 'Patient was given the privacy notice' },
          say: 'Give her the privacy notice, which says what the hospital records and why, and mark that she was given it.',
          zoom: 1.4,
        },
        {
          fill: [{ label: 'City / town / village' }, 'Theni'],
          say: 'Type her town.',
          zoom: 1.4,
        },
        {
          fill: [{ label: 'PIN code' }, '625531'],
          say: 'And her PIN code.',
          zoom: 1.4,
        },
        {
          click: { role: 'button', name: 'Register and issue UHID' },
          say: 'Click Register and issue UHID, or press Control S.',
          after:
            'HC360 issues her UHID. Print her UHID card from here: she brings it on every visit. Her mobile number is not verified yet. Verify mobile sends her a code, to make sure the number is hers.',
          wait: { role: 'dialog', name: 'Patient registered' },
          result: { within: { role: 'dialog', name: 'Patient registered' }, text: /^NH\d+$/ },
        },
      ],
    },
    {
      title: 'Check in and give a token',
      steps: [
        {
          click: { within: { role: 'dialog', name: 'Patient registered' }, role: 'button', name: 'Check in now' },
          say: 'She wants to see a doctor today. Click Check in now.',
          wait: checkIn,
        },
        {
          select: [{ within: checkIn, label: 'Department' }, 'General Medicine'],
          native: true,
          say: 'Choose the department.',
          zoom: 1.3,
        },
        {
          select: [{ within: checkIn, label: 'Doctor' }, 'Arjun Mehta'],
          native: true,
          say: 'Then the doctor.',
          zoom: 1.3,
        },
        {
          show: { within: checkIn, role: 'radiogroup', name: 'Queue priority' },
          say: 'Senior citizens, pregnant women and differently-abled patients get priority in the queue. Kalaivani is none of these, so leave it at Normal.',
          zoom: 1.4,
        },
        {
          fill: [{ within: checkIn, label: 'Reason for visit' }, 'Headache and giddiness for two days'],
          say: 'Write the reason for her visit, in her words.',
          zoom: 1.3,
        },
        {
          click: { within: checkIn, role: 'button', name: 'Check in and issue token' },
          say: 'Click Check in and issue token.',
          after: 'Her token is GM-012, for Dr. Arjun Mehta. It is also sent to her phone. Print the token slip for her.',
          wait: { role: 'dialog', name: 'Checked in' },
          result: { within: { role: 'dialog', name: 'Checked in' }, text: /^GM-\d+$/ },
        },
        {
          click: { within: { role: 'dialog', name: 'Checked in' }, role: 'button', name: 'Done' },
          say: 'Then click Done.',
        },
      ],
    },
    {
      title: 'Call the next token',
      steps: [
        {
          click: { within: nav, role: 'button', name: 'Front office' },
          say: 'Now open Front office.',
        },
        {
          nav: 'Queue',
          say: 'Then the queue.',
          after: 'Kalaivani is now waiting, after Anand Joseph and Farah Sheikh.',
          wait: { role: 'cell', name: /Kalaivani Murugesan/ },
          result: { role: 'row', name: /Kalaivani Murugesan/ },
        },
        {
          click: { role: 'button', name: /^Call next patient/ },
          say: 'When the doctor is ready for the next patient, click Call next patient, or press F2.',
          after:
            'Anand Joseph’s token, GM-010, is called, and he leaves the waiting list. The waiting-area screen shows his token, so he knows to go in.',
          wait: { role: 'button', name: 'Call next patient (2 waiting)' },
          result: { role: 'heading', name: /^Waiting/ },
        },
      ],
    },
  ],
  recap: [
    'Before you register, look at Possible matches. One patient has one UHID, for life.',
    'Mark the privacy notice. The mobile number also signs the patient in to the portal.',
    'Check in issues the token. Give priority to senior citizens, pregnant women and the differently-abled.',
  ],
  check: [
    {
      q: 'Possible matches shows Likely same for the patient you are registering. What do you do?',
      a: 'Open that record and check with the patient. If it is the same person, check them in on the existing record. Never make a second UHID.',
    },
    {
      q: 'A 72-year-old man is checking in. Which queue priority do you choose?',
      a: 'Priority. Senior citizens, pregnant women and differently-abled patients are given priority in the queue.',
    },
  ],
};
