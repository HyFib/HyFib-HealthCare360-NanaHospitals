// Wave 6 (back office): the CRM executive's day. Capturing an enquiry as a lead, adding a follow-up call for a
// patient, and drafting a campaign for patients who agreed to hear from the hospital. Recorded as the CRM executive
// (Sathish Kumar) at 11:10 am, when the leads, calls and campaigns lists are still empty.

const nav = { role: 'navigation', name: 'Main navigation' };

export default {
  id: 'w6-crm-executive',
  title: 'CRM executive: leads, follow-up calls and campaigns',
  role: 'CrmExecutive',
  roleLabel: 'CRM executive',
  user: 'crm',
  wave: 6,
  audience: 'For the CRM executive of Nana Hospitals, who handles enquiries, follow-up calls and campaigns.',
  intro:
    'The CRM executive keeps patients connected to the hospital: enquiries become leads, patients get a call after their visit, and patients who agreed to hear from us receive messages about camps and check-ups.',
  objectives: [
    'Capture an enquiry as a lead.',
    'Add a follow-up call for a patient.',
    'Draft an SMS campaign, and know why a second person approves it.',
  ],
  modules: ['crm'],
  covers: ['/crm', '/crm/leads', '/crm/follow-ups', '/crm/campaigns'],
  scenes: [
    {
      title: 'Capture a lead',
      steps: [
        {
          click: { within: nav, role: 'button', name: 'Patient relations' },
          say: 'Open Patient relations from the menu.',
        },
        {
          nav: 'Leads',
          say: 'Open Leads.',
          after: 'Leads are enquiries from the website, calls, camps and referrals. They move across the pipeline until the person books or is lost.',
          wait: { role: 'heading', name: 'Leads' },
        },
        {
          click: { role: 'button', name: 'New lead' },
          say: 'A person phones to ask about a health check-up. Click New lead.',
          wait: { role: 'dialog', name: 'New lead' },
        },
        {
          fill: [{ label: 'Name' }, 'Geetha Priya'],
          say: 'Type the name.',
          zoom: 1.3,
        },
        {
          fill: [{ label: 'Mobile' }, '9876501234'],
          say: 'Type the 10-digit mobile number. This is how you call back.',
          zoom: 1.3,
        },
        {
          fill: [{ label: 'Package or service asked about' }, 'Master health check-up'],
          say: 'Write what she asked about.',
          zoom: 1.3,
        },
        {
          click: { role: 'button', name: 'Add lead' },
          say: 'Click Add lead.',
          after: 'The lead opens, with its number and the stage New. You can log a call or note, change its stage, or convert it into a patient.',
          wait: { role: 'heading', name: /^Geetha Priya/ },
        },
      ],
    },
    {
      title: 'Add a follow-up call',
      steps: [
        {
          nav: 'Follow-up calls',
          say: 'Open Follow-up calls.',
          after: 'Calls after discharge are added by HC360 two days after the patient goes home. You add the others yourself.',
          wait: { role: 'heading', name: 'Follow-up calls' },
        },
        {
          click: { role: 'button', name: 'Add follow-up call' },
          say: 'Meenakshi Sundaram had fever and went home with medicines. Click Add follow-up call.',
          wait: { role: 'dialog', name: 'Add a follow-up call' },
        },
        {
          fill: [{ label: 'Patient' }, 'Meenakshi'],
          say: 'Find the patient.',
          wait: { role: 'option', name: /Meenakshi Sundaram/ },
        },
        {
          click: { role: 'option', name: /Meenakshi Sundaram/ },
          say: 'Choose Meenakshi Sundaram.',
        },
        {
          fill: [{ label: 'Call on' }, '2026-11-19'],
          say: 'Choose the day to call. Today is 19 Nov 2026.',
          zoom: 1.3,
        },
        {
          fill: [{ label: 'Notes for the caller' }, 'Ask if the fever has gone and whether she has finished the medicines.'],
          say: 'Write what the caller should ask.',
          zoom: 1.3,
        },
        {
          click: { role: 'button', name: 'Add follow-up' },
          say: 'Click Add follow-up.',
          after: 'The call is on the Due today list.',
          wait: { role: 'row', name: /Meenakshi Sundaram/ },
        },
      ],
    },
    {
      title: 'Draft a campaign',
      steps: [
        {
          nav: 'Campaigns',
          say: 'Open Campaigns.',
          after: 'Campaigns are SMS or WhatsApp messages. Sending happens between 10:00 and 21:00 only, and every message has an opt-out link.',
          wait: { role: 'heading', name: 'Campaigns' },
        },
        {
          click: { role: 'button', name: 'New campaign' },
          say: 'Click New campaign.',
          wait: { role: 'heading', name: 'New campaign' },
        },
        {
          fill: [{ label: 'Campaign name' }, 'Diabetes camp, December'],
          say: 'Name the campaign. Only staff see the name.',
          zoom: 1.3,
        },
        {
          fill: [{ label: 'Message' }, 'Free blood sugar check at Nana Hospitals, Theni, on Saturday 5 December, 9 am to 1 pm.'],
          say: 'Write the message.',
          zoom: 1.3,
        },
        {
          click: { role: 'button', name: /^Save draft/ },
          say: 'Click Save draft.',
          after: 'The draft is saved.',
          wait: { text: 'Campaign saved' },
        },
        {
          show: { role: 'heading', name: 'Audience' },
          say: 'Only patients who agreed to marketing messages are ever counted. This is the law, and HC360 checks consent again when each message is sent.',
          zoom: 1.3,
        },
      ],
    },
  ],
  recap: [
    'Capture every enquiry as a lead with a name, a mobile number and what the person asked about.',
    'Add a follow-up call for a patient, and say what the caller should ask.',
    'A campaign reaches only patients who agreed to messages, and a second person approves it before it is sent.',
  ],
  check: [
    {
      q: 'Which patients receive a campaign?',
      a: 'Only patients who agreed to marketing messages. HC360 checks the consent again when each message is sent.',
    },
    {
      q: 'Who adds the follow-up calls after discharge?',
      a: 'HC360 adds them two days after discharge. You add the other calls yourself.',
    },
  ],
  next: 'Quality manager: incidents, corrective actions and indicators',
};
