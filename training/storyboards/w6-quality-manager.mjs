// Wave 6 (back office): the quality manager's day. Reviewing an incident a nurse reported, planning a corrective
// action for it, and recording a quality indicator and reading the risk register. Recorded as the quality manager
// (Dr. Meera Krishnan) at 11:10 am. setup() has the nurse of the female ward report a near miss first.

const nav = { role: 'navigation', name: 'Main navigation' };

export default {
  id: 'w6-quality-manager',
  title: 'Quality manager: incidents, corrective actions, indicators and risks',
  role: 'QualityManager',
  roleLabel: 'Quality manager',
  user: 'quality',
  wave: 6,
  audience: 'For the quality manager of Nana Hospitals, who reviews incidents and keeps the hospital’s safety records.',
  intro:
    'Staff report incidents and near misses. The quality manager reviews each one, plans corrective actions so it does not happen again, and watches the quality indicators and the risk register.',
  objectives: [
    'Review a reported incident and start its review.',
    'Plan a corrective action with an owner and a due date.',
    'Record a quality indicator and read the risk register.',
  ],
  modules: ['quality'],
  covers: ['/quality/incidents', '/quality/capas', '/quality/indicators', '/quality/risks'],
  setup: async ({ api }) => {
    const nurse = await api('nurse');
    await nurse.get('/api/v1/quality/incidents/mine');
    const { serverNow } = await import('../../src/web/demo/lib/api.mjs');
    await nurse.post('/api/v1/quality/incidents', {
      type: 'NearMiss',
      severity: 'NearMiss',
      occurredAt: new Date(serverNow().getTime() - 3600e3).toISOString(),
      location: 'Female general ward',
      description:
        'A wrong-strength insulin vial was in the ward trolley. A second nurse saw it before any dose was drawn.',
      immediateAction: 'The vial was taken out and returned to the pharmacy.',
      isAnonymous: false,
      flagAsSentinel: false,
    });
  },
  scenes: [
    {
      title: 'Review a reported incident',
      steps: [
        {
          click: { within: nav, role: 'button', name: 'Quality and safety' },
          say: 'Open Quality and safety from the menu.',
        },
        {
          nav: 'Incidents',
          say: 'Open Incidents.',
          after: 'Every incident that is not closed is listed. This morning the nurse of the female general ward reported a near miss.',
          wait: { role: 'heading', name: 'Incidents' },
        },
        {
          click: { role: 'row', name: /Near miss/ },
          say: 'Open the near miss.',
          wait: { role: 'button', name: 'Start review' },
        },
        {
          show: { role: 'tab', name: 'What happened' },
          say: 'What happened tells the story in the reporter’s words: a wrong-strength insulin vial, noticed before any dose was drawn. No harm reached a patient.',
          zoom: 1.2,
        },
        {
          click: { role: 'button', name: 'Start review' },
          say: 'Click Start review.',
          wait: { role: 'dialog' },
        },
        {
          click: { within: { role: 'dialog' }, role: 'button', name: 'Start review' },
          say: 'Confirm.',
          after: 'The incident is now Under review.',
          wait: { text: 'Under review' },
        },
      ],
    },
    {
      title: 'Plan a corrective action',
      steps: [
        {
          click: { role: 'tab', name: /^Actions/ },
          say: 'Open the Actions tab.',
          wait: { role: 'button', name: 'Plan an action' },
        },
        {
          click: { role: 'button', name: 'Plan an action' },
          say: 'A near miss is a warning. Click Plan an action, so that it does not happen again.',
          wait: { role: 'dialog', name: /^Plan a corrective/ },
        },
        {
          fill: [
            { label: 'Action' },
            'Store look-alike insulin vials apart in the ward trolley, and add tall-man labels.',
          ],
          say: 'Write the action. A corrective action fixes the cause. A preventive action stops it happening elsewhere.',
          zoom: 1.3,
        },
        {
          fill: [{ label: 'Owner' }, 'Meera'],
          say: 'Every action has an owner. You can choose yourself. Choosing other staff needs access to the staff list.',
          wait: { role: 'option', name: /Meera Krishnan/ },
        },
        {
          click: { role: 'option', name: /Meera Krishnan/ },
          say: 'Choose yourself as the owner.',
        },
        {
          show: { label: 'Due by' },
          say: 'Every action has a due date. About 30 days after it is done, the quality team checks that it worked.',
          zoom: 1.4,
        },
        {
          click: { role: 'button', name: 'Plan action' },
          say: 'Click Plan action.',
          after: 'The action is on the incident, with its number and its due date.',
          wait: { role: 'table', name: 'Corrective and preventive actions' },
        },
      ],
    },
    {
      title: 'Indicators and the risk register',
      steps: [
        {
          nav: 'Quality indicators',
          say: 'Open Quality indicators.',
          after: 'Each indicator has a target. Some are computed by HC360. Others are entered by hand.',
          wait: { role: 'heading', name: 'Quality indicators' },
        },
        {
          click: { within: { role: 'row', name: /^Hand hygiene compliance rate/ }, role: 'button', name: /^Record/ },
          say: 'Hand hygiene compliance is entered by hand. Click Record.',
          wait: { role: 'dialog', name: /^Record value Hand hygiene/ },
        },
        {
          fill: [{ label: /^Numerator/ }, '88'],
          say: 'Enter the opportunities where hand hygiene was complied with.',
          zoom: 1.3,
        },
        {
          fill: [{ label: /^Denominator/ }, '100'],
          say: 'Enter the opportunities observed.',
          zoom: 1.3,
        },
        {
          show: { role: 'status' },
          say: 'HC360 works out the rate and compares it with the target of 90%.',
          zoom: 1.4,
        },
        {
          click: { role: 'button', name: 'Save value' },
          say: 'Click Save value.',
          wait: { role: 'row', name: /Hand hygiene compliance rate/ },
        },
        {
          nav: 'Risk register',
          say: 'Open the Risk register.',
          after: 'Risk is likelihood times impact, shown on a heat map. Each risk has controls and a review date.',
          wait: { role: 'heading', name: 'Risk register' },
        },
      ],
    },
  ],
  recap: [
    'Review every reported incident, including a near miss. Start the review so that it has an owner and a stage.',
    'Plan a corrective action with an owner and a due date, and check about 30 days later that it worked.',
    'Record the indicators that are entered by hand, and review the risk register by its dates.',
  ],
  check: [
    {
      q: 'No patient was harmed. Why does the near miss still need an action?',
      a: 'It shows how harm could happen. An action fixes the cause, so that it does not reach a patient next time.',
    },
    {
      q: 'What does every corrective action have?',
      a: 'An owner and a due date. The quality team checks about 30 days after it is done that it worked.',
    },
  ],
  next: 'MIS analyst: read the reports, send them on a schedule',
};
