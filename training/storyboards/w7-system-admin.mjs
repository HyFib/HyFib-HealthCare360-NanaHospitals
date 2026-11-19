// Wave 7 (insights): the system administrator's work. Adding a user, who waits for a second administrator; the roles
// and the audit log; system health, devices, backups and features. Recorded as the system administrator ("admin") at
// 11:10 am. At the hospital, administration works only in HC360 Desktop on an enrolled computer and the administrator
// signs in with two-step verification (docs/security/ROLES.md and SECURITY.md); the training server relaxes both.

const nav = { role: 'navigation', name: 'Main navigation' };
const addUser = { role: 'dialog', name: 'Add user' };

export default {
  id: 'w7-system-admin',
  title: 'System administrator: users, the audit log and system health',
  role: 'SuperAdmin',
  roleLabel: 'System administrator',
  user: 'admin',
  wave: 7,
  audience: 'For the system administrators of Nana Hospitals.',
  intro:
    'The system administrator looks after who may use HC360, and whether the server is well. At the hospital, administration works only in HC360 Desktop, on a computer that IT enrolled, and administrators sign in with two-step verification. The training server relaxes both, so this video uses a browser.',
  objectives: [
    'Add a user, and see that the new access waits for a second administrator.',
    'Read the roles and the audit log.',
    'Check system health, devices, backups and features.',
  ],
  modules: ['admin', 'approvals', 'masters'],
  covers: ['/admin/users', '/admin/roles', '/admin/audit', '/admin/system', '/admin/devices', '/admin/backups', '/setup/features'],
  scenes: [
    {
      title: 'Add a user',
      steps: [
        {
          click: { within: nav, role: 'button', name: 'Administration' },
          say: 'Open Administration from the menu.',
        },
        {
          nav: 'Users',
          say: 'Then choose Users.',
          after: 'Every staff account is listed, with its roles, its two-step verification and its last sign-in.',
          wait: { role: 'heading', name: 'Users' },
        },
        {
          click: { role: 'button', name: 'Add user' },
          say: 'A new billing executive joins on Monday. Click Add user.',
          wait: addUser,
        },
        {
          fill: [{ within: addUser, label: 'User name' }, 'kavitha.s'],
          say: 'Type the user name. It is what the new user types to sign in.',
          zoom: 1.3,
        },
        {
          fill: [{ within: addUser, label: 'Full name' }, 'Kavitha Sundaram'],
          say: 'Then the full name, as screens and prints show it.',
          zoom: 1.3,
        },
        {
          fill: [{ within: addUser, label: 'Mobile' }, '9000003001'],
          say: 'And the mobile number.',
          zoom: 1.3,
        },
        {
          check: { within: addUser, role: 'checkbox', name: 'Billing' },
          say: 'Choose the role. Give a role that fits the job, and nothing more.',
          zoom: 1.3,
        },
        {
          fill: [{ within: addUser, label: 'Reason, for the approver' }, 'New billing executive for the OP counters, from Monday'],
          say: 'Write the reason. The second administrator reads it before deciding.',
          zoom: 1.3,
        },
        {
          click: { within: addUser, role: 'button', name: 'Create user' },
          say: 'Click Create user.',
          after:
            'HC360 shows a temporary password, once. Give it to the new user in person. It must be changed at the first sign-in. The role waits for another administrator: until it is approved, the account holds no role.',
          wait: { role: 'heading', name: /^Temporary password for Kavitha Sundaram/ },
          zoom: 1.2,
        },
        {
          click: { role: 'button', name: 'Done' },
          say: 'Click Done.',
          wait: { role: 'heading', name: 'Users' },
        },
        {
          nav: 'Approvals',
          say: 'Open the Approvals screen to see the request.',
          wait: { role: 'tab', name: 'Asked by me' },
        },
        {
          click: { role: 'tab', name: 'Asked by me' },
          say: 'Open Asked by me.',
          after: 'The new access is waiting for approval. You cannot approve it yourself: a second administrator does. The hospital administrator’s video shows how.',
          wait: { role: 'row', name: /Kavitha Sundaram/ },
        },
      ],
    },
    {
      title: 'Roles and the audit log',
      steps: [
        {
          nav: 'Roles and permissions',
          say: 'Open Roles and permissions.',
          after: 'A role is a named set of permissions. Give users roles, not single permissions.',
          wait: { role: 'heading', name: 'Roles and permissions' },
        },
        {
          show: { role: 'row', name: /^Billing Built-in/ },
          say: 'The Billing role has 25 permissions, and one user holds it. The switch In use turns the whole role off, and takes what it gives from everyone who holds it.',
          zoom: 1.3,
        },
        {
          nav: 'Audit log',
          say: 'Open the Audit log.',
          after: 'Every sign-in, record access and change is here. Entries cannot be edited or deleted.',
          wait: { role: 'table', name: 'Audit events' },
        },
        {
          fill: [{ label: 'Search' }, 'kavitha'],
          say: 'The search finds a user, an action or a summary. Type kavitha to find the entry for the account you just added.',
          wait: { role: 'cell', name: /Created user kavitha/ },
          result: { role: 'row', name: /Created user kavitha/ },
        },
        {
          show: { role: 'button', name: 'Verify integrity' },
          say: 'Verify integrity checks that no entry was changed. Auditors can also have the log exported.',
          zoom: 1.5,
        },
      ],
    },
    {
      title: 'System health, devices and backups',
      steps: [
        {
          nav: 'System health',
          say: 'Open System health.',
          after: 'The page lists what needs attention, and the state of the database, the storage and the security certificates.',
          wait: { role: 'heading', name: 'System health' },
        },
        {
          show: { role: 'group', name: /^Backup recovery key missing/ },
          say: 'Read the warnings at the top. Here, nobody holds the recovery key for the backups. Make one, so that the backups can be restored on new hardware.',
          zoom: 1.3,
        },
        {
          nav: 'Devices',
          say: 'Open Devices.',
          after: 'No computer is enrolled on the training server. At the hospital, IT enrols each computer here. Administration works only on an enrolled computer.',
          wait: { role: 'heading', name: 'Devices' },
        },
        {
          nav: 'Backups',
          say: 'Open Backups.',
          wait: { role: 'heading', name: 'Backups' },
        },
        {
          show: { role: 'button', name: 'Back up now' },
          say: 'Back up now makes an encrypted copy of the database. Keep one copy on a second disk, and one off site.',
          zoom: 1.5,
        },
        {
          click: { within: nav, role: 'button', name: 'Hospital setup' },
          say: 'Features are under Hospital setup. Open it.',
        },
        {
          nav: 'Features',
          say: 'Choose Features.',
          after: 'Three parts of HC360 can be turned on or off for the whole hospital: the patient portal, video consultations and ABDM health records.',
          wait: { role: 'heading', name: 'Features' },
        },
        {
          show: { role: 'switch', name: 'Patient portal' },
          say: 'A change applies at once. If you turn the patient portal off, patients cannot sign in. Think before you do it.',
          zoom: 1.4,
        },
      ],
    },
  ],
  recap: [
    'A new user’s roles wait for a second administrator. Give each person only the role that fits the job.',
    'The audit log records every sign-in, access and change. Nobody can edit or delete an entry.',
    'Check System health regularly. At the hospital, administration works only in HC360 Desktop on an enrolled computer.',
  ],
  check: [
    {
      q: 'You add a new user with the Billing role. Can the new user open billing screens at the first sign-in?',
      a: 'No. The role waits for another administrator to approve it. Until then the account holds no role, so no billing screen opens.',
    },
    {
      q: 'Why does administration work only in HC360 Desktop at the hospital?',
      a: 'Administration is limited to computers that IT enrolled. A browser, a kiosk or a queue display cannot administer HC360.',
    },
  ],
  next: 'Hospital administrator: the dashboard, the bed board and approvals',
};
