// Wave 6 (back office): the HR manager's day. Deciding a leave request, planning the duty roster for the week, and
// starting the month's payroll. Recorded as the HR manager (Kalaiselvi Murthy) at 11:10 am. Setup: the staff nurse of
// the female ward (Deepa Raman) applies for two days of casual leave, as she would in My HR.

const nav = { role: 'navigation', name: 'Main navigation' };

export default {
  id: 'w6-hr-manager',
  title: 'HR manager: decide leave, plan the roster, start payroll',
  role: 'HrManager',
  roleLabel: 'HR manager',
  user: 'hrmanager',
  wave: 6,
  audience: 'For the HR manager and HR executives of Nana Hospitals.',
  intro:
    'HR keeps the people side of the hospital: who is on duty, who is on leave, and what each person is paid. In HC360, staff ask for leave themselves, and you decide.',
  objectives: [
    'Approve a leave request.',
    'Plan the duty roster of a department for a week.',
    'Check attendance and start the month’s payroll.',
  ],
  modules: ['hr'],
  covers: ['/hr', '/hr/leave', '/hr/roster', '/hr/attendance', '/hr/payroll'],
  setup: async ({ api }) => {
    const nurse = await api('nurse');
    const types = await nurse.get('/api/v1/hr/leave/types');
    const casual = types.find((t) => t.code === 'CL');
    await nurse.post('/api/v1/hr/me/leave/requests', {
      leaveTypeId: casual.id,
      fromDate: '2026-11-24',
      fromSession: 'FirstHalf',
      toDate: '2026-11-25',
      toSession: 'SecondHalf',
      reason: 'Family function at Madurai.',
      attachmentFileId: null,
    });
  },
  scenes: [
    {
      title: 'Decide a leave request',
      steps: [
        {
          click: { within: nav, role: 'button', name: 'Human resources' },
          say: 'Open Human resources from the menu.',
        },
        {
          nav: 'HR overview',
          say: 'Open the HR overview.',
          after: 'It shows the staff on the rolls, who is present, and the leave that waits for your decision.',
          wait: { role: 'heading', name: 'HR overview' },
        },
        {
          show: { role: 'table', name: 'Leave waiting for approval' },
          say: 'Leave waiting for approval is listed here, oldest first. Deepa Raman has asked for leave.',
          zoom: 1.3,
        },
        {
          nav: 'Leave',
          say: 'Open the Leave screen.',
          after: 'Waiting requests are listed with the dates, the number of days and the reason.',
          wait: { role: 'row', name: /Deepa Raman/ },
        },
        {
          show: { role: 'row', name: /Deepa Raman/ },
          say: 'Deepa Raman asks for two days of casual leave, for a family function. HC360 checks her balance again when you approve.',
          zoom: 1.3,
        },
        {
          click: { within: { role: 'row', name: /Deepa Raman/ }, role: 'button', name: /^Approve/ },
          say: 'Click Approve.',
          after: 'The request leaves the waiting list.',
          wait: { text: 'No leave waiting for approval' },
        },
      ],
    },
    {
      title: 'Plan the duty roster',
      steps: [
        {
          nav: 'Duty roster',
          say: 'Open the Duty roster.',
          after: 'The roster is planned for one department and one week at a time.',
          wait: { role: 'heading', name: 'Duty roster' },
        },
        {
          select: [{ label: 'Department' }, 'General Medicine'],
          native: true,
          say: 'Choose the department. Here, General Medicine.',
          wait: { role: 'table', name: /^Duty roster, week of/ },
          zoom: 1.3,
        },
        {
          click: { role: 'button', name: 'Next week' },
          say: 'Choose the week. Click Next week.',
          wait: { role: 'table', name: /^Duty roster, week of 23 Nov/ },
        },
        {
          click: { within: { role: 'toolbar', name: 'Choose what to assign' }, role: 'button', name: /GEN/ },
          say: 'Choose a shift to assign. Here, the General shift.',
          zoom: 1.3,
        },
        {
          click: { role: 'button', name: /^Arjun Mehta, 23 Nov/ },
          say: 'Then click the days. Dr. Arjun Mehta works on Monday.',
        },
        {
          click: { role: 'button', name: /^Arjun Mehta, 24 Nov/ },
          say: 'And on Tuesday.',
        },
        {
          click: { role: 'button', name: /^Save roster/ },
          say: 'Click Save roster. HC360 checks for clashes first, such as two shifts on one day.',
          after: 'The two days are saved as General duty. The row On duty counts the staff on each day.',
          wait: { role: 'button', name: 'Save roster' },
        },
      ],
    },
    {
      title: 'Attendance and payroll',
      steps: [
        {
          nav: 'Attendance',
          say: 'Open Attendance.',
          after: 'The daily sheet is computed from the punches at the terminals, the roster, leave and holidays.',
          wait: { role: 'heading', name: 'Attendance' },
        },
        {
          click: { role: 'tab', name: 'Corrections' },
          say: 'When a punch is missed, staff ask for a correction from My HR. Open the Corrections tab.',
          after: 'Nothing is waiting now. When you approve a correction, HC360 recomputes the day at once.',
          wait: { role: 'tab', name: 'Corrections', selected: true },
        },
        {
          nav: 'Payroll',
          say: 'Open Payroll.',
          wait: { role: 'heading', name: 'Payroll' },
        },
        {
          click: { role: 'button', name: 'New payroll' },
          say: 'There is one payroll a month. Click New payroll.',
          wait: { role: 'dialog', name: 'New payroll' },
        },
        {
          select: [{ label: 'Month' }, 'October'],
          native: true,
          say: 'Choose the month. Here, October.',
        },
        {
          click: { role: 'button', name: 'Create payroll' },
          say: 'Click Create payroll.',
          after: 'The October payroll opens as a draft, at the step Collect inputs. Next you process it, a second person approves it, and then it is paid and locked.',
          wait: { role: 'heading', name: /^Payroll · October 2026/ },
        },
      ],
    },
  ],
  recap: [
    'Approve or reject leave from the Leave screen. HC360 checks the balance again when you approve.',
    'Plan the roster for one department and one week. Choose a shift, click the days, and save.',
    'Payroll is one run a month. The person who approves it must be different from the person who processed it.',
  ],
  check: [
    {
      q: 'Why can you not approve your own leave request in HC360?',
      a: 'Nobody can approve their own request. Another person who may approve leave decides it.',
    },
    {
      q: 'Who must approve a payroll after you have processed it?',
      a: 'A different person. The person who approves must not be the person who processed it.',
    },
  ],
};
