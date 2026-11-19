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
  scenes: [
    { title: 'The blood bank this morning', steps: [
      { click: { within: nav, role: 'button', name: 'Blood bank' }, say: 'Open Blood bank from the menu.' },
      { nav: 'Blood stock', say: 'Open Blood stock.', after: 'Every group shows no units yet. One request is open, and one unit is in quarantine.', wait: { role: 'button', name: /^Open requests/ }, result: { role: 'button', name: /^Open requests/ } },
      { show: { role: 'button', name: /^In quarantine/ }, say: 'A unit in quarantine is never given to a patient. It waits for its group and its TTI results.', zoom: 1.5 },
    ] },
    { title: 'Release a donation to stock', steps: [
      { nav: 'Donations', say: 'Open Donations.', after: 'One bag is listed, from the donor Murugan Selvam.', wait: { role: 'row', name: /Murugan Selvam/ }, result: { role: 'row', name: /Murugan Selvam/ } },
      { click: { role: 'row', name: /Murugan Selvam/ }, say: 'Open the bag.', wait: { text: 'Awaiting verification' } },
      { show: { text: 'Awaiting verification' }, say: 'Kavitha Raman entered the results. The units stay in quarantine until a second officer checks them.', zoom: 1.4 },
      { show: { role: 'heading', name: 'Grouping and TTI screening' }, say: 'Compare the group and every marker with the worksheet and the analyser’s printout. Here the group is O+ and all five markers are Non-reactive.' },
      { click: { role: 'button', name: 'Verify results' }, say: 'You did not enter these results, so you may verify them. Click Verify results.', wait: { role: 'button', name: 'Verify and release' } },
      { click: { role: 'button', name: 'Verify and release' }, say: 'Verify only after you have checked. The unit leaves quarantine. Click Verify and release.', after: 'The results now name you as the verifier, and the red cell unit NHBB26000001-R is Available.', wait: { role: 'row', name: /NHBB26000001-R.*Available/ }, result: { role: 'row', name: /NHBB26000001-R.*Available/ } },
      { click: { role: 'button', name: 'Open donor' }, say: 'Open the donor.', wait: { role: 'cell', name: 'Eligible to donate' } },
      { show: { role: 'row', name: /Eligible to donate/ }, say: 'Before a bag is collected, HC360 needs a screening that found the donor eligible. His haemoglobin, weight, blood pressure, pulse and temperature were checked.', zoom: 1.3 },
    ] },
    { title: 'Cross-match and issue for a patient', steps: [
      { nav: 'Blood requests', say: 'Open Blood requests.', after: 'Dr. Arjun Mehta has asked for one unit of PRBC for Fatima Begum, urgently. Her sample is already grouped.', wait: { role: 'row', name: /Fatima Begum/ }, result: { role: 'row', name: /Fatima Begum/ } },
      { click: { role: 'row', name: /Fatima Begum/ }, say: 'Open her request.', wait: { role: 'button', name: 'Record cross-match' } },
      { show: { role: 'heading', name: /^Patient.s blood group/ }, say: 'Kavitha Raman grouped Fatima’s sample as O+. HC360 never uses the group recorded at registration for a transfusion.', zoom: 1.2 },
      { click: { role: 'button', name: 'Record cross-match' }, say: 'The compatible unit that expires first is listed. Click Record cross-match.', wait: { role: 'dialog', name: /Cross-match unit/ } },
      { click: { role: 'radio', name: 'Compatible' }, say: 'Match the unit with her sample and record the result. Choose Compatible.', zoom: 1.3 },
      { click: { role: 'button', name: 'Save cross-match' }, say: 'Click Save cross-match. A compatible result reserves the unit for her for 72 hours.', after: 'The unit is Reserved for this request, and the cross-match is listed.', wait: { text: 'Reserved' } },
      { click: { role: 'button', name: 'NHBB26000001-R' }, say: 'To issue, scan the bag in your hand. Here, click the unit number.', after: 'HC360 checks the unit again: All checks passed for Fatima Begum.', wait: { text: /All checks passed/, exact: false } },
      { fill: [{ label: 'Handed to', exact: false }, 'Deepa Raman, ward nurse'], say: 'Write who collects the unit, and check their identity card.', zoom: 1.3 },
      { click: { role: 'button', name: /Issue unit and print slip/ }, say: 'One officer may not group, cross-match and issue the same unit. Kavitha Raman grouped it and you cross-matched it, so you may issue it. Click Issue unit and print slip.', wait: { role: 'button', name: /^Print issue slip/ }, after_do: dump('b-issue') },
      { show: { role: 'main' }, say: 'x', after_do: dump('b-issue2') },
    ] },
  ],
  recap: ['a','b','c'], check: [], next: 'x',
};
