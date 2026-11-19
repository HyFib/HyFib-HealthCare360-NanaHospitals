// Wave 6 (back office): the biomedical engineer's day. Taking a breakdown ticket from open to resolved, recording a
// calibration so the equipment stays in date, and a preventive maintenance visit. Recorded as the biomedical engineer
// (Vignesh Raja) at 11:10 am, when the infusion pump of the ward has been reported (src/web/demo/story.mjs).

const nav = { role: 'navigation', name: 'Main navigation' };

export default {
  id: 'w6-biomedical-engineer',
  title: 'Biomedical engineer: breakdown tickets, calibration and maintenance',
  role: 'BiomedicalEngineer',
  roleLabel: 'Biomedical engineer',
  user: 'biomed',
  wave: 6,
  audience: 'For the biomedical engineers of Nana Hospitals.',
  intro:
    'Every ventilator, pump and autoclave in the hospital is in the asset register. HC360 tells you what has broken, what is due for maintenance and what must be calibrated.',
  objectives: [
    'Take a breakdown ticket from open to resolved.',
    'Record a calibration.',
    'Record a preventive maintenance visit.',
  ],
  modules: ['assets'],
  covers: ['/assets', '/assets/register', '/assets/tickets', '/assets/maintenance'],
  scenes: [
    {
      title: 'Your equipment this morning',
      steps: [
        {
          click: { within: nav, role: 'button', name: 'Equipment and assets' },
          say: 'Open Equipment and assets from the menu.',
        },
        {
          nav: 'Equipment overview',
          say: 'Open the equipment overview.',
          after: 'It counts the assets, the open breakdowns, the maintenance that is overdue and the calibrations that are due.',
          wait: { role: 'button', name: /^Open breakdowns/ },
          result: { role: 'button', name: /^Open breakdowns/ },
        },
        {
          show: { role: 'table', name: 'Falling due' },
          say: 'Falling due lists what needs you in the next 30 days: maintenance, calibration, warranties and service contracts. Overdue work comes first.',
          zoom: 1.2,
        },
      ],
    },
    {
      title: 'Take a breakdown ticket to resolved',
      steps: [
        {
          nav: 'Breakdown tickets',
          say: 'Open Breakdown tickets.',
          after: 'Anyone can report equipment that does not work. The ticket waits here for you.',
          wait: { role: 'row', name: /NHMT26000001/ },
        },
        {
          click: { role: 'row', name: /NHMT26000001/ },
          say: 'The infusion pump NHAS000007 keeps sounding an occlusion alarm. Open the ticket.',
          after: 'The downtime runs from the moment the ticket was reported.',
          wait: { role: 'dialog', name: 'Ticket NHMT26000001' },
          zoom: 1.2,
        },
        {
          click: { role: 'button', name: 'Assign engineer' },
          say: 'Click Assign engineer.',
          wait: { role: 'dialog', name: 'Assign NHMT26000001' },
        },
        {
          fill: [{ label: 'Engineer or service team' }, 'Vignesh Raja'],
          say: 'Type who repairs it. This can be you, or the vendor’s engineer.',
          zoom: 1.3,
        },
        {
          click: { role: 'button', name: 'Assign' },
          say: 'Click Assign.',
          after: 'The ticket is Assigned, with the name of the engineer.',
          after_do: async ({ page }) => {
            await page.getByRole('dialog', { name: 'Assign NHMT26000001' }).waitFor({ state: 'hidden', timeout: 15000 });
          },
          wait: { text: 'Start work' },
        },
        {
          click: { text: 'Start work' },
          say: 'When you begin the repair, click Start work.',
          wait: { text: 'Start work on this breakdown?' },
        },
        {
          click: { role: 'button', name: 'Start work', last: true },
          say: 'Confirm. The ticket then shows as being repaired.',
          after_do: async ({ page }) => {
            await page.getByText('Start work on this breakdown?').waitFor({ state: 'hidden', timeout: 15000 });
          },
          wait: { text: 'Mark resolved' },
        },
        {
          click: { text: 'Mark resolved' },
          say: 'When the pump works again, click Mark resolved.',
          wait: { role: 'dialog', name: 'Resolve NHMT26000001' },
        },
        {
          fill: [{ label: 'What was done' }, 'Occlusion sensor cleaned and tested. Pump ran for 30 minutes without an alarm.'],
          say: 'Write what you did.',
          zoom: 1.3,
        },
        {
          fill: [{ label: 'Root cause' }, 'Dried medicine on the occlusion sensor.'],
          say: 'And the root cause. It helps to find equipment that fails again and again.',
          zoom: 1.3,
        },
        {
          click: { within: { role: 'dialog', name: 'Resolve NHMT26000001' }, role: 'button', name: 'Mark resolved' },
          say: 'Click Mark resolved. The ward then confirms that it works, and you close the ticket.',
          after: 'The ticket is Resolved, with what was done and the root cause. The downtime stops, and Close ticket is now offered.',
          wait: { text: 'Close ticket' },
        },
        {
          click: { role: 'button', name: 'Close', exact: true },
          say: 'Close the ticket window.',
        },
      ],
    },
    {
      title: 'Calibration and maintenance',
      steps: [
        {
          nav: 'Maintenance and calibration',
          say: 'Open Maintenance and calibration.',
          after: 'Overdue work stays listed until it is recorded.',
          wait: { role: 'table', name: 'Preventive maintenance plans' },
        },
        {
          click: { role: 'tab', name: /^Calibration/ },
          say: 'Open the Calibration tab.',
          wait: { role: 'table', name: 'Calibration due' },
        },
        {
          click: { within: { role: 'row', name: /Horizontal Autoclave/ }, role: 'button', name: 'Record calibration' },
          say: 'The autoclave of the CSSD is due on 23 November. Click Record calibration.',
          wait: { role: 'dialog', name: /^Record calibration/ },
        },
        {
          fill: [{ label: 'Agency' }, 'Southern Calibration Labs'],
          say: 'Write the agency that calibrated it. Use a laboratory accredited by NABL.',
          zoom: 1.3,
        },
        {
          fill: [{ label: 'Certificate number' }, 'SCL-2026-1187'],
          say: 'Write the number of its certificate.',
          zoom: 1.3,
        },
        {
          fill: [{ label: 'Next due' }, '2027-05-19'],
          say: 'Choose when it is next due.',
          zoom: 1.3,
        },
        {
          click: { within: { role: 'dialog', name: /^Record calibration/ }, role: 'button', name: 'Record' },
          say: 'Click Record.',
          after: 'The autoclave leaves the list of calibrations that are due.',
          wait: { text: 'Calibration recorded' },
        },
        {
          click: { role: 'tab', name: /^Preventive maintenance/ },
          say: 'Open Preventive maintenance.',
          wait: { role: 'table', name: 'Preventive maintenance plans' },
        },
        {
          click: { within: { role: 'row', name: /Biphasic Defibrillator/ }, role: 'button', name: 'Record visit' },
          say: 'The defibrillator’s maintenance is overdue. Click Record visit.',
          wait: { role: 'dialog', name: /^Record visit/ },
        },
        {
          fill: [{ label: 'Done by' }, 'Southern Biomedical Services'],
          say: 'Write who did the visit.',
          zoom: 1.3,
        },
        {
          fill: [{ label: 'Findings' }, 'Battery and pads checked. Energy delivery tested at 200 joules.'],
          say: 'Write what was checked.',
          zoom: 1.3,
        },
        {
          click: { within: { role: 'dialog', name: /^Record visit/ }, role: 'button', name: 'Record visit' },
          say: 'Click Record visit.',
          after: 'HC360 sets the date of the next visit from the plan.',
          wait: { text: 'Visit recorded' },
        },
      ],
    },
    {
      title: 'The asset register',
      steps: [
        {
          nav: 'Asset register',
          say: 'Open the Asset register.',
          after: 'Every piece of equipment is listed with its tag, where it is, how critical it is, and when its next maintenance, calibration and warranty fall due.',
          wait: { role: 'table', name: 'Assets' },
        },
        {
          fill: [{ label: 'Search' }, 'ventilator'],
          say: 'Search by name or tag. You can also scan the tag on the equipment.',
          wait: { role: 'row', name: /ICU Ventilator/ },
          zoom: 1.2,
        },
      ],
    },
  ],
  recap: [
    'Assign a breakdown ticket, start work, and mark it resolved with what you did and the root cause.',
    'Record each calibration and each maintenance visit, so that nothing stays overdue.',
    'The asset register lists every piece of equipment with its tag and its due dates.',
  ],
  check: [
    {
      q: 'Why do you write the root cause when you resolve a ticket?',
      a: 'It helps to find equipment that fails again and again.',
    },
    {
      q: 'Where do you record that the autoclave was calibrated?',
      a: 'On Maintenance and calibration, on the Calibration tab, with Record calibration.',
    },
  ],
  next: 'CSSD technician: receive trays, run a sterilizer load, issue sterile packs',
};
