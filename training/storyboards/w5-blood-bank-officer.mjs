import { writeFileSync } from 'node:fs';
const nav = { role: 'navigation', name: 'Main navigation' };
const dump = (name) => async ({ page }) => writeFileSync(`/tmp/claude-0/-home-user-HyFib-HealthCare360-NanaHospitals/4cf56889-54f3-5e4e-8287-5c222ea81477/scratchpad/${name}.txt`, await page.locator('body').ariaSnapshot());
export default {
  id: 'w5-blood-bank-officer', title: 'Blood bank officer', role: 'BloodBankOfficer', roleLabel: 'Blood bank officer', user: 'bloodbank', wave: 5,
  audience: 'x', intro: 'x', objectives: ['a', 'b'], modules: ['blood-bank'], covers: ['/blood-bank'],
  setup: async ({ api }) => {
    const { firstSignIn } = await import('../../src/web/demo/lib/api.mjs');
    const admin = await api('admin');
    const roles = await admin.get('/api/v1/admin/roles');
    const facilityId = admin.me.facilityId ?? admin.me.defaultFacilityId;
    const temporary = `Trn-first-${crypto.randomUUID().slice(0, 12)}`;
    const created = await admin.post('/api/v1/admin/users', { userName: 'bloodbank2', displayName: 'Kavitha Raman', email: null, phone: null, roleIds: [roles.find((r) => r.name === 'BloodBankOfficer').id], facilityIds: [facilityId], defaultFacilityId: facilityId, practitionerId: null, temporaryPassword: temporary });
    for (const id of [created.approvalId, created.practitionerLinkApprovalId]) {
      if (!id) continue;
      const checker = await api('hospitaladmin');
      const request = await checker.get(`/api/v1/approvals/${id}`);
      await checker.post(`/api/v1/approvals/${id}/approve`, { note: null, version: request.version });
    }
    await firstSignIn('bloodbank2', temporary);
    const second = await api('bloodbank2');
    const donor = await second.post('/api/v1/blood-bank/donors', { firstName: 'Murugan', lastName: 'Selvam', gender: 'Male', dateOfBirth: '1990-03-12', bloodGroup: 'OPositive', phone: '9876501234', email: null, address: { line1: null, line2: null, city: 'Theni', district: null, state: 'Tamil Nadu', pinCode: null, country: 'India' }, occupation: null, consentToNotify: true, notes: null });
    const questions = await second.get('/api/v1/blood-bank/donor-questions');
    const screening = await second.post(`/api/v1/blood-bank/donors/${donor.id}/screenings`, { haemoglobinGdl: 14.2, weightKg: 70, systolic: 120, diastolic: 80, pulse: 72, temperatureC: 36.8, answers: questions.filter((q) => !q.onlyFor || q.onlyFor === 'Male').map((q) => ({ key: q.key, yes: false })) });
    const donation = await second.post(`/api/v1/blood-bank/donors/${donor.id}/donations`, { screeningId: screening.id, bagNumber: null, bagType: 'Triple', volumeMl: 450, type: 'Voluntary', replacementForPatientId: null, collectedAt: null, donorReaction: null, notes: null });
    await second.post(`/api/v1/blood-bank/donations/${donation.id}/components`, { components: [{ type: 'PRBC', volumeMl: null, storageLocation: 'Blood bank refrigerator 1' }] });
    const detail = await second.get(`/api/v1/blood-bank/donations/${donation.id}`);
    await second.put(`/api/v1/blood-bank/donations/${donation.id}/lab-results`, { bloodGroup: 'OPositive', hiv: 'NonReactive', hbsAg: 'NonReactive', hcv: 'NonReactive', syphilis: 'NonReactive', malaria: 'NonReactive', version: detail.version });
    const doctor = await api('doctor');
    const fo = await api('frontoffice');
    const found = await fo.get('/api/v1/patients?search=Fatima%20Begum&pageSize=5');
    const patient = (found.items ?? found).find((p) => p.fullName.includes('Fatima'));
    const admissions = await doctor.get(`/api/v1/inpatient/admissions?pageSize=10`);
    const admission = (admissions.items ?? admissions).find((a) => a.patientId === patient.id);
    const request = await doctor.post('/api/v1/blood-bank/requests', { patientId: patient.id, admissionId: admission.id, visitId: null, componentType: 'PRBC', units: 1, urgency: 'Urgent', indication: 'Anaemia with pneumonia', requiredBy: null, haemoglobinGdl: 7.4, plateletCount: null });
    await second.post(`/api/v1/blood-bank/requests/${request.id}/grouping`, { bloodGroup: 'OPositive', newSample: false });
  },
  scenes: [{ title: 'x', steps: [
    { click: { within: nav, role: 'button', name: 'Blood bank' }, say: 'Open.' },
    { nav: 'Blood stock', say: 'Open.', wait: { role: 'heading', name: 'Blood stock' } },
    { nav: 'Donations', say: 'Open.', wait: { role: 'row', name: /Murugan Selvam/ }, after_do: dump('b-b') },
    { click: { role: 'row', name: /Murugan Selvam/ }, say: 'Open.', wait: { text: 'Awaiting verification' }, after_do: dump('b-c') },
    { click: { role: 'button', name: 'Verify results' }, say: 'Verify.', wait: { role: 'dialog' }, after_do: dump('b-d') },
    { click: { role: 'button', name: 'Verify and release' }, say: 'Confirm.', after_do: dump('b-e') },
    { nav: 'Blood requests', say: 'Open.', wait: { role: 'row', name: /Fatima Begum/ }, after_do: dump('b-f') },
    { click: { role: 'row', name: /Fatima Begum/ }, say: 'Open.', wait: { role: 'button', name: 'Record cross-match' }, after_do: dump('b-g') },
    { click: { role: 'button', name: 'Record cross-match' }, say: 'Open.', wait: { role: 'dialog', name: /Cross-match unit/ }, after_do: dump('b-h') },
    { click: { role: 'radio', name: 'Compatible' }, say: 'C.' },
    { click: { role: 'button', name: 'Save cross-match' }, say: 'Save.', wait: { text: 'Reserved' }, after_do: dump('b-i') },
  ] }],
  recap: ['a','b','c'], check: [], next: 'x',
};
