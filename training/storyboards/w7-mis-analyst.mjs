// Wave 7 (insights): the MIS analyst's day. Reading the executive dashboard, opening an MIS report and reading its
// figures, and scheduling that report to be e-mailed every morning. Recorded as the MIS analyst (Pooja Sundar) at
// 11:10 am, when the day's figures are small because the hospital's morning has only just begun.

const nav = { role: 'navigation', name: 'Main navigation' };

export default {
  id: 'w7-mis-analyst',
  title: 'MIS analyst: read the reports, send them on a schedule',
  role: 'MisAnalyst',
  roleLabel: 'MIS analyst',
  user: 'mis',
  wave: 7,
  audience: 'For the MIS analyst of Nana Hospitals, who prepares the figures that managers read.',
  intro:
    'Managers ask the MIS analyst for figures: how many patients came, how full the beds are, how fast the laboratory works. HC360 reads these from the live database, so you do not collect them by hand.',
  objectives: [
    'Read the executive dashboard and open the report behind a figure.',
    'Open an MIS report and read its summary and table.',
    'Schedule a report to be e-mailed to managers at a fixed time.',
  ],
  modules: ['analytics'],
  covers: ['/analytics', '/analytics/reports', '/analytics/schedules'],
  scenes: [
    {
      title: 'The executive dashboard',
      steps: [
        {
          click: { within: nav, role: 'button', name: 'Analytics' },
          say: 'Open Analytics from the menu.',
        },
        {
          nav: 'Executive dashboard',
          say: 'Open the Executive dashboard.',
          after: 'It shows the hospital’s main figures for this month: out-patient visits, admissions, bed occupancy, length of stay and laboratory turnaround.',
          wait: { role: 'heading', name: 'Executive dashboard' },
        },
        {
          show: { role: 'link', name: /^OP visits: 12/ },
          say: 'Each tile shows a figure and compares it with the previous period. Here, 12 out-patient visits this month.',
          zoom: 1.4,
        },
        {
          show: { role: 'combobox', name: 'Period' },
          say: 'Change the period to look at today, last week or last month. Every figure changes with it.',
          zoom: 1.5,
        },
      ],
    },
    {
      title: 'Open an MIS report',
      steps: [
        {
          nav: 'MIS reports',
          say: 'Open MIS reports.',
          after: 'The list holds every report you may open, grouped by area. Each one can be exported to Excel, CSV or PDF.',
          wait: { role: 'heading', name: 'MIS reports' },
        },
        {
          click: { role: 'link', name: /^Census by ward \(now\)/ },
          say: 'Open Census by ward. It shows the beds in every ward right now.',
          after: 'The summary says how many beds are occupied and vacant, and the occupancy now.',
          wait: { role: 'heading', name: 'Census by ward (now)' },
        },
        {
          click: { role: 'tab', name: 'Table' },
          say: 'Choose the Table tab to read the exact numbers for each ward.',
          wait: { role: 'table', name: 'Census by ward (now)' },
        },
        {
          show: { role: 'row', name: /^General Ward — Female/ },
          say: 'In the female general ward, one of twelve beds is occupied. That is Fatima Begum.',
          zoom: 1.3,
        },
        {
          show: { role: 'button', name: 'Export' },
          say: 'Use Export to download the report as Excel, CSV or PDF.',
          zoom: 1.6,
        },
      ],
    },
    {
      title: 'Send the report every morning',
      steps: [
        {
          click: { role: 'button', name: 'E-mail regularly' },
          say: 'To send this report on a schedule, click E-mail regularly.',
          after: 'The schedule form opens with the report already chosen.',
          wait: { role: 'dialog', name: 'New scheduled e-mail' },
        },
        {
          show: { role: 'combobox', name: 'How often' },
          say: 'Choose how often to send it, and at what time. The default is every day at 07:00.',
          zoom: 1.4,
        },
        {
          fill: [{ label: 'Send to' }, 'medical.director@nanahospitals.in'],
          say: 'Type the manager’s e-mail address.',
          zoom: 1.4,
        },
        {
          press: 'Enter',
          say: 'Press Enter to add it. You can add up to 20 addresses.',
        },
        {
          click: { role: 'button', name: 'Create schedule' },
          say: 'Click Create schedule.',
          after: 'The schedule appears in Scheduled e-mails, with its time and the address it goes to.',
          wait: { role: 'table', name: 'Scheduled e-mails' },
        },
      ],
    },
  ],
  recap: [
    'The executive dashboard shows the hospital’s main figures. Change the period to compare.',
    'Open a report from MIS reports, read its summary and table, and export it if someone needs a file.',
    'Schedule a report with E-mail regularly, and the manager receives it without asking.',
  ],
  check: [
    {
      q: 'A manager asks for the bed census every morning. What do you do?',
      a: 'Open Census by ward, click E-mail regularly, choose how often and the time, add the manager’s address and create the schedule.',
    },
    {
      q: 'Where do the figures in the reports come from?',
      a: 'From the live database. They are the hospital’s figures as they stand, so nothing is collected by hand.',
    },
  ],
};
