// Wave 7 (insights): the patient portal, as a patient uses it. Rajendran Pillai signs in with his mobile number and the
// one-time code the hospital sends, confirms his details once, reads today's HbA1c report, opens his prescription with its
// QR code, and reads his bill. The capture starts on the portal's sign-in page; the code is read from the SMS the server
// queued (the training server sends no real SMS).

const main = { role: 'navigation', name: 'Main' };

export default {
  id: 'w7-patient-portal',
  title: 'Patient portal: sign in, reports, prescriptions and bills',
  role: 'Patient',
  roleLabel: 'Patient',
  user: 'patient',
  portal: true,
  wave: 7,
  audience: 'For patients and their families, and for the staff who help them at the front desk.',
  intro:
    'Patients of Nana Hospitals can see their reports, prescriptions and bills on their own phone. No password is needed. This video shows how patients sign in, and what they find.',
  objectives: [
    'Sign in to the portal with a mobile number and a one-time code.',
    'Read a lab report, and open a prescription with its QR code.',
    'See a bill and its receipt.',
  ],
  modules: [],
  covers: ['/portal/sign-in', '/portal/reports', '/portal/prescriptions', '/portal/bills'],
  scenes: [
    {
      title: 'Sign in',
      steps: [
        {
          show: { role: 'heading', name: 'Welcome to Nana Hospitals' },
          say: 'This is the patient portal. Rajendran Pillai opens it on his phone. There is no password to remember.',
          zoom: 1.2,
        },
        {
          fill: [{ label: 'Mobile number' }, '9000001021'],
          say: 'He types the mobile number he gave the hospital when he registered.',
          zoom: 1.4,
        },
        {
          before: ({ sentCodes, state }) => {
            state.earlier = new Set(sentCodes('auth.otp', '+919000001021').map(([id]) => id));
          },
          click: { role: 'button', name: 'Send code' },
          say: 'Then he clicks Send code.',
          after: 'The hospital sends a 6-digit code by SMS to his phone. The code expires in five minutes.',
          wait: { role: 'heading', name: 'Enter the code' },
        },
        {
          fill: [
            { label: '6-digit code' },
            async ({ sentCodes, state, page }) => {
              for (let i = 0; i < 40; i++) {
                const code = sentCodes('auth.otp', '+919000001021').find(([id]) => !state.earlier.has(id))?.[1];
                if (code) return code;
                await page.waitForTimeout(250);
              }
              throw new Error('The server queued no sign-in code.');
            },
          ],
          say: 'He types the code from the SMS.',
          zoom: 1.4,
          wait: { role: 'heading', name: 'Confirm your details' },
        },
        {
          show: { role: 'heading', name: 'Confirm your details' },
          say: 'The first time, the portal asks him to confirm who he is, to keep records private. It asks for the date of birth, or the UHID on the hospital card.',
          zoom: 1.2,
        },
        {
          fill: [{ label: 'Day' }, '02'],
          say: 'He types the day of his birth.',
          zoom: 1.3,
        },
        {
          fill: [{ label: 'Month' }, '08'],
          say: 'The month.',
          zoom: 1.3,
        },
        {
          fill: [{ label: 'Year' }, '1979'],
          say: 'And the year.',
          zoom: 1.3,
        },
        {
          click: { role: 'button', name: 'Confirm' },
          say: 'Then he clicks Confirm.',
          after: 'The home page opens. It shows his recent reports and prescriptions.',
          wait: { role: 'heading', name: /^Good morning, Rajendran/ },
        },
      ],
    },
    {
      title: 'Today’s report',
      steps: [
        {
          click: { within: main, role: 'link', name: 'Reports' },
          say: 'To see his tests, he opens Reports.',
          wait: { role: 'heading', name: 'Reports' },
        },
        {
          click: { role: 'link', name: /^Glycated haemoglobin/ },
          say: 'The report of this morning is released. He opens it.',
          wait: { role: 'heading', name: /Glycated haemoglobin/, level: 1 },
        },
        {
          show: { css: 'li', has: /8\.4/ },
          say: 'His HbA1c is 8.4 %. The portal marks it Above normal, and shows the normal range under it.',
          zoom: 1.4,
        },
        {
          show: { role: 'heading', name: 'Checked and signed by' },
          say: 'The report names the pathologist who checked it. The portal tells him that his doctor will explain what the results mean for him.',
          zoom: 1.2,
        },
      ],
    },
    {
      title: 'The prescription and its QR code',
      steps: [
        {
          click: { within: main, role: 'link', name: 'Prescriptions' },
          say: 'Next he opens Prescriptions.',
          wait: { role: 'heading', name: 'Prescriptions' },
        },
        {
          click: { role: 'link', name: /^Metformin/ },
          say: 'Dr. Arjun Mehta’s prescription is here. He opens it.',
          wait: { role: 'heading', name: 'Medicines' },
        },
        {
          show: { role: 'heading', name: 'Medicines' },
          say: 'Metformin 500 mg, one tablet twice a day, for 30 days, with breakfast and dinner.',
          zoom: 1.2,
        },
        {
          show: { role: 'img', name: 'QR code to check this prescription' },
          say: 'At the pharmacy, he shows this QR code. The pharmacist scans it to check that the prescription is genuine.',
          zoom: 1.4,
        },
      ],
    },
    {
      title: 'The bill',
      steps: [
        {
          click: { within: main, role: 'link', name: 'Bills' },
          say: 'Finally, he opens Bills.',
          after: 'The portal says there is nothing to pay. His bill is paid.',
          wait: { text: /Nothing to pay/ },
        },
        {
          click: { role: 'link', name: /^OPD bill/ },
          say: 'He opens the bill.',
          wait: { role: 'heading', name: 'Summary' },
        },
        {
          show: { role: 'heading', name: 'Summary' },
          say: 'The summary shows the bill amount, what he paid, and the amount due: ₹0.',
          zoom: 1.2,
        },
        {
          show: { role: 'heading', name: 'Payments and receipts' },
          say: 'The receipt number is here. He can download the bill or print the receipt.',
          zoom: 1.2,
        },
        {
          click: { role: 'button', name: 'Sign out' },
          say: 'When he is done, he clicks Sign out, so that nobody else can open his records on that phone.',
          wait: { role: 'heading', name: 'Welcome to Nana Hospitals' },
        },
      ],
    },
  ],
  recap: [
    'Sign in with the mobile number the hospital has, and the one-time code sent by SMS.',
    'Reports, prescriptions and bills are in the menu. The QR code on a prescription proves it is genuine.',
    'Sign out when you are done, especially on a phone someone else may use.',
  ],
  check: [
    {
      q: 'A patient says the portal does not send a code. What do you check first?',
      a: 'That the mobile number on his record is the number he types. The portal sends the code only to the number the hospital registered.',
    },
    {
      q: 'What does the pharmacist do with the QR code on a prescription?',
      a: 'Scans it, to check that the prescription is genuine.',
    },
  ],
  // Its viewers are patients: no training server, no next video.
  outro: 'Well done. Now you can see your reports, prescriptions and bills on your own phone. If you need help with the portal, ask at the front desk of Nana Hospitals.',
  endLines: [['Help', 'Ask at the front desk of Nana Hospitals.']],
};
