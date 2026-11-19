// Shared video 2 of 3, for every member of staff: the parts of HC360 every role has, whatever its work: My HR (leave,
// attendance, payslips), reporting an incident and following it, internal audits, and the two-person rule behind
// Approvals. Recorded as the staff nurse of the female ward (Deepa Raman), who is on the HR roll.

const nav = { role: 'navigation', name: 'Main navigation' };

export default {
  id: '01-everyday-tools',
  title: 'Tools every member of staff uses',
  role: '*',
  roleLabel: 'All staff',
  user: 'nurse',
  wave: 0,
  audience: 'For every member of staff at Nana Hospitals: leave, incident reports and approvals.',
  intro:
    'Whatever your work at Nana Hospitals, three parts of HC360 are yours: My HR, for your leave and payslips; incident reporting, to make care safer; and approvals, where a second person checks important changes.',
  objectives: [
    'Apply for leave, and see your attendance and payslips in My HR.',
    'Report an incident or a near miss, and follow what happens to it.',
    'Understand why some changes wait for a second person to approve them.',
  ],
  modules: ['hr', 'quality', 'approvals'],
  covers: ['/hr/me', '/quality/report', '/quality/my-reports', '/quality/audits', '/approvals'],
  scenes: [
    {
      title: 'My HR: leave, attendance and payslips',
      steps: [
        {
          nav: 'Human resources',
          say: 'Open Human resources from the menu.',
          after:
            'It opens My HR, your own page: your duty roster, your leave, your attendance and your payslips. If your work includes managing staff, your menu shows more here.',
          wait: { role: 'heading', name: 'My HR' },
        },
        {
          show: { role: 'tablist', name: 'My HR screens' },
          say: 'The tabs at the top show your leave balances, your attendance for the month, and your payslips, which you can download.',
        },
        {
          show: { text: 'Bank account and PAN are shown masked. Ask HR to change them.' },
          say: 'Your bank account and PAN are shown masked, to keep them safe. Only HR can change them.',
          zoom: 1.5,
        },
        {
          click: { role: 'button', name: 'Apply for leave' },
          say: 'To ask for leave, click Apply for leave.',
          after: 'Your reporting manager, or HR, is told of your request and decides it.',
          wait: { role: 'heading', name: 'Leave request' },
        },
        {
          select: [{ label: 'Type of leave' }, 'Casual Leave (CL) · 12 days available'],
          native: true,
          say: 'Choose the type of leave. Each type shows how many days you have left.',
          zoom: 1.4,
        },
        {
          show: { label: 'From' },
          say: 'Choose the first and last day. HC360 counts the days for you, and leaves out holidays and weekly offs.',
          zoom: 1.4,
        },
        {
          fill: [{ label: 'Reason' }, 'Family function at Madurai.'],
          say: 'Give a short reason.',
          zoom: 1.4,
        },
        {
          click: { role: 'button', name: 'Send for approval' },
          say: 'Then click Send for approval.',
          after:
            'Your request is sent. It shows as Pending under My requests, and the days are held from your balance. While it is pending, you can withdraw it.',
          wait: { row: 'Family function at Madurai.' },
        },
      ],
    },
    {
      title: 'Report an incident',
      steps: [
        {
          click: { within: nav, role: 'button', name: 'Quality and safety' },
          say: 'Anything that harmed, or nearly harmed, a patient or a member of staff should be reported. Open Quality and safety.',
        },
        {
          nav: 'Report an incident',
          say: 'Then choose Report an incident.',
          after: 'Reports help the hospital learn and make care safer. They are not used to blame anyone.',
          wait: { role: 'heading', name: 'Report an incident' },
        },
        {
          select: [{ label: 'Kind of incident' }, 'Medication error'],
          native: true,
          say: 'Choose the kind of incident. Here, a medication error that was caught in time.',
          zoom: 1.4,
        },
        {
          fill: [{ label: 'Where' }, 'General Ward Female, medication trolley'],
          say: 'Say where it happened.',
          zoom: 1.4,
        },
        {
          select: [{ label: 'Department' }, 'General Medicine'],
          native: true,
          say: 'And choose the department.',
          zoom: 1.4,
        },
        {
          fill: [
            { label: 'Describe what happened' },
            'At the 10 am round, an antibiotic vial labelled for another patient was on the tray for bed 4. It was noticed at the wristband check and was not given.',
          ],
          say: 'Describe what happened, in the order it happened. Give the facts only. There is no need to name anyone.',
          zoom: 1.3,
        },
        {
          fill: [{ label: 'What was done straight away' }, 'Vial returned to the pharmacy. Ward sister informed.'],
          say: 'Then say what was done straight away.',
          zoom: 1.3,
        },
        {
          show: { role: 'radiogroup', name: 'How serious was it' },
          say: 'Choose how serious it was. This one is a near miss: it was caught before it reached the patient. Your best judgement is enough. The quality team reviews every grade.',
        },
        {
          show: { role: 'switch', name: 'Report anonymously' },
          say: 'You may report anonymously. Your name is then not stored with the report at all. You get a report number to quote, but the quality team cannot contact you, so give enough detail.',
          zoom: 1.4,
        },
        {
          click: { role: 'button', name: 'Send report' },
          say: 'Click Send report.',
          after:
            'Your report is sent, with a report number. Note the number if you want to follow it up. The quality team may contact you for more details.',
          wait: { text: 'Your report has been sent' },
          result: { text: /^NHIR\d+$/ },
        },
        {
          nav: 'My reports and actions',
          say: 'Your reports are under My reports and actions.',
          after: 'Here you can follow each report, and see any action the quality team gives you to do.',
          wait: { role: 'heading', level: 1 },
        },
        {
          nav: 'Internal audits',
          say: 'Internal audits are checks against the NABH standards. If you lead an audit, it is listed here.',
          wait: { role: 'heading', name: 'Internal audits' },
        },
      ],
    },
    {
      title: 'Approvals: the two-person rule',
      steps: [
        {
          click: { within: nav, role: 'button', name: 'Approvals' },
          say: 'Some changes in HC360 are important enough to need a second person. Open the Approvals screen.',
          wait: { role: 'heading', name: 'Approvals' },
        },
        {
          show: { text: 'Changes one person asks for and another approves; nobody approves their own' },
          say: 'One person asks for a change, and another approves it. Nobody approves their own request. A refund, a discount above a limit, and new access to HC360 all work this way.',
          zoom: 1.4,
        },
        {
          show: { role: 'tab', name: 'To decide' },
          say: 'If your role may approve a change, it waits for you under To decide, oldest first. Your own requests are under Asked by me.',
          zoom: 1.5,
        },
      ],
    },
  ],
  recap: [
    'My HR has your roster, leave, attendance and payslips. Leave is sent for approval.',
    'Report every incident and near miss, with facts only. You may report anonymously.',
    'Important changes need a second person. Nobody approves their own request.',
  ],
  check: [
    {
      q: 'A wrong medicine was caught before it reached the patient. Should you report it?',
      a: 'Yes. Report it as a near miss. Near misses show where care can be made safer, before anyone is harmed.',
    },
    {
      q: 'Can you approve a refund that you asked for yourself?',
      a: 'No. Nobody approves their own request. A second person who may approve it decides.',
    },
  ],
  next: 'Find a patient and follow their visit',
};
