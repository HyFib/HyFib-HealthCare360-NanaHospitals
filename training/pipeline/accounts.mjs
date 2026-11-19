// Gives each built-in role that the demo morning has no account for an account of its own on the training server,
// so every role's video is recorded signed in as that role and shows only what that role is given. Run once after
// src/web/demo/story.mjs, before the snapshot. Access is given by two people, as at the hospital: the system
// administrator asks, the hospital administrator approves; then each account's first sign-in sets its password.
//
//   HC360_DEMO_PASSWORD=… NODE_EXTRA_CA_CERTS=… node training/pipeline/accounts.mjs
//
// Every name is fictional.

import { dirname, join, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const { Api, firstSignIn } = await import(pathToFileURL(join(ROOT, 'src/web/demo/lib/api.mjs')).href);

/** user name, display name, roles: one per built-in role the demo leaves without an account. */
export const TRAINING_ACCOUNTS = [
  ['cashier', 'Revathi Selvam', ['Cashier']],
  ['insurance', 'Karthikeyan Murugan', ['InsuranceDesk']],
  ['cardiotech', 'Balaji Ramasamy', ['CardiologyTechnician']],
  ['cardiologist', 'Dr. Ramesh Venkatesan', ['Cardiologist', 'Doctor']],
  ['storekeeper', 'Selvaraj Pandian', ['StoreKeeper']],
  ['purchase', 'Malathi Sekar', ['PurchaseOfficer']],
  ['mrd', 'Gowri Shankar', ['MrdOfficer']],
  ['theatre', 'Jayanthi Kannan', ['OtCoordinator']],
  ['dialysis', 'Prakash Arumugam', ['DialysisTechnician']],
  ['physio', 'Divya Natarajan', ['Physiotherapist']],
  ['bloodbank', 'Dr. Sangeetha Iyer', ['BloodBankOfficer']],
  ['dietitian', 'Nandhini Ravi', ['Dietitian']],
  ['canteen', 'Muthu Kumar', ['CanteenManager']],
  ['cssd', 'Saravanan Thangavel', ['CssdTechnician']],
  ['biomed', 'Vignesh Raja', ['BiomedicalEngineer']],
  ['hrmanager', 'Kalaiselvi Murthy', ['HrManager']],
  ['crm', 'Sathish Kumar', ['CrmExecutive']],
  ['quality', 'Dr. Meera Krishnan', ['QualityManager']],
  ['accountant', 'Venkatesh Balan', ['Accountant']],
  ['mis', 'Pooja Sundar', ['MisAnalyst']],
];

async function approve(approvalId) {
  if (!approvalId) return;
  const checker = await Api.as('hospitaladmin');
  const request = await checker.get(`/api/v1/approvals/${approvalId}`);
  await checker.post(`/api/v1/approvals/${approvalId}/approve`, { note: null, version: request.version });
}

const admin = await Api.as('admin');
const roles = await admin.get('/api/v1/admin/roles');
const facilityId = admin.me.facilityId ?? admin.me.defaultFacilityId;
const users = await admin.get('/api/v1/admin/users?pageSize=200');
const existing = new Set((users.items ?? users).map((u) => u.userName));

for (const [userName, displayName, roleNames] of TRAINING_ACCOUNTS) {
  if (existing.has(userName)) {
    console.log(`= ${userName} exists`);
    continue;
  }
  const roleIds = roleNames.map((name) => {
    const role = roles.find((r) => r.name === name);
    if (!role) throw new Error(`No role ${name}`);
    return role.id;
  });
  const temporary = `Trn-first-${crypto.randomUUID().slice(0, 12)}`;
  const created = await admin.post('/api/v1/admin/users', {
    userName,
    displayName,
    email: null,
    phone: null,
    roleIds,
    facilityIds: [facilityId],
    defaultFacilityId: facilityId,
    practitionerId: null,
    temporaryPassword: temporary,
  });
  await approve(created.approvalId);
  await approve(created.practitionerLinkApprovalId);
  await firstSignIn(userName, temporary);
  console.log(`+ ${userName} (${displayName}): ${roleNames.join(', ')}`);
}
