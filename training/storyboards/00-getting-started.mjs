// Shared video 1 of 3, for every member of staff: signing in, the home page, the menu, search, the bell, the
// connection light, the account menu (lock, appearance, sign out). Recorded as the front office executive
// (Anitha Krishnan): what she sees is what every role sees, with a menu of its own.

const nav = { role: 'navigation', name: 'Main navigation' };

export default {
  id: '00-getting-started',
  title: 'Getting started with HC360',
  role: '*',
  roleLabel: 'All staff',
  user: 'frontoffice',
  signIn: 'on-screen',
  wave: 0,
  audience: 'For every member of staff at Nana Hospitals, before your first day on HC360.',
  intro:
    'Welcome to HC360, the hospital system of Nana Hospitals. This first video is for every member of staff. It shows how to sign in, how to find your way around, and how to keep patient information safe.',
  objectives: [
    'Sign in with your own user name and password.',
    'Find your work from the home page, the menu and the search box.',
    'Read the bell and the connection light.',
    'Lock the screen when you leave the desk, and sign out at the end of your shift.',
  ],
  modules: ['shell'],
  covers: ['/login', '/', '/front-office/queue'],
  scenes: [
    {
      title: 'Sign in',
      steps: [
        {
          show: { role: 'heading', name: 'Sign in' },
          say: 'HC360 opens on the sign-in screen. Each member of staff has an account of their own.',
        },
        {
          fill: [{ label: 'User name' }, 'frontoffice'],
          say: 'Type your user name. The IT help desk gives it to you on your first day.',
          zoom: 1.5,
        },
        {
          fill: [{ label: 'Password' }, () => process.env.HC360_DEMO_PASSWORD],
          secret: true,
          say: 'Then type your password. It has at least twelve characters. Never share it, and never write it down. Everything you do in HC360 is recorded against your account.',
          zoom: 1.5,
        },
        {
          show: { text: 'Forgot your password? Ask the IT help desk to reset it.' },
          say: 'After five wrong passwords in a row, the account locks for fifteen minutes. If you forget your password, the IT help desk resets it for you.',
          zoom: 1.5,
        },
        {
          click: { role: 'button', name: 'Sign in' },
          say: 'Click Sign in.',
          after:
            'Some roles also need two-step verification. If yours does, HC360 now asks for the six-digit code from the authenticator app on your phone.',
          wait: { role: 'heading', level: 1 },
        },
      ],
    },
    {
      title: 'Your home page',
      steps: [
        {
          show: { role: 'heading', level: 1 },
          say: 'Your home page greets you by name, with today’s date and the name of the hospital.',
        },
        {
          show: { role: 'navigation', name: 'Quick actions' },
          say: 'Quick actions are the jobs you do most often. One click opens each one.',
        },
        {
          show: { role: 'button', name: /^Waiting now/ },
          say: 'Below them are today’s numbers for your work. Here, two patients are waiting for their token to be called.',
          zoom: 1.4,
        },
      ],
    },
    {
      title: 'The menu and the search box',
      steps: [
        {
          show: nav,
          say: 'The menu is on the left. It is grouped by area of the hospital, in the order a patient meets them.',
        },
        {
          show: nav,
          say: 'You see only what your role may use. A doctor’s menu and a cashier’s menu are different. If you need something that is not in your menu, ask your department in-charge.',
        },
        {
          click: { within: nav, role: 'button', name: 'Front office' },
          say: 'Click an area to open it.',
          after: 'Its screens appear below it.',
        },
        {
          nav: 'Queue',
          say: 'Then click the screen you need.',
          after: 'The screen opens on the right. The menu stays where it is.',
          wait: { role: 'heading', level: 1 },
        },
        {
          press: 'Control+k',
          say: 'To go anywhere quickly, press Control K.',
          after: 'The search box opens.',
          wait: { role: 'dialog', name: 'Search' },
        },
        {
          fill: [{ within: { role: 'dialog', name: 'Search' }, role: 'combobox' }, 'Rajendran'],
          say: 'Type a few letters of a screen’s name, or of a patient’s name, mobile number or UHID.',
          after:
            'Matching patients and screens appear as you type. Choose one with the arrow keys and press Enter, or click it.',
          wait: { role: 'option', name: /Rajendran/ },
          zoom: 1.3,
        },
        {
          press: 'Escape',
          say: 'Press Escape to close the search box without opening anything.',
        },
      ],
    },
    {
      title: 'The bell and the connection light',
      steps: [
        {
          show: { role: 'button', name: /^Notifications/ },
          say: 'The bell shows alerts meant for you, such as a critical result, or a request waiting for your approval. A number on the bell means unread alerts.',
          zoom: 1.8,
        },
        {
          show: { role: 'button', name: /^Connection:/ },
          say: 'This light shows the connection to the HC360 server. Green means connected. Amber means the connection is weak.',
          zoom: 1.8,
        },
        {
          show: { role: 'button', name: /^Connection:/ },
          say: 'If the connection is lost, the light turns red and a banner appears. Do not close the screen. What you are typing is kept on this computer, and HC360 reconnects on its own.',
          zoom: 1.8,
        },
      ],
    },
    {
      title: 'Lock the screen, and sign out',
      steps: [
        {
          click: { role: 'button', name: /^Account menu for/ },
          say: 'Your initials are at the top right. Click them to open your account menu.',
          wait: { role: 'menu' },
          zoom: 1.4,
        },
        {
          show: { role: 'menuitem', name: /^Lock screen/ },
          say: 'When you leave the desk, even for a minute, choose Lock screen, or press Control, Shift and L. HC360 also locks by itself after ten minutes without use, so that nobody else can read patient information on your screen.',
          zoom: 1.4,
        },
        {
          show: { role: 'menuitem', name: 'My account and security' },
          say: 'My account and security is where you change your password and set up two-step verification.',
          zoom: 1.4,
        },
        {
          show: { role: 'menuitemradio', name: 'High contrast' },
          say: 'Under Appearance, you can choose a dark screen, or high contrast if you find the text hard to read.',
          zoom: 1.4,
        },
        {
          show: { role: 'menuitem', name: 'Sign out' },
          say: 'At the end of your shift, choose Sign out.',
          zoom: 1.4,
        },
      ],
    },
  ],
  recap: [
    'Sign in with your own user name and password, and never share them.',
    'Your menu shows what your role may use. Control K finds any screen or patient.',
    'The bell has your alerts. The light shows the connection: if it is lost, keep the screen open.',
    'Lock the screen when you leave the desk, and sign out at the end of your shift.',
  ],
  check: [
    {
      q: 'A colleague asks to use your account for a few minutes. What do you do?',
      a: 'Say no, politely. Everything in HC360 is recorded against the account that did it. Your colleague signs in with their own account.',
    },
    {
      q: 'The connection light turns red while you are typing. What should you do?',
      a: 'Keep the screen open. What you typed is kept on this computer, and HC360 reconnects on its own. Save again when the light is green.',
    },
  ],
  next: 'Tools every member of staff uses: My HR, incident reports and approvals',
};
