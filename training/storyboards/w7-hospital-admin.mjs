// Wave 7 (insights): the hospital administrator's morning. The executive dashboard and the bed board, approving a new
// user another administrator asked for (the two-person rule), and the masters (services and prices). Recorded as the
// hospital administrator at 11:10 am. setup() has the system administrator ask for a new account, as they would earlier.

const nav = { role: 'navigation', name: 'Main navigation' };
const approve = { role: 'alertdialog', name: /^Approve/ };

export default {
  id: 'w7-hospital-admin',
  title: 'Hospital administrator: the dashboard, the bed board and approvals',
  role: 'HospitalAdmin',
  roleLabel: 'Hospital administrator',
  user: 'hospitaladmin',
  wave: 7,
  audience: 'For the hospital administrators of Nana Hospitals.',
  intro:
    'The hospital administrator sees how the hospital is doing, and decides what another person asked for. This video shows the executive dashboard, the bed board, the approvals inbox and the masters.',
  objectives: [
    'Read the executive dashboard and the bed board.',
    'Approve a change another administrator asked for.',
    'Find the hospital’s services and prices in the masters.',
  ],
  modules: ['analytics', 'inpatient', 'approvals', 'masters'],
  covers: ['/analytics', '/inpatient/beds', '/approvals', '/setup/services'],
  setup: async ({ api }) => {
    // Earlier this morning the system administrator asked for a new account: a billing executive, who starts on Monday.
    const admin = await api('admin');
    const roles = await admin.get('/api/v1/admin/roles');
    const billing = (roles.items ?? roles).find((r) => r.name === 'Billing');
    await admin.post('/api/v1/admin/users', {
      userName: 'meena.k',
      displayName: 'Meena Karthikeyan',
      email: null,
      phone: null,
      roleIds: [billing.id],
      facilityIds: null,
      defaultFacilityId: null,
      practitionerId: null,
      temporaryPassword: null,
      reason: 'New billing executive for the OP counters, joining on Monday',
      allFacilities: false,
    });
  },
  scenes: [
    {
      title: 'The hospital this morning',
      steps: [
        {
          click: { within: nav, role: 'button', name: 'Analytics' },
          say: 'Open Analytics from the menu.',
        },
        {
          nav: 'Executive dashboard',
          say: 'Then choose Executive dashboard.',
          after: 'One page shows the hospital’s main figures: visits, admissions, bed occupancy, turnaround time, revenue and collections.',
          wait: { role: 'heading', name: 'Executive dashboard' },
        },
        {
          select: [{ label: 'Period' }, 'Today'],
          native: true,
          say: 'The dashboard opens on this month. Choose Today to see this morning only.',
          wait: { role: 'link', name: /^OP visits/ },
          result: { role: 'link', name: /^OP visits/ },
          zoom: 1.2,
        },
        {
          show: { role: 'link', name: /^Revenue/ },
          say: 'Each figure is compared with the previous period. Click a figure to open the report behind it.',
          zoom: 1.4,
        },
        {
          click: { within: nav, role: 'button', name: 'In-patients' },
          say: 'Now open In-patients.',
        },
        {
          nav: 'Bed board',
          say: 'Then choose Bed board.',
          after: 'Every ward and bed is shown, live. Most beds are vacant. One is occupied.',
          wait: { role: 'heading', name: 'General Ward — Female' },
        },
        {
          show: { role: 'button', name: /^Bed GWF-01, occupied/ },
          say: 'Bed GWF-01 is occupied by Fatima Begum. HC360 marks her as urgent on the early warning score, and as escalated.',
          zoom: 1.6,
        },
      ],
    },
    {
      title: 'Approve what another administrator asked for',
      steps: [
        {
          nav: 'Approvals',
          say: 'Open the Approvals screen.',
          after: 'The system administrator asked for a new user account. It waits here for you.',
          wait: { role: 'row', name: /Meena Karthikeyan/ },
        },
        {
          show: { role: 'row', name: /Meena Karthikeyan/ },
          say: 'Meena Karthikeyan is a new billing executive. The Billing role is not given until a second person approves it. Nobody approves their own request.',
          zoom: 1.4,
        },
        {
          click: { role: 'cell', name: /Access for Meena Karthikeyan/ },
          say: 'Open the request, and read what it gives.',
          wait: { role: 'heading', name: 'Access given to a user' },
        },
        {
          show: { role: 'heading', name: 'Access given to a user' },
          say: 'It gives the Billing role. Check the reason, and who asked. Approve only what you know is right.',
          zoom: 1.2,
        },
        {
          click: { role: 'button', name: 'Approve' },
          say: 'Click Approve.',
          wait: approve,
        },
        {
          fill: [{ within: approve, label: 'Note' }, 'Checked with the billing manager'],
          say: 'You may write a note. It is kept with the approval.',
          zoom: 1.3,
        },
        {
          click: { within: approve, role: 'button', name: 'Approve' },
          say: 'Click Approve in the window.',
          after: 'The request is now Approved, with who decided and your note. Made shows that one role was given. Roles held now lists the roles from when it was asked, so it still says None.',
          wait: { text: 'Approved' },
          zoom: 1.2,
        },
      ],
    },
    {
      title: 'Services and prices',
      steps: [
        {
          click: { within: nav, role: 'button', name: 'Hospital setup' },
          say: 'The masters are under Hospital setup. Open it.',
        },
        {
          nav: 'Services and prices',
          say: 'Choose Services and prices.',
          after: 'Everything the hospital charges for is here, with its base price and GST.',
          wait: { role: 'heading', name: 'Services and prices' },
        },
        {
          fill: [{ label: 'Search' }, 'Consultation'],
          say: 'Search for a service. Type Consultation.',
          wait: { role: 'cell', name: /^Consultation/ },
        },
        {
          show: { role: 'table', name: 'Services' },
          say: 'Prices are set here. A change to a price also waits for a second person to approve it, so that nobody changes what patients pay alone.',
          zoom: 1.2,
        },
      ],
    },
  ],
  recap: [
    'The executive dashboard and the bed board show how the hospital is doing now.',
    'Changes another person asks for wait in Approvals. Nobody approves their own.',
    'Services and prices are in Hospital setup. A price change needs a second person.',
  ],
  check: [
    {
      q: 'Why does the new account of Meena Karthikeyan wait in your Approvals?',
      a: 'Access is given by two people. The system administrator asked for it, and a second administrator decides. Nobody approves their own request.',
    },
    {
      q: 'You open the executive dashboard and see this month’s figures. How do you see this morning only?',
      a: 'Choose Today in the Period list.',
    },
  ],
};
