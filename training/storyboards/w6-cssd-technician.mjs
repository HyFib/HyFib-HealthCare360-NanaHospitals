// Wave 6 (back office): the CSSD technician's day. Receiving used trays from the operation theatre against the count
// sheet, running a steam sterilizer load through to release, and issuing sterile packs
// to a department. Recorded as the CSSD technician (Saravanan Thangavel) at 11:10 am. The shelf starts empty, so the
// video makes the packs it then issues.

const nav = { role: 'navigation', name: 'Main navigation' };

export default {
  id: 'w6-cssd-technician',
  title: 'CSSD technician: receive trays, run a sterilizer load, issue sterile packs',
  role: 'CssdTechnician',
  roleLabel: 'CSSD technician',
  user: 'cssd',
  wave: 6,
  audience: 'For the technicians of the Central Sterile Supply Department at Nana Hospitals.',
  intro:
    'The CSSD makes sure every instrument tray that reaches a patient is sterile. HC360 keeps the count of each tray, the record of each sterilizer cycle, and the place of each pack, so a failed load can be traced.',
  objectives: [
    'Receive used trays and count them against the count sheet.',
    'Run a sterilizer load, record its result and release the packs.',
    'Issue sterile packs to a department.',
  ],
  modules: ['cssd'],
  covers: ['/cssd', '/cssd/receipts', '/cssd/loads', '/cssd/packs', '/cssd/issue'],
  setup: async ({ api }) => {
    // The trays were washed earlier this morning: a passed wash cycle of two minor surgical trays.
    const cssd = await api('cssd');
    const sets = await cssd.get('/api/v1/cssd/sets');
    const minor = (sets.items ?? sets).find((x) => x.code === 'MINOR-TRAY');
    const wash = await cssd.post('/api/v1/cssd/wash-cycles', {
      washerName: 'Washer-disinfector 1',
      program: 'Instruments 90 °C / 5 min',
      operatorName: 'Saravanan Thangavel',
      notes: null,
      trays: [{ instrumentSetId: minor.id, quantity: 2 }],
    });
    await cssd.post(`/api/v1/cssd/wash-cycles/${wash.id}/complete`, { temperatureC: 90, holdMinutes: 5, inspectedClean: true });
  },
  scenes: [
    {
      title: 'Receive used trays',
      steps: [
        {
          click: { within: nav, role: 'button', name: 'CSSD' },
          say: 'Open CSSD from the menu.',
        },
        {
          nav: 'Soiled receipt',
          say: 'Open Soiled receipt.',
          after: 'Used trays from the theatre and the wards are received here, and counted against the count sheet.',
          wait: { role: 'heading', name: 'Soiled receipt' },
        },
        {
          click: { role: 'button', name: 'Receive trays' },
          say: 'The theatre sends back its used trays. Click Receive trays.',
          wait: { role: 'dialog', name: 'Receive soiled trays' },
        },
        {
          select: [{ label: 'From department' }, 'Operation Theatre'],
          native: true,
          say: 'Choose the department that sends the trays. Here, the Operation Theatre.',
          zoom: 1.3,
        },
        {
          fill: [{ label: 'Handed over by' }, 'OT nurse Latha'],
          say: 'Write who hands them over.',
          zoom: 1.3,
        },
        {
          select: [{ label: 'Add a tray' }, 'Minor surgical tray'],
          native: true,
          say: 'Choose the instrument set of the tray. Here, a Minor surgical tray.',
          after: 'HC360 lists every instrument of the set, with the number it expects back.',
          wait: { role: 'table', name: 'Count sheet of Minor surgical tray' },
          zoom: 1.2,
        },
        {
          show: { role: 'table', name: 'Count sheet of Minor surgical tray' },
          say: 'Count the instruments on the tray against this sheet. A missing or damaged instrument is entered here, and HC360 raises an alert.',
          zoom: 1.2,
        },
        {
          click: { within: { role: 'dialog', name: 'Receive soiled trays' }, role: 'button', name: 'Receive trays' },
          say: 'When the count matches, click Receive trays.',
          after: 'The receipt is listed, with the department and the result of the count.',
          wait: { role: 'row', name: /Operation Theatre/ },
        },
      ],
    },
    {
      title: 'Run a sterilizer load and release it',
      steps: [
        {
          nav: 'Wash cycles',
          say: 'Trays are washed before they are packed. Open Wash cycles.',
          after: 'This morning’s wash of two minor surgical trays has passed, so they can be packed.',
          wait: { role: 'row', name: /Passed/ },
        },
        {
          nav: 'Sterilizer loads',
          say: 'Then the trays are packed and loaded. Open Sterilizer loads.',
          wait: { role: 'heading', name: 'Sterilizer loads' },
        },
        {
          click: { role: 'button', name: 'New load' },
          say: 'Click New load.',
          wait: { role: 'dialog', name: 'New sterilizer load' },
        },
        {
          fill: [{ label: 'Sterilizer' }, 'Autoclave 1'],
          say: 'Name the sterilizer.',
          zoom: 1.3,
        },
        {
          fill: [{ label: 'Programme / cycle' }, '134 °C wrapped'],
          say: 'Write the programme.',
          zoom: 1.3,
        },
        {
          select: [{ label: 'Set 1' }, 'Minor surgical tray'],
          native: true,
          say: 'Choose the set that goes into the load.',
          zoom: 1.3,
        },
        {
          select: [{ label: 'Wash cycle of set 1' }, 'Washed in '],
          native: true,
          say: 'Choose the wash cycle the trays came from. Only a wash that passed can be chosen.',
          zoom: 1.3,
        },
        {
          fill: [{ label: 'Packs of set 1' }, '2'],
          say: 'And the number of packs. Here, two.',
          zoom: 1.3,
        },
        {
          click: { role: 'button', name: /^Create load/ },
          say: 'Click Create load.',
          after: 'Each pack gets its own number. The load is Loaded, and has not started.',
          wait: { role: 'button', name: 'Start cycle' },
        },
        {
          click: { role: 'button', name: 'Start cycle' },
          say: 'Close the door and start the cycle on the sterilizer. Then click Start cycle.',
          wait: { role: 'button', name: 'Record cycle result' },
        },
        {
          click: { role: 'button', name: 'Record cycle result' },
          say: 'When the cycle ends, read the printout. Click Record cycle result.',
          wait: { role: 'dialog', name: /^Record cycle/ },
        },
        {
          fill: [{ label: 'Temperature (°C)' }, '134'],
          say: 'Type the temperature from the printout.',
          zoom: 1.3,
        },
        {
          fill: [{ label: 'Exposure (minutes)' }, '4'],
          say: 'And the exposure time.',
          zoom: 1.3,
        },
        {
          click: { role: 'radio', name: 'Passed (colour changed)' },
          say: 'Read the chemical indicator in the pack. Here, its colour changed.',
          zoom: 1.3,
        },
        {
          click: { within: { role: 'dialog', name: /^Record cycle/ }, role: 'button', name: 'Record cycle' },
          say: 'Click Record cycle. A cycle outside the safe range, or a failed indicator, fails the load at once.',
          after: 'The cycle is complete, and the packs can be released.',
          wait: { role: 'button', name: 'Release packs' },
        },
        {
          click: { role: 'button', name: 'Release packs' },
          say: 'Click Release packs.',
          wait: { text: /^Release load /, exact: false },
        },
        {
          click: { role: 'button', name: 'Release packs', last: true },
          say: 'Confirm. The packs go on the shelf, with the use-by date.',
          wait: { text: 'On shelf' },
        },
        {
          click: { role: 'button', name: 'Biological indicator' },
          say: 'The biological indicator is read after incubation. Click Biological indicator to record its result.',
          wait: { role: 'dialog', name: /^Biological indicator for/ },
        },
        {
          click: { role: 'radio', name: 'Passed (no growth)' },
          say: 'Here, no growth. Choose Passed.',
          zoom: 1.3,
        },
        {
          click: { within: { role: 'dialog', name: /^Biological indicator for/ }, role: 'button', name: 'Record result' },
          say: 'Click Record result. If it failed, HC360 would recall every pack of this load and alert the departments that hold them.',
          after: 'The load now shows Biological passed.',
          wait: { text: 'Biological passed' },
        },
      ],
    },
    {
      title: 'Issue sterile packs',
      steps: [
        {
          nav: 'Issue packs',
          say: 'Open Issue packs.',
          wait: { role: 'heading', name: 'Issue sterile packs' },
        },
        {
          select: [{ label: 'Department' }, 'Operation Theatre'],
          native: true,
          say: 'Choose the department that collects the packs.',
          zoom: 1.3,
        },
        {
          fill: [{ label: 'Received by' }, 'OT nurse Latha'],
          say: 'Write the name of the person who collects them.',
          zoom: 1.3,
        },
        {
          click: { role: 'button', name: /^Add / },
          say: 'Scan the label of the pack, or pick the pack that expires first. Click Add.',
          after: 'The pack is on the issue list. Expired and recalled packs cannot be issued.',
          wait: { role: 'button', name: /^Remove / },
        },
        {
          click: { role: 'button', name: /^Issue \d+ packs/ },
          say: 'Click Issue.',
          after: 'HC360 records which department has the pack, so that it can be found if the load is ever recalled.',
          wait: { role: 'dialog', name: 'Packs issued' },
        },
      ],
    },
  ],
  recap: [
    'Receive used trays against the count sheet. A missing or damaged instrument raises an alert.',
    'Record the cycle from the printout and the chemical indicator, then release the packs. Record the biological indicator when it is read.',
    'Issue packs to a department, so that a failed load can be traced to where its packs went.',
  ],
  check: [
    {
      q: 'What happens to a load when its chemical indicator fails?',
      a: 'The load fails at once, and its packs go back for reprocessing.',
    },
    {
      q: 'Why does HC360 record which department received each pack?',
      a: 'If a load is recalled, HC360 can find every pack of that load, and where it went.',
    },
  ],
  next: 'Biomedical engineer: breakdown tickets, calibration and maintenance',
};
