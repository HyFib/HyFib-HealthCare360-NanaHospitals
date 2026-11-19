// Wave 5 (in-patient services): the insurance desk's morning. Reading the desk's overview, recording an insurance
// policy for an admitted patient (Fatima Begum, in-patient on the female general ward, self-pay so far), and
// preparing her cashless pre-authorisation. Recorded as Karthikeyan Murugan at 11:10 am, when nothing is waiting at the
// desk (src/web/demo/story.mjs).

const nav = { role: 'navigation', name: 'Main navigation' };
const policy = { role: 'dialog', name: 'Record a policy' };
const preauth = { role: 'dialog', name: 'New pre-authorisation' };

export default {
  id: 'w5-insurance-desk',
  title: 'Insurance desk: record a policy and ask the insurer for cashless approval',
  role: 'InsuranceDesk',
  roleLabel: 'Insurance desk',
  user: 'insurance',
  wave: 5,
  audience: 'For the insurance and TPA desk staff who handle policies, pre-authorisations and claims at Nana Hospitals.',
  intro:
    'When a patient is insured, the hospital asks the insurer to pay. You record the patient’s policy, ask the insurer for cashless approval, and later send the claim. HC360 keeps every step, and every rupee that an insurer owes.',
  objectives: [
    'Read the insurance desk overview: what waits for the insurer, and what the insurers owe.',
    'Record a patient’s policy, and know why a second person verifies it.',
    'Prepare a pre-authorisation for an admitted patient and send it to the insurer.',
  ],
  modules: ['insurance', 'billing'],
  covers: ['/insurance', '/insurance/policies', '/insurance/preauths'],
  scenes: [
    {
      title: 'The insurance desk',
      steps: [
        {
          click: { within: nav, role: 'button', name: 'Insurance' },
          say: 'Open Insurance from the menu.',
        },
        {
          nav: 'Insurance desk',
          say: 'Then choose Insurance desk.',
          after: 'Today no pre-authorisation is waiting, and no claim is pending.',
          wait: { role: 'heading', name: 'Insurance desk', level: 1 },
        },
        {
          show: { role: 'button', name: /^Insurer asked a question/ },
          say: 'These tiles show the work that needs you: questions from insurers, pre-authorisations that wait for the insurer, drafts, and approvals that are only partly given.',
          zoom: 1.2,
        },
        {
          show: { role: 'heading', name: 'Claims pending' },
          say: 'Claims pending shows how much the insurers owe the hospital. Today the figure is ₹0.',
          zoom: 1.3,
        },
      ],
    },
    {
      title: 'Record the patient’s policy',
      steps: [
        {
          nav: 'Patient policies',
          say: 'Fatima Begum is in bed GWF-01 on the female general ward. She pays herself so far, but she has a health policy. Open Patient policies.',
          wait: { role: 'heading', name: 'Patient policies', level: 1 },
        },
        {
          fill: [{ label: 'Patient' }, 'NH26000010'],
          say: 'Find the patient by UHID.',
          wait: { role: 'option', name: /^Fatima Begum/ },
        },
        {
          click: { role: 'option', name: /^Fatima Begum/ },
          say: 'Check the name and the age, then choose her.',
          after: 'No policy is recorded for Fatima Begum yet.',
          wait: { role: 'button', name: 'Record a policy' },
        },
        {
          click: { role: 'button', name: 'Record a policy' },
          say: 'Click Record a policy. Copy the details from her insurance card.',
          wait: policy,
        },
        {
          select: [{ label: 'Insurer or scheme' }, 'ICICI Lombard General Insurance'],
          native: true,
          say: 'Choose the insurer.',
          zoom: 1.3,
        },
        {
          fill: [{ label: 'Policy or card number' }, 'ICL-4410-2291'],
          say: 'Type the policy number from the card. HC360 stores it encrypted, and lists show it masked.',
          zoom: 1.3,
        },
        {
          fill: [{ label: 'Member id' }, 'M-100234'],
          say: 'Type the member id.',
          zoom: 1.3,
        },
        {
          fill: [{ label: 'Policy holder' }, 'Fatima Begum'],
          say: 'Type the name of the policy holder.',
          zoom: 1.3,
        },
        {
          fill: [{ label: 'Relation to holder' }, 'Self'],
          say: 'Type her relation to the holder. She is the holder.',
          zoom: 1.3,
        },
        {
          fill: [{ label: 'Sum insured (₹)' }, '300000'],
          say: 'Type the sum insured, ₹3,00,000.',
          zoom: 1.3,
        },
        {
          fill: [{ label: 'Co-pay (%)' }, '10'],
          say: 'Type the co-pay. The patient pays this share of the bill herself.',
          zoom: 1.3,
        },
        {
          click: { within: policy, role: 'button', name: 'Record policy' },
          say: 'Click Record policy.',
          after: 'The policy is listed with its number masked. Verified says Not yet: someone else verifies it.',
          wait: { text: /Not yet: someone else verifies it/, exact: false },
          result: { role: 'table', name: 'Policies' },
        },
        {
          show: { role: 'table', name: 'Policies' },
          say: 'You recorded this policy, so you cannot verify it. A second person checks it with the insurer. This keeps one person from creating cover that does not exist.',
          zoom: 1.3,
        },
      ],
    },
    {
      title: 'Ask the insurer for cashless approval',
      steps: [
        {
          nav: 'Pre-authorisations',
          say: 'Open Pre-authorisations.',
          wait: { role: 'heading', name: 'Pre-authorisations', level: 1 },
        },
        {
          click: { role: 'button', name: 'New pre-authorisation' },
          say: 'Click New pre-authorisation.',
          wait: preauth,
        },
        {
          fill: [{ within: preauth, label: 'Patient' }, 'NH26000010'],
          say: 'Find Fatima Begum by UHID.',
          wait: { role: 'option', name: /^Fatima Begum/ },
        },
        {
          click: { role: 'option', name: /^Fatima Begum/ },
          say: 'Choose her.',
          wait: { within: preauth, label: 'Policy' },
        },
        {
          select: [{ within: preauth, label: 'Policy' }, 'ICICI Lombard General Insurance · XXXXXXX2291 · to 18 Nov 2027'],
          native: true,
          say: 'Choose the policy you just recorded.',
          zoom: 1.3,
        },
        {
          select: [{ within: preauth, label: 'Admission' }, 'NHIP26000001 · admitted 18 Nov 2026'],
          native: true,
          say: 'Choose her admission. Leave it empty only for a planned admission.',
          zoom: 1.3,
        },
        {
          fill: [{ within: preauth, label: 'Estimated cost' }, '60000'],
          say: 'Type the estimated cost, ₹60,000. It is what you ask the insurer to approve.',
          zoom: 1.3,
        },
        {
          fill: [{ within: preauth, label: 'Expected stay (days)' }, '5'],
          say: 'Type the expected stay in days.',
          zoom: 1.3,
        },
        {
          fill: [{ within: preauth, label: 'Diagnosis (ICD-10)' }, 'J18.9 Pneumonia, unspecified organism'],
          say: 'Type the diagnosis with its ICD-10 code.',
          zoom: 1.3,
        },
        {
          fill: [
            { within: preauth, label: 'Clinical summary' },
            'Fever and cough. Right upper zone consolidation on the chest X-ray. Admitted for IV antibiotics.',
          ],
          say: 'Write why the patient needs admission. The insurer reads this.',
          zoom: 1.3,
        },
        {
          click: { within: preauth, role: 'button', name: 'Save draft' },
          say: 'Click Save draft. Nothing is sent to the insurer yet.',
          after: 'The pre-authorisation is a draft, with its number.',
          wait: { role: 'button', name: 'Submit to insurer' },
        },
        {
          click: { role: 'button', name: 'Submit to insurer' },
          say: 'Check the details. Then click Submit to insurer.',
          wait: { text: /Submitted/, exact: false },
        },
      ],
    },
  ],
  recap: [
    'The overview shows what waits for the insurer, and what the insurers owe the hospital.',
    'Record the policy from the card. A second person verifies it with the insurer, never the person who recorded it.',
    'Save the pre-authorisation as a draft, check it, then submit it to the insurer.',
  ],
  check: [
    {
      q: 'You recorded a patient’s policy. Can you also mark it as verified?',
      a: 'No. Someone else verifies it with the insurer. This keeps one person from creating cover that does not exist.',
    },
    {
      q: 'You save a pre-authorisation as a draft. Does the insurer see it?',
      a: 'No. Nothing is sent until you click Submit to insurer.',
    },
  ],
  next: 'Cashier: bills, payments and shifts',
};
