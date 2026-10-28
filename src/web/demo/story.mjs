// The demo's morning at the hospital, made through the real API by the staff who do each step (the same calls as the
// integration tests' journeys, tests/HyFib.HealthCare360.IntegrationTests/*JourneyTests.cs). Run it once against a
// server that has just been seeded with demonstration data at 9:00 am (README.md): patients then stand at every stage
// the demo shows (seen, tested, waiting to pay, at the pharmacy, admitted, discharged, in casualty, with the doctor).
//
//   HC360_DEMO_PASSWORD=… NODE_EXTRA_CA_CERTS=hc360-ca.pem DEMO_CLOCK_FILE=clock.txt node demo/story.mjs [story.json]
//
// DEMO_CLOCK_FILE is the file the demo server and its database take their time from (libfaketime's
// FAKETIME_TIMESTAMP_FILE): each step moves the hospital's clock to its time of the morning before it happens, and at
// the end the clock stops at 11:10 am, the moment the demo shows (README.md).
//
// Every person is fictional, and every phone number is in the 90000 0xxxx series the load tests use.

import { writeFileSync } from 'node:fs';
import { addDays, Api, ApiError, askServerTime, firstSignIn, istAt, istDate, serverNow } from './lib/api.mjs';

const outcome = { recordedAt: null, today: null, demoClock: null, accounts: [], staff: [], scenes: [] };
let phoneSequence = 1000;
const nextPhone = () => `90000${String(++phoneSequence).padStart(5, '0')}`;

async function scene(name, run) {
  const started = Date.now();
  try {
    const result = (await run()) ?? {};
    outcome.scenes.push({ name, at: hhmm(serverNow()), ok: true, ...result });
    console.log(`✓ ${hhmm(serverNow())} ${name} (${((Date.now() - started) / 1000).toFixed(1)} s)`);
  } catch (error) {
    const fields = error.problem?.errors ? ` ${JSON.stringify(error.problem.errors)}` : '';
    outcome.scenes.push({ name, ok: false, error: `${error.message ?? error}${fields}` });
    console.log(`✗ ${name}: ${error.message ?? error}${fields}`);
  }
}

// ---------------------------------------------------------------------------------------------------------- clock

const CLOCK = process.env.DEMO_CLOCK_FILE;
const INDIA_OFFSET_MS = 330 * 60_000;
const hhmm = (instant) => new Date(instant.getTime() + INDIA_OFFSET_MS).toISOString().slice(11, 16);

/** Waits until the server shows `target`: libfaketime reads the clock file again at most once a second. */
async function settle(target, label) {
  for (let i = 0; i < 40; i++) {
    await new Promise((resolve) => setTimeout(resolve, 400));
    if (Math.abs((await askServerTime()).getTime() - target) < 5_000) return;
  }
  throw new Error(`The server's clock did not move to ${label}`);
}

/** Moves the hospital's clock on to `time` (IST, "09:15"), and lets it run from there. It never goes back. */
async function at(time) {
  const target = Date.parse(istAt(0, time));
  if (target < (await askServerTime()).getTime() - 60_000) {
    throw new Error(`The story went back in time, to ${time}`);
  }
  const seconds = Math.round((target - Date.now()) / 1000);
  writeFileSync(CLOCK, `${seconds >= 0 ? '+' : ''}${seconds}\n`);
  await settle(target, time);
}

/** Stops the hospital's clock at `time`: the demo is recorded at that moment, so what screens count from it agrees. */
async function stopAt(time) {
  const target = new Date(istAt(0, time));
  writeFileSync(CLOCK, `${target.toISOString().slice(0, 19).replace('T', ' ')}\n`);
  await settle(target.getTime(), time);
  return target;
}

// ---------------------------------------------------------------------------------------------------------- lookups

const cache = new Map();
async function once(key, load) {
  if (!cache.has(key)) {
    cache.set(key, await load());
  }
  return cache.get(key);
}

const service = (api, category, code) =>
  once(`service:${code}`, async () => {
    const list = await api.get(
      `/api/v1/masters/lookups/services?category=${category}&search=${encodeURIComponent(code)}`,
    );
    const found = list.find((s) => s.code === code);
    if (!found) throw new Error(`No ${category} service ${code}`);
    return found;
  });

const drug = (api, search, code) =>
  once(`drug:${code}`, async () => {
    const found = (await api.get(`/api/v1/emr/drugs?search=${encodeURIComponent(search)}`)).find(
      (d) => d.code === code,
    );
    if (!found) throw new Error(`No drug ${code}`);
    return found;
  });

const departments = (api) => once('departments', () => api.get('/api/v1/masters/lookups/departments'));
const department = async (api, code) => (await departments(api)).find((d) => d.code === code);
const wards = (api) => once('wards', () => api.get('/api/v1/inpatient/wards'));

async function theDoctor() {
  const doctor = await Api.as('doctor');
  const practitionerId = doctor.me.practitionerId ?? (await doctor.get('/api/v1/auth/me')).practitionerId;
  return once('doctor', () => doctor.get(`/api/v1/masters/practitioners/${practitionerId}`));
}

async function seededPatient(firstName, lastName) {
  const frontOffice = await Api.as('frontoffice');
  const found = await frontOffice.get(
    `/api/v1/patients?search=${encodeURIComponent(`${firstName} ${lastName}`)}&pageSize=5`,
  );
  const patient = (found.items ?? found).find((p) => p.fullName?.includes(firstName) && p.fullName?.includes(lastName));
  if (!patient) throw new Error(`Seeded patient ${firstName} ${lastName} not found`);
  return patient;
}

// ---------------------------------------------------------------------------------------------------------- staff

/** The specialists the demonstration seed has no accounts for: made by the administrator, as a hospital would. */
async function createSpecialists() {
  const admin = await Api.as('admin');
  const roles = await admin.get('/api/v1/admin/roles');
  const facilityId = admin.me.facilityId ?? admin.me.defaultFacilityId;
  const users = await admin.get('/api/v1/admin/users?pageSize=200');
  const existing = new Set((users.items ?? users).map((u) => u.userName));
  const specialists = [
    ['pathologist', 'Dr. Shalini Rao', 'Pathologist'],
    ['radiologist', 'Dr. Vikram Shetty', 'Radiologist'],
    ['radiographer', 'Arun Prasad', 'Radiographer'],
  ];
  for (const [userName, displayName, roleName] of specialists) {
    if (existing.has(userName)) {
      continue;
    }
    const role = roles.find((r) => r.name === roleName);
    const temporary = `Demo-first-${crypto.randomUUID().slice(0, 12)}`;
    await admin.post('/api/v1/admin/users', {
      userName,
      displayName,
      email: null,
      phone: null,
      roleIds: [role.id],
      facilityIds: [facilityId],
      defaultFacilityId: facilityId,
      practitionerId: null,
      temporaryPassword: temporary,
    });
    await firstSignIn(userName, temporary);
    outcome.accounts.push({ userName, displayName, role: roleName });
  }
}

/**
 * Every member of staff the demo signs in as is a person on the HR roll, so their own HR screens (profile, leave,
 * attendance) are theirs: the seed's front office executive, staff nurse and laboratory technician take the matching
 * accounts, and the others join the roll. The system administrator's account is the IT department's, nobody's own.
 */
const STAFF = [
  { userName: 'frontoffice', name: 'Anitha Krishnan', employee: 'Anitha Krishnan' },
  { userName: 'nurse', name: 'Deepa Raman', employee: 'Deepa Raman' },
  { userName: 'lab', name: 'Suresh Babu', employee: 'Suresh Babu' },
  {
    userName: 'doctor',
    title: 'Dr.',
    first: 'Arjun',
    last: 'Mehta',
    gender: 'Male',
    dept: 'GM',
    job: 'CONS',
    joined: '2019-04-01',
    dob: '1981-02-11',
  },
  {
    userName: 'pharmacist',
    name: 'Lalitha Venkatesh',
    first: 'Lalitha',
    last: 'Venkatesh',
    gender: 'Female',
    dept: 'PHARM',
    job: 'PHARM',
    joined: '2020-08-17',
    dob: '1990-05-23',
  },
  {
    userName: 'billing',
    name: 'Prakash Iyer',
    first: 'Prakash',
    last: 'Iyer',
    gender: 'Male',
    dept: 'BILL',
    job: 'BILL-EXE',
    joined: '2022-01-10',
    dob: '1994-09-30',
  },
  {
    userName: 'pathologist',
    title: 'Dr.',
    first: 'Shalini',
    last: 'Rao',
    gender: 'Female',
    dept: 'LAB',
    job: 'CONS',
    joined: '2018-11-05',
    dob: '1978-12-02',
  },
  {
    userName: 'radiologist',
    title: 'Dr.',
    first: 'Vikram',
    last: 'Shetty',
    gender: 'Male',
    dept: 'RAD',
    job: 'CONS',
    joined: '2017-06-19',
    dob: '1976-07-14',
  },
  {
    userName: 'radiographer',
    first: 'Arun',
    last: 'Prasad',
    gender: 'Male',
    dept: 'RAD',
    job: 'RT',
    joined: '2021-03-01',
    dob: '1993-01-08',
  },
  {
    userName: 'hospitaladmin',
    name: 'Rekha Nair',
    first: 'Rekha',
    last: 'Nair',
    gender: 'Female',
    dept: 'HR',
    job: 'ADMIN',
    joined: '2016-02-01',
    dob: '1975-10-19',
  },
];

async function staffOnTheRoll() {
  const admin = await Api.as('admin');
  const users = new Map(
    ((await admin.get('/api/v1/admin/users?pageSize=200')).items ?? []).map((u) => [u.userName, u]),
  );
  const designations = await admin.get('/api/v1/hr/designations');
  const employees = (await admin.get('/api/v1/hr/employees?pageSize=200')).items ?? [];
  const doctor = await theDoctor();
  for (const person of STAFF) {
    const user = users.get(person.userName);
    // The account carries the person's own name.
    if (person.name && user.displayName !== person.name) {
      const detail = await admin.get(`/api/v1/admin/users/${user.id}`);
      await admin.put(`/api/v1/admin/users/${user.id}`, {
        displayName: person.name,
        email: detail.email ?? null,
        phone: detail.phone ?? null,
        roleIds: detail.roleIds ?? detail.roles.map((r) => r.id),
        facilityIds: detail.facilityIds ?? null,
        defaultFacilityId: detail.defaultFacilityId ?? null,
        practitionerId: detail.practitionerId ?? null,
        isActive: true,
        version: detail.version,
      });
    }
    const listed = person.employee && employees.find((e) => e.fullName === person.employee);
    const existing = listed ? await admin.get(`/api/v1/hr/employees/${listed.id}`) : null;
    const record = existing ?? {
      title: person.title ?? null,
      firstName: person.first,
      middleName: null,
      lastName: person.last,
      gender: person.gender,
      dateOfBirth: person.dob,
      phone: nextPhone(),
      email: null,
      address: null,
      emergencyContactName: null,
      emergencyContactPhone: null,
      departmentId: (await department(admin, person.dept)).id,
      designationId: (designations.items ?? designations).find((d) => d.code === person.job).id,
      employmentType: 'Permanent',
      status: 'Active',
      joiningDate: person.joined,
      confirmationDate: null,
      reportingManagerId: null,
      practitionerId: person.userName === 'doctor' ? doctor.id : null,
      biometricPin: null,
      defaultShiftId: null,
      weeklyOff: 'Sunday',
      isAttendanceExempt: false,
      paymentMode: 'BankTransfer',
      uan: null,
      pfNumber: null,
      esiNumber: null,
      photoFileId: null,
      notes: null,
    };
    const body = { ...record, userId: user.id, pan: null, bank: null };
    for (const key of ['id', 'employeeCode', 'fullName', 'departmentName', 'designationName', 'leavingDate'])
      delete body[key];
    for (const key of ['separationReason', 'separationNote', 'reportingManagerName', 'bankName', 'bankAccountMasked'])
      delete body[key];
    for (const key of ['bankIfscMasked', 'panMasked']) delete body[key];
    if (existing) {
      await admin.put(`/api/v1/hr/employees/${existing.id}`, { ...body, version: existing.version });
    } else {
      await admin.post('/api/v1/hr/employees', { ...body, version: null });
    }
    outcome.staff.push({
      userName: person.userName,
      name: person.name ?? `${person.title ?? ''} ${person.first} ${person.last}`.trim(),
    });
  }
}

// ---------------------------------------------------------------------------------------------------------- steps

async function register(p) {
  const frontOffice = await Api.as('frontoffice');
  const phone = nextPhone();
  const created = await frontOffice.post('/api/v1/patients', {
    title: p.title,
    firstName: p.first,
    middleName: null,
    lastName: p.last,
    gender: p.gender,
    dateOfBirth: p.dob,
    ageYears: null,
    ageMonths: null,
    phone,
    alternatePhone: null,
    email: null,
    address: { line1: p.street ?? null, city: p.city, state: p.state, pinCode: p.pin ?? null, country: 'India' },
    bloodGroup: p.blood ?? null,
    maritalStatus: null,
    guardianName: null,
    guardianRelation: null,
    emergencyContactName: p.contact ?? null,
    emergencyContactPhone: p.contact ? nextPhone() : null,
    emergencyContactRelation: p.contactRelation ?? null,
    occupation: p.occupation ?? null,
    religion: null,
    nationality: 'Indian',
    preferredLanguage: p.language ?? null,
    category: null,
    abhaNumber: null,
    abhaAddress: null,
    aadhaarNumber: null,
    photoFileId: null,
    isVip: false,
    notes: null,
    confirmedNotDuplicate: true,
    privacyNoticeGiven: true,
  });
  return { ...created, phone, fullName: `${p.first} ${p.last}` };
}

/** The seeded patients' numbers look like anyone's: they get numbers from the fictional series too. */
async function fictionalPhone(patientId) {
  const frontOffice = await Api.as('frontoffice');
  const detail = await frontOffice.get(`/api/v1/patients/${patientId}`);
  await frontOffice.put(`/api/v1/patients/${patientId}`, {
    title: detail.title ?? null,
    firstName: detail.firstName,
    middleName: detail.middleName ?? null,
    lastName: detail.lastName ?? null,
    gender: detail.gender,
    dateOfBirth: detail.dateOfBirth ?? null,
    ageYears: null,
    ageMonths: null,
    phone: nextPhone(),
    alternatePhone: null,
    email: null,
    address: detail.address ?? null,
    bloodGroup: detail.bloodGroup ?? null,
    maritalStatus: detail.maritalStatus ?? null,
    guardianName: detail.guardianName ?? null,
    guardianRelation: detail.guardianRelation ?? null,
    emergencyContactName: null,
    emergencyContactPhone: null,
    emergencyContactRelation: null,
    occupation: detail.occupation ?? null,
    religion: detail.religion ?? null,
    nationality: detail.nationality ?? null,
    preferredLanguage: detail.preferredLanguage ?? null,
    category: detail.category ?? null,
    abhaNumber: detail.abhaNumber ?? null,
    abhaAddress: detail.abhaAddress ?? null,
    aadhaarNumber: null,
    photoFileId: null,
    isVip: detail.isVip ?? false,
    isDeceased: false,
    deceasedAt: null,
    notes: detail.notes ?? null,
    isActive: true,
    version: detail.version,
  });
}

/** The doctor's consulting room (the seed's schedule for Dr. Arjun Mehta), where tokens are called to. */
const ROOM = 'OPD-01';

/** A walk-in: a General Medicine visit with Dr. Arjun Mehta and a queue token, as the front desk makes it. */
async function walkIn(patientId, complaint) {
  const frontOffice = await Api.as('frontoffice');
  const doctor = await theDoctor();
  const created = await frontOffice.post('/api/v1/visits', {
    patientId,
    type: 'OutPatient',
    departmentId: (await department(frontOffice, 'GM')).id,
    practitionerId: doctor.id,
    appointmentId: null,
    payerType: 'Self',
    payerId: null,
    referralSource: 'Self',
    referredBy: null,
    chiefComplaint: complaint,
    isMlc: false,
    mlcNumber: null,
    notes: null,
    priority: 'Normal',
    issueToken: true,
    notifyPatient: false,
  });
  const visit = await frontOffice.get(`/api/v1/visits/${created.id}`);
  return { visitId: visit.id, tokenId: visit.token?.id, tokenLabel: visit.token?.label, room: ROOM };
}

/** A token the seed issued this morning (GM-001 … GM-005), for the doctor to call. */
async function seededToken(patientId) {
  const nurse = await Api.as('nurse');
  const gm = await department(nurse, 'GM');
  const tokens = await nurse.get(`/api/v1/queue?departmentId=${gm.id}&includeClosed=true`);
  const token = (tokens.items ?? tokens).find((t) => t.patientId === patientId);
  if (!token) throw new Error("The seeded patient has no token in today's General Medicine queue");
  return { visitId: token.visitId, tokenId: token.tokenId, tokenLabel: token.label, room: ROOM };
}

/** Vitals, taken now or (`takenAt`, "08:20") when the patient arrived. */
async function vitals(patientId, v, { encounterId = null, takenAt = null } = {}) {
  const nurse = await Api.as('nurse');
  return nurse.post(`/api/v1/emr/patients/${patientId}/vitals`, {
    encounterId,
    recordedAt: takenAt ? istAt(0, takenAt) : null,
    temperature: v.temp ?? null,
    temperatureUnit: 'Celsius',
    pulse: v.pulse ?? null,
    respiratoryRate: v.rr ?? null,
    systolicBp: v.sbp ?? null,
    diastolicBp: v.dbp ?? null,
    spO2: v.spo2 ?? null,
    onSupplementalOxygen: false,
    heightCm: v.height ?? null,
    weightKg: v.weight ?? null,
    painScore: v.pain ?? null,
    bloodGlucose: v.glucose ?? null,
    glucoseTiming: 'Random',
    gcs: null,
    notes: null,
  });
}

/**
 * The doctor calls the patient in (now) and writes the note: diagnoses, orders and the prescription. The note is signed
 * and the token closed at `done` ("09:16"); without `done` the patient is still with the doctor.
 */
async function consult(patient, visit, note, { done = null } = {}) {
  const doctor = await Api.as('doctor');
  if (visit.tokenId) {
    await doctor.post(`/api/v1/queue/${visit.tokenId}/call`, { room: visit.room ?? null });
  }
  let encounter = await doctor.post('/api/v1/emr/encounters', {
    patientId: patient.id,
    visitId: visit.visitId,
    admissionId: null,
    type: null,
    departmentId: null,
    practitionerId: null,
    templateId: null,
    chiefComplaint: note.complaint,
  });
  encounter = await doctor.put(`/api/v1/emr/encounters/${encounter.id}`, {
    chiefComplaint: note.complaint,
    historyOfPresentIllness: note.history,
    examination: note.examination,
    assessment: note.assessment ?? null,
    plan: note.plan ?? null,
    followUpDate: note.followUpDays ? addDays(istDate(), note.followUpDays) : null,
    followUpAdvice: note.followUpAdvice ?? null,
    referralDepartmentId: null,
    referralPractitionerId: null,
    referralExternal: null,
    referralReason: null,
    version: encounter.version,
  });
  for (const [code, primary, type] of note.diagnoses ?? []) {
    await doctor.post(`/api/v1/emr/encounters/${encounter.id}/diagnoses`, {
      icdCode: code,
      title: null,
      type: type ?? 'Provisional',
      isPrimary: primary,
      onsetDate: null,
      status: 'Active',
      notes: null,
    });
  }
  const result = { encounterId: encounter.id };
  const items = [];
  for (const code of note.labs ?? [])
    items.push({ serviceItemId: (await service(doctor, 'Laboratory', code)).id, notes: null });
  for (const code of note.imaging ?? [])
    items.push({ serviceItemId: (await service(doctor, 'Radiology', code)).id, notes: null });
  for (const code of note.cardiology ?? [])
    items.push({ serviceItemId: (await service(doctor, 'Cardiology', code)).id, notes: null });
  if (items.length) {
    const order = await doctor.post('/api/v1/clinical/orders', {
      patientId: patient.id,
      encounterId: encounter.id,
      visitId: null,
      admissionId: null,
      priority: 'Routine',
      clinicalNotes: note.orderNotes ?? note.complaint,
      items,
      orderSetId: null,
      confirmDuplicates: true,
    });
    result.orderId = order.id;
  }
  if (!done) return result;
  await at(done);
  if (note.medicines?.length) {
    result.prescription = await prescribe(doctor, patient.id, encounter.id, note.medicines, note.advice);
  }
  const latest = await doctor.get(`/api/v1/emr/encounters/${encounter.id}`);
  await doctor.post(`/api/v1/emr/encounters/${encounter.id}/sign`, { version: latest.version });
  if (visit.tokenId) {
    await doctor.post(`/api/v1/queue/${visit.tokenId}/complete`);
  }
  return result;
}

async function prescribe(
  doctor,
  patientId,
  encounterId,
  medicines,
  advice,
  { admissionId = null, inpatient = false } = {},
) {
  const items = [];
  for (const m of medicines) {
    const d = await drug(doctor, m.search, m.code);
    items.push({
      drugItemId: d.id,
      drugName: d.name,
      genericName: d.genericName ?? null,
      strength: d.strength ?? null,
      dosageForm: d.dosageForm ?? null,
      route: m.route ?? 'Oral',
      dose: m.dose,
      frequency: m.frequency,
      customFrequency: null,
      dosesPerDay: null,
      durationDays: m.days,
      // Given for "when needed" (SOS) doses, which no frequency can count.
      quantity: m.quantity ?? null,
      instructions: m.instructions ?? null,
      isControlledDrug: d.isControlled ?? false,
      controlledSchedule: d.controlledSchedule ?? null,
    });
  }
  const prescription = await doctor.post('/api/v1/clinical/prescriptions', {
    patientId,
    encounterId,
    visitId: null,
    admissionId,
    isInpatient: inpatient,
    practitionerId: null,
    notes: null,
    advice: advice ?? null,
    refillsAllowed: 0,
    items,
  });
  const check = await doctor.post(`/api/v1/clinical/prescriptions/${prescription.id}/check`);
  const signed = await doctor.post(`/api/v1/clinical/prescriptions/${prescription.id}/sign`, {
    overrideReason: check.isBlocked ? 'Benefit outweighs the risk; discussed with the patient' : null,
    acknowledgeWarnings: true,
    // Schedule H1/X medicines (a third-generation cephalosporin is H1) are signed only with the prescriber's confirmation.
    confirmControlledDrugs: Boolean(check.hasControlledDrugs) || prescription.items.some((i) => i.isControlledDrug),
    version: prescription.version,
  });
  return signed.prescription ?? signed;
}

async function openShift(counter) {
  const billing = await Api.as('billing');
  const current = await billing.response('GET', '/api/v1/billing/shifts/current');
  if (current.status === 204) {
    await billing.post('/api/v1/billing/shifts/open', { counter, openingFloat: 2000 });
  }
}

async function billVisit(visitId, pay) {
  const billing = await Api.as('billing');
  const draft = await billing.post('/api/v1/billing/bills', { visitId, patientId: null, chargeIds: null });
  const bill = await billing.post(`/api/v1/billing/bills/${draft.id}/finalize`, { version: draft.version });
  if (pay) {
    await openShift('OP counter 1');
    await billing.post('/api/v1/billing/receipts', {
      billId: bill.id,
      payments: [
        {
          mode: pay,
          amount: bill.dueAmount,
          reference: pay === 'Upi' ? 'UPI-428193' : pay === 'Card' ? 'POS-55104' : null,
          cardLast4: pay === 'Card' ? '1043' : null,
          cardNetwork: pay === 'Card' ? 'RuPay' : null,
          chequeNumber: null,
          chequeBank: null,
          chequeDate: null,
        },
      ],
      notes: null,
    });
  }
  return { billId: bill.id, billNumber: bill.billNumber, amount: bill.netAmount };
}

/**
 * The value a test's line is given: what the story says for its code or name, else a normal one — the first normal
 * option, or a value inside its reference interval (the interval's text, for example "13.0 - 17.0" or "< 200").
 */
function valueFor(line, values) {
  for (const key of [line.testCode, line.testName]) {
    if (key && values[key] !== undefined) return String(values[key]);
  }
  if (line.resultType === 'Options' && line.options?.length) {
    const normal = line.options.find((o) => /^(negative|non[- ]?reactive|not detected|absent|nil|normal)$/i.test(o));
    return normal ?? line.options[0];
  }
  if (line.resultType !== 'Numeric') return 'Within normal limits';
  const text = (line.referenceText ?? '').replace(/,/g, '');
  const numbers = [...text.matchAll(/\d+(?:\.\d+)?/g)].map((m) => Number(m[0]));
  let value;
  if (numbers.length >= 2 && /[-–]|to/.test(text)) value = (numbers[0] + numbers[1]) / 2;
  else if (numbers.length && /<|up to|below|less/i.test(text)) value = numbers[0] * 0.6;
  else if (numbers.length && />|above|more/i.test(text)) value = numbers[0] * 1.3;
  else value = numbers[0] ?? 1;
  return value.toFixed(Math.max(0, Math.min(line.decimals ?? 1, 2)));
}

/** Lab: draws the patient's samples (at the OPD counter or the bedside) and receives them in the lab. */
async function drawSamples(patient, site = 'OP sample collection') {
  const lab = await Api.as('lab');
  const waiting = await lab.get(`/api/v1/lab/samples/pending-collection?search=${encodeURIComponent(patient.uhid)}`);
  const labels = [];
  for (const orderId of new Set((waiting.items ?? []).map((t) => t.labOrderId))) {
    labels.push(
      ...(await lab.post(`/api/v1/lab/orders/${orderId}/collect`, {
        sampleIds: null,
        collectedAt: null,
        collectionSite: site,
      })),
    );
  }
  await lab.post('/api/v1/lab/samples/receive', { barcodes: labels.map((l) => l.barcode) });
  return labels;
}

/** Lab: enters each sample's results, from the story's values or normal ones. */
async function enterResults(labels, values) {
  const lab = await Api.as('lab');
  const resultIds = [];
  for (const label of labels) {
    const sheet = await lab.get(`/api/v1/lab/samples/${label.sampleId}/results`);
    const entries = sheet.lines.map((line) => ({
      resultId: line.resultId,
      value: valueFor(line, values),
      remarks: null,
      organism: null,
      colonyCount: null,
      susceptibilities: null,
    }));
    resultIds.push(...(await lab.post('/api/v1/lab/results', { entries })).map((e) => e.resultId));
  }
  return resultIds;
}

/** The pathologist verifies the results and releases the report. */
async function release(resultIds) {
  const pathologist = await Api.as('pathologist');
  const verify = (qcOverrideReason) =>
    pathologist.post('/api/v1/lab/results/verify', { resultIds, qcOverrideReason, criticalCall: null, release: true });
  try {
    await verify(null);
  } catch (error) {
    if (!(error instanceof ApiError) || !/qc/i.test(`${error.code} ${error.message}`)) throw error;
    await verify("Morning QC on the analyser was within limits; today's run not yet uploaded");
  }
}

/** Pharmacy: dispenses the signed prescription from the queue, first-expiry-first-out, paid at the counter. */
async function dispense(prescription) {
  const pharmacist = await Api.as('pharmacist');
  const store = (await pharmacist.get('/api/v1/inventory/stores?type=Pharmacy')).find((s) => s.code === 'OPPH');
  const queue = await pharmacist.get(
    `/api/v1/pharmacy/queue?search=${encodeURIComponent(prescription.prescriptionNumber)}`,
  );
  const entry = await pharmacist.get(`/api/v1/pharmacy/queue/${queue.items[0].id}?storeId=${store.id}`);
  let total = 0;
  const lines = [];
  for (const line of entry.lines) {
    const batches = await pharmacist.get(
      `/api/v1/pharmacy/stock/batches?storeId=${store.id}&itemId=${line.drugItemId}`,
    );
    const fefo = batches
      .filter((b) => !b.isExpired && !b.isBlocked && b.available >= line.requestedQuantity)
      .sort((a, b) => a.expiryDate.localeCompare(b.expiryDate))[0];
    total += line.requestedQuantity * fefo.mrp;
    lines.push({
      queueLineId: line.id,
      itemId: line.drugItemId,
      quantity: line.requestedQuantity,
      batchId: null,
      substitutionReason: null,
      prescriberConsent: false,
    });
  }
  const amount = Math.round(total);
  const sale = await pharmacist.post(`/api/v1/pharmacy/queue/${entry.id}/dispense`, {
    storeId: store.id,
    lines,
    counsellingNotes: 'Explained the dose and timing; take after food.',
    discountPercent: 0,
    payments: [{ mode: 'Cash', amount, reference: null }],
    confirmControlledDrugs: false,
  });
  return { saleId: sale.id, amount };
}

/** Radiology: the radiographer takes the patient's ordered X-ray, from scheduling to the image. */
async function takeXray(patientId, room, { arrive, start, acquired, doseNote, notes = null }) {
  const radiographer = await Api.as('radiographer');
  const studies = await radiographer.get(`/api/v1/radiology/studies?patientId=${patientId}`);
  const study = (studies.items ?? studies).find((s) => !['Completed', 'Reported', 'Verified'].includes(s.status));
  await radiographer.post(`/api/v1/radiology/studies/${study.id}/schedule`, {
    scheduledAt: istAt(0, arrive),
    room,
    stationAeTitle: null,
  });
  await at(arrive);
  await radiographer.post(`/api/v1/radiology/studies/${study.id}/arrive`);
  await at(start);
  await radiographer.post(`/api/v1/radiology/studies/${study.id}/start`);
  await at(acquired);
  await radiographer.post(`/api/v1/radiology/studies/${study.id}/acquire`, {
    contrastUsed: false,
    contrastAgent: null,
    contrastVolumeMl: null,
    doseNote,
    notes,
  });
  return study;
}

// ---------------------------------------------------------------------------------------------------------- the day

const results = {
  pneumonia: {
    'C-reactive protein (CRP), quantitative': '64',
    CRP: '64',
    WBC: '14.6',
    TLC: '14.6',
    'Total leucocyte count': '14.6',
    'Total leukocyte count': '14.6',
    Neutrophils: '84',
    'Neutrophils %': '84',
  },
  meenakshi: { 'C-reactive protein (CRP), quantitative': '18.6', CRP: '18.6' },
  diabetes: {
    HBA1C: '8.4',
    'Glycated haemoglobin (HbA1c)': '8.4',
    FBS: '168',
    'Glucose, fasting (FBS)': '168',
    'Total cholesterol': '226',
    'LDL cholesterol': '148',
    'HDL cholesterol': '38',
    Triglycerides: '212',
  },
};

async function main() {
  if (!CLOCK) {
    throw new Error('Set DEMO_CLOCK_FILE to the file the demo server takes its clock from (README.md).');
  }
  outcome.recordedAt = (await askServerTime()).toISOString();
  await Api.as('admin');
  outcome.today = istDate();
  console.log(`Demo day: ${outcome.today}, the hospital's clock at ${hhmm(serverNow())} IST`);
  const patients = {};
  const visits = {};
  const care = {};
  const samples = {};

  // ------------------------------------------------------------------ 9:00, before the OPD: staff and the ward round
  await at('09:01');
  await scene('Specialist accounts (pathologist, radiologist, radiographer)', createSpecialists);
  await scene('Staff on the HR roll, under their own names', staffOnTheRoll);
  await scene('Seeded patients get fictional phone numbers', async () => {
    const frontOffice = await Api.as('frontoffice');
    const all = await frontOffice.get('/api/v1/patients?pageSize=50');
    for (const p of all.items ?? all) await fictionalPhone(p.id);
    return { patients: (all.items ?? all).length };
  });

  await at('09:03');
  await scene(
    'Ward round: Fatima Begum, day 2 of pneumonia (IV antibiotics, bloods and a chest X-ray ordered)',
    async () => {
      const patient = (patients.fatima = await seededPatient('Fatima', 'Begum'));
      const frontOffice = await Api.as('frontoffice');
      const doctor = await theDoctor();
      const ward = (await wards(frontOffice)).find((w) => w.code === 'GWF');
      const beds = await frontOffice.get(`/api/v1/beds/available?patientId=${patient.id}&wardId=${ward.id}`);
      const bed = beds.find((b) => b.isEligible && b.status === 'Vacant');
      const admission = await frontOffice.post('/api/v1/inpatient/admissions', {
        patientId: patient.id,
        bedId: bed.bedId,
        type: 'Emergency',
        source: 'Emergency',
        sourceVisitId: null,
        departmentId: null,
        admittingPractitionerId: doctor.id,
        consultantPractitionerId: doctor.id,
        provisionalDiagnosis: 'Community-acquired pneumonia, right lower lobe',
        referredBy: null,
        payerType: 'Self',
        payerId: null,
        policyNumber: null,
        isMlc: false,
        mlcNumber: null,
        expectedDischargeDate: addDays(istDate(), 3),
        attendantName: 'Imran Begum',
        attendantRelation: 'Son',
        attendantPhone: nextPhone(),
        admittedAt: istAt(-1, '18:40'),
        notes: 'Admitted from casualty with fever, cough and breathlessness.',
        notifyPatient: false,
      });
      care.fatimaAdmission = admission;
      await vitals(
        patient.id,
        { temp: 38.1, pulse: 102, rr: 24, sbp: 112, dbp: 70, spo2: 93, weight: 61 },
        { takenAt: '06:00' },
      );
      const doc = await Api.as('doctor');
      await doc.post(`/api/v1/inpatient/admissions/${admission.id}/consultant-visits`, {
        practitionerId: null,
        visitedAt: istAt(0, '08:40'),
        notes:
          'Day 2. Fever down to 38.1 °C, SpO2 93% on room air, crepitations right base. Continue IV ceftriaxone; CBC and CRP today; repeat chest X-ray.',
      });
      await prescribe(
        doc,
        patient.id,
        null,
        [
          {
            search: 'Ceftriaxone',
            code: 'DRG-CEFTRI1G',
            route: 'IV',
            dose: '1 g',
            frequency: 'BD',
            days: 5,
            instructions: 'Slow IV over 30 minutes',
          },
          {
            search: 'Paracetamol',
            code: 'DRG-PCM650',
            dose: '1 tablet',
            frequency: 'TDS',
            days: 3,
            instructions: 'If temperature above 100 °F',
          },
        ],
        null,
        { admissionId: admission.id, inpatient: true },
      );
      await doc.post('/api/v1/clinical/orders', {
        patientId: patient.id,
        encounterId: null,
        visitId: null,
        admissionId: admission.id,
        priority: 'Routine',
        clinicalNotes: 'Pneumonia, day 2: CBC and CRP; repeat chest X-ray to see the consolidation',
        items: [
          { serviceItemId: (await service(doc, 'Laboratory', 'LAB-CBC')).id, notes: null },
          { serviceItemId: (await service(doc, 'Laboratory', 'LAB-CRP')).id, notes: null },
          { serviceItemId: (await service(doc, 'Radiology', 'RAD-XR-CHEST')).id, notes: 'Portable, on the ward' },
        ],
        orderSetId: null,
        confirmDuplicates: true,
      });
      return {
        patient: patient.fullName,
        uhid: patient.uhid,
        admission: admission.admissionNumber ?? admission.id,
        bed: bed.bedNumber ?? bed.code,
      };
    },
  );

  await at('09:06');
  await scene(
    'Ward round: Sunita Devi fit for discharge (summary signed, her video appointment called off)',
    async () => {
      const patient = (patients.sunita = await seededPatient('Sunita', 'Devi'));
      const frontOffice = await Api.as('frontoffice');
      const doctor = await theDoctor();
      const ward = (await wards(frontOffice)).find((w) => w.code === 'SPW');
      const beds = await frontOffice.get(`/api/v1/beds/available?patientId=${patient.id}&wardId=${ward.id}`);
      const bed = beds.find((b) => b.isEligible && b.status === 'Vacant');
      const admittedAt = istAt(-3, '10:15');
      const admission = await frontOffice.post('/api/v1/inpatient/admissions', {
        patientId: patient.id,
        bedId: bed.bedId,
        type: 'Elective',
        source: 'OutPatient',
        sourceVisitId: null,
        departmentId: null,
        admittingPractitionerId: doctor.id,
        consultantPractitionerId: doctor.id,
        provisionalDiagnosis: 'Acute gastroenteritis with moderate dehydration',
        referredBy: null,
        payerType: 'Self',
        payerId: null,
        policyNumber: null,
        isMlc: false,
        mlcNumber: null,
        expectedDischargeDate: istDate(),
        attendantName: 'Ramesh Prasad',
        attendantRelation: 'Husband',
        attendantPhone: nextPhone(),
        admittedAt,
        notes: null,
        notifyPatient: false,
      });
      care.sunitaAdmission = admission;
      const doc = await Api.as('doctor');
      await doc.post(`/api/v1/inpatient/admissions/${admission.id}/consultant-visits`, {
        practitionerId: null,
        visitedAt: istAt(0, '08:50'),
        notes: 'Tolerating oral diet, no vomiting for 36 hours, hydration good. Fit for discharge.',
      });
      const nurse = await Api.as('nurse');
      await nurse.post(`/api/v1/inpatient/admissions/${admission.id}/discharge/initiate`, {
        expectedDischargeDate: istDate(),
        notes: 'Discharge advised on the ward round',
      });
      await at('09:08');
      const path = `/api/v1/clinical/discharge-summaries/by-admission/${admission.id}`;
      const prefill = await doc.get(`${path}/prefill?patientId=${patient.id}`);
      const draft = await doc.put(path, {
        patientId: patient.id,
        encounterId: null,
        practitionerId: null,
        admittedAt,
        dischargedAt: null,
        diagnoses: 'Acute gastroenteritis with moderate dehydration — resolved',
        diagnosisCodes: ['A09.9'],
        presentingComplaints: 'Loose stools and vomiting for two days with giddiness',
        courseInHospital:
          'Rehydrated with IV fluids for 24 hours, then oral rehydration. Stool frequency settled by day 2. Electrolytes normal on day 2.',
        procedures: prefill.procedures ?? null,
        investigationsSummary: 'Serum electrolytes within normal limits on day 2.',
        conditionAtDischarge: 'Stable, tolerating oral diet',
        medications:
          'ORS sachets after each loose stool for 3 days; tablet Pantoprazole 40 mg once daily before breakfast for 5 days',
        advice: 'Boiled water, light diet for a week; hand hygiene.',
        followUpDate: addDays(istDate(), 7),
        followUpInstructions: 'Review in the OPD after a week, earlier if the loose stools return.',
        version: null,
      });
      await doc.post(`/api/v1/clinical/discharge-summaries/${draft.id}/sign`, { version: draft.version });
      // The seed booked her a video consultation this morning; she is on the ward, so the desk calls it off.
      await at('09:09');
      const frontDesk = await Api.as('frontoffice');
      const booked = await frontDesk.get(
        `/api/v1/scheduling/appointments?from=${istDate()}&to=${istDate()}&patientId=${patient.id}&pageSize=50`,
      );
      for (const appointment of (booked.items ?? booked).filter(
        (a) => a.patientId === patient.id && ['Booked', 'Confirmed'].includes(a.status),
      )) {
        await frontDesk.post(`/api/v1/scheduling/appointments/${appointment.id}/cancel`, {
          reasonCode: 'HospitalRequest',
          reason: 'Admitted to the ward; to be seen in the follow-up clinic after discharge',
          notifyPatient: false,
        });
      }
      return { patient: patient.fullName, uhid: patient.uhid, admission: admission.admissionNumber ?? admission.id };
    },
  );

  // ------------------------------------------------------------------ 9:10, the OPD: the morning's tokens, in order
  await at('09:10');
  await scene('OPD: Ravi Kumar (GM-001), fever for three days; CBC and dengue test ordered, not yet paid', async () => {
    const patient = (patients.ravi = await seededPatient('Ravi', 'Kumar'));
    const visit = await seededToken(patient.id);
    await vitals(
      patient.id,
      { temp: 38.9, pulse: 104, rr: 20, sbp: 118, dbp: 76, spo2: 97, weight: 71, pain: 4 },
      { takenAt: '08:20' },
    );
    await consult(
      patient,
      visit,
      {
        complaint: 'Fever and cough for 3 days',
        history: 'High-grade fever with chills and dry cough for three days; body ache; no breathlessness.',
        examination: 'Temperature 38.9 °C, pulse 104/min. Chest: few scattered crepitations right base.',
        assessment: 'Acute febrile illness — dengue vs lower respiratory infection.',
        plan: 'CBC and dengue NS1 today; paracetamol; review with reports.',
        followUpDays: 2,
        diagnoses: [['R50.9', true]],
        labs: ['LAB-CBC', 'LAB-DENGUE-NS1'],
        medicines: [
          {
            search: 'Paracetamol',
            code: 'DRG-PCM650',
            dose: '1 tablet',
            frequency: 'TDS',
            days: 3,
            instructions: 'After food, for fever',
          },
        ],
        advice: 'Plenty of fluids; come back with the reports.',
      },
      { done: '09:16' },
    );
    return { patient: patient.fullName, uhid: patient.uhid, token: visit.tokenLabel };
  });

  await at('09:17');
  await scene('Front desk: Meenakshi Sundaram registered, walk-in token, vitals', async () => {
    const patient = (patients.meenakshi = await register({
      title: 'Mrs.',
      first: 'Meenakshi',
      last: 'Sundaram',
      gender: 'Female',
      dob: '1972-03-14',
      city: 'Madurai',
      state: 'Tamil Nadu',
      pin: '625001',
      blood: 'OPositive',
      occupation: 'School teacher',
      language: 'ta',
    }));
    await at('09:18');
    visits.meenakshi = await walkIn(patient.id, 'Fever and body ache for three days');
    await at('09:20');
    await vitals(patient.id, {
      temp: 38.4,
      pulse: 98,
      rr: 18,
      sbp: 124,
      dbp: 80,
      spo2: 98,
      weight: 63,
      height: 158,
      pain: 3,
    });
    return { patient: patient.fullName, uhid: patient.uhid, token: visits.meenakshi.tokenLabel };
  });

  await at('09:21');
  await scene('Appointments: Joseph Mathew phones; his appointment moves to 11:30', async () => {
    const patient = (patients.joseph = await seededPatient('Joseph', 'Mathew'));
    const frontDesk = await Api.as('frontoffice');
    const booked = await frontDesk.get(
      `/api/v1/scheduling/appointments?from=${istDate()}&to=${istDate()}&patientId=${patient.id}&pageSize=50`,
    );
    const appointment = (booked.items ?? booked).find(
      (a) => a.patientId === patient.id && ['Booked', 'Confirmed'].includes(a.status),
    );
    if (!appointment) return { patient: patient.fullName, note: 'no appointment to move' };
    const moved = await frontDesk.post(`/api/v1/scheduling/appointments/${appointment.id}/reschedule`, {
      startsAt: istAt(0, '11:30'),
      practitionerId: null,
      isTelehealth: null,
      reason: 'Patient phoned: coming after work',
      overbook: false,
      notifyPatient: false,
    });
    return { patient: patient.fullName, at: moved.startsAt ?? istAt(0, '11:30') };
  });

  await at('09:22');
  await scene('OPD: Lakshmi Narayanan (GM-002), knee pain; bill not yet paid, medicines waiting', async () => {
    const patient = (patients.lakshmi = await seededPatient('Lakshmi', 'Narayanan'));
    const visit = (visits.lakshmi = await seededToken(patient.id));
    await vitals(
      patient.id,
      { temp: 36.7, pulse: 78, rr: 16, sbp: 146, dbp: 86, spo2: 97, weight: 66, pain: 6 },
      { takenAt: '08:25' },
    );
    care.lakshmi = await consult(
      patient,
      visit,
      {
        complaint: 'Pain in both knees for six months, worse on climbing stairs',
        history:
          'Bilateral knee pain, morning stiffness under 20 minutes, worse on stairs. Known hypertensive on amlodipine.',
        examination: 'Crepitus over both knees, no effusion. BP 146/86 mmHg.',
        assessment: 'Primary osteoarthritis of both knees; hypertension.',
        plan: 'Vitamin D and B12 levels; analgesics; physiotherapy referral; continue amlodipine.',
        followUpDays: 14,
        diagnoses: [
          ['M17.1', true],
          ['I10', false, 'Final'],
        ],
        labs: ['LAB-VITD', 'LAB-VITB12'],
        medicines: [
          {
            search: 'Paracetamol',
            code: 'DRG-PCM500',
            dose: '1 tablet',
            frequency: 'BD',
            days: 10,
            instructions: 'After food, for pain',
          },
          {
            search: 'Amlodipine',
            code: 'DRG-AMLO5',
            dose: '1 tablet',
            frequency: 'OD',
            days: 30,
            instructions: 'In the morning',
          },
        ],
        advice: 'Quadriceps exercises twice daily; avoid squatting.',
      },
      { done: '09:28' },
    );
    // She also held a portal appointment for later this morning: seen already, so it is called off.
    await at('09:29');
    const frontDesk = await Api.as('frontoffice');
    const booked = await frontDesk.get(
      `/api/v1/scheduling/appointments?from=${istDate()}&to=${istDate()}&patientId=${patient.id}&pageSize=50`,
    );
    for (const appointment of (booked.items ?? booked).filter(
      (a) => a.patientId === patient.id && ['Booked', 'Confirmed'].includes(a.status),
    )) {
      await frontDesk.post(`/api/v1/scheduling/appointments/${appointment.id}/cancel`, {
        reasonCode: 'Duplicate',
        reason: 'Seen on a walk-in token this morning',
        notifyPatient: false,
      });
    }
    await at('09:30');
    const bill = await billVisit(visit.visitId, null);
    return {
      patient: patient.fullName,
      uhid: patient.uhid,
      token: visit.tokenLabel,
      bill: bill.billNumber,
      prescription: care.lakshmi.prescription?.prescriptionNumber,
    };
  });

  await at('09:31');
  await scene('Front desk: Rajendran Pillai registered, walk-in token, vitals', async () => {
    const patient = (patients.rajendran = await register({
      title: 'Mr.',
      first: 'Rajendran',
      last: 'Pillai',
      gender: 'Male',
      dob: '1979-08-02',
      city: 'Coimbatore',
      state: 'Tamil Nadu',
      pin: '641002',
      blood: 'BPositive',
      occupation: 'Textile trader',
      contact: 'Revathi Pillai',
      contactRelation: 'Wife',
    }));
    await at('09:32');
    visits.rajendran = await walkIn(patient.id, 'Diabetes review, tiredness');
    await at('09:34');
    await vitals(patient.id, {
      temp: 36.8,
      pulse: 84,
      rr: 16,
      sbp: 138,
      dbp: 88,
      spo2: 98,
      weight: 82,
      height: 170,
      glucose: 212,
    });
    return { patient: patient.fullName, uhid: patient.uhid, token: visits.rajendran.tokenLabel };
  });

  await at('09:35');
  await scene('OPD: Aarav Sharma (GM-003), type 1 diabetes review', async () => {
    const patient = (patients.aarav = await seededPatient('Aarav', 'Sharma'));
    const visit = await seededToken(patient.id);
    await vitals(patient.id, { temp: 36.6, pulse: 92, rr: 20, weight: 24, height: 124 }, { takenAt: '08:30' });
    await consult(
      patient,
      visit,
      {
        complaint: 'Routine diabetes review',
        history:
          'Type 1 diabetes since age 5 on basal-bolus insulin. Home glucose log mostly 100–180 mg/dL; one mild low last week after football.',
        examination: 'Well, growing along the 50th centile. Injection sites healthy.',
        assessment: 'Type 1 diabetes, well controlled.',
        plan: 'Continue insulin as now; carry glucose tablets for games; HbA1c at the next visit in three months.',
        followUpDays: 90,
        diagnoses: [['E10.9', true, 'Final']],
      },
      { done: '09:40' },
    );
    return { patient: patient.fullName, uhid: patient.uhid, token: visit.tokenLabel };
  });

  await at('09:41');
  await scene('OPD: Priya Menon (GM-004), tension headache', async () => {
    const patient = (patients.priya = await seededPatient('Priya', 'Menon'));
    const visit = await seededToken(patient.id);
    await vitals(
      patient.id,
      { temp: 36.9, pulse: 82, rr: 16, sbp: 112, dbp: 72, spo2: 99, weight: 54, pain: 5 },
      { takenAt: '08:35' },
    );
    await consult(
      patient,
      visit,
      {
        complaint: 'Headache for a week',
        history:
          'Band-like headache over both temples for a week, worse in the evenings after work on the computer. No vomiting, no visual change.',
        examination: 'BP 112/72 mmHg. Neurological examination normal. Neck muscles tender.',
        assessment: 'Tension-type headache.',
        plan: 'Paracetamol when needed; screen breaks; neck stretches. Come back if it changes in character.',
        diagnoses: [['G44.2', true, 'Final']],
        medicines: [
          {
            search: 'Paracetamol',
            code: 'DRG-PCM500',
            dose: '1 tablet',
            frequency: 'SOS',
            days: 5,
            quantity: 10,
            instructions: 'When the headache comes on; at most three a day',
          },
        ],
        advice: 'A short break from the screen every hour.',
      },
      { done: '09:46' },
    );
    return { patient: patient.fullName, uhid: patient.uhid, token: visit.tokenLabel };
  });

  await at('09:47');
  await scene("Lab: Fatima Begum's bloods drawn at the bedside", async () => {
    samples.fatima = await drawSamples(patients.fatima, 'Ward GWF, bedside');
    return { samples: samples.fatima.length };
  });

  await at('09:48');
  await scene('Front desk: Karthik Subramanian, walk-in token, vitals', async () => {
    const patient = (patients.karthik = await seededPatient('Karthik', 'Subramanian'));
    visits.karthik = await walkIn(patient.id, 'Cough with sputum for two weeks');
    await at('09:50');
    await vitals(patient.id, { temp: 37.2, pulse: 88, rr: 18, sbp: 120, dbp: 78, spo2: 97, weight: 69 });
    return { patient: patient.fullName, uhid: patient.uhid, token: visits.karthik.tokenLabel };
  });

  await at('09:51');
  await scene('OPD: Mohammed Irfan (GM-005), follow-up after a urinary infection', async () => {
    const patient = (patients.mohammed = await seededPatient('Mohammed', 'Irfan'));
    const visit = await seededToken(patient.id);
    await vitals(
      patient.id,
      { temp: 36.7, pulse: 76, rr: 16, sbp: 124, dbp: 80, spo2: 99, weight: 74 },
      { takenAt: '08:40' },
    );
    await consult(
      patient,
      visit,
      {
        complaint: 'Follow-up after treatment for a urinary infection',
        history: 'Finished five days of nitrofurantoin last week. No burning or frequency now. No fever.',
        examination: 'Afebrile, no renal angle tenderness.',
        assessment: 'Urinary tract infection, resolved.',
        plan: 'No further treatment. Two to three litres of water a day.',
        diagnoses: [['N39.0', true, 'Final']],
      },
      { done: '09:55' },
    );
    return { patient: patient.fullName, uhid: patient.uhid, token: visit.tokenLabel };
  });

  await at('09:56');
  await scene(
    'OPD: Meenakshi Sundaram seen for fever; CRP and CBC ordered; paid by UPI; samples drawn; medicines dispensed',
    async () => {
      const patient = patients.meenakshi;
      care.meenakshi = await consult(
        patient,
        visits.meenakshi,
        {
          complaint: 'Fever and body ache for three days',
          history:
            'Intermittent fever up to 101 °F for three days with body ache and mild headache. No cough, rash or bleeding. No recent travel.',
          examination:
            'Temperature 38.4 °C, pulse 98/min, BP 124/80 mmHg. Throat mildly congested; chest clear; abdomen soft.',
          assessment: 'Acute febrile illness, probably viral; rule out bacterial infection.',
          plan: 'CRP and CBC today. Paracetamol for fever, oral fluids, rest. Review with reports.',
          followUpDays: 3,
          followUpAdvice: 'Come back earlier if the fever lasts beyond two more days or you notice a rash or bleeding.',
          diagnoses: [['R50.9', true]],
          labs: ['LAB-CRP', 'LAB-CBC'],
          orderNotes: 'Fever for three days',
          medicines: [
            {
              search: 'Paracetamol',
              code: 'DRG-PCM650',
              dose: '1 tablet',
              frequency: 'TDS',
              days: 3,
              instructions: 'After food, when the temperature is above 100 °F',
            },
          ],
          advice: 'Drink plenty of fluids and rest.',
        },
        { done: '10:02' },
      );
      await at('10:03');
      const bill = await billVisit(visits.meenakshi.visitId, 'Upi');
      await at('10:05');
      samples.meenakshi = await drawSamples(patient);
      await at('10:08');
      const sale = await dispense(care.meenakshi.prescription);
      return { patient: patient.fullName, uhid: patient.uhid, bill: bill.billNumber, sale };
    },
  );

  await at('10:09');
  await scene(
    'OPD: Rajendran Pillai, diabetes review; HbA1c, fasting glucose and lipids ordered; paid in cash; samples drawn',
    async () => {
      const patient = patients.rajendran;
      care.rajendran = await consult(
        patient,
        visits.rajendran,
        {
          complaint: 'Diabetes review; tiredness and increased thirst for two weeks',
          history:
            'Type 2 diabetes for 6 years on metformin 500 mg once daily, irregular. Increased thirst and tiredness for two weeks. No chest pain.',
          examination:
            'BP 138/88 mmHg, BMI 28.4. Random capillary glucose 212 mg/dL. Feet: pulses felt, sensation intact.',
          assessment: 'Type 2 diabetes mellitus, poorly controlled; overweight.',
          plan: 'HbA1c, fasting glucose and lipid profile. Increase metformin to 500 mg twice daily. Diet counselling.',
          followUpDays: 30,
          followUpAdvice: 'Review in a month with home glucose readings.',
          diagnoses: [
            ['E11.9', true, 'Final'],
            ['E66.9', false],
          ],
          labs: ['LAB-HBA1C', 'LAB-FBS', 'LAB-LIPID'],
          medicines: [
            {
              search: 'Metformin',
              code: 'DRG-MET500',
              dose: '1 tablet',
              frequency: 'BD',
              days: 30,
              instructions: 'With breakfast and dinner',
            },
          ],
          advice: 'Low-sugar diet, 30 minutes of walking daily.',
        },
        { done: '10:15' },
      );
      await at('10:16');
      const bill = await billVisit(visits.rajendran.visitId, 'Cash');
      await at('10:18');
      samples.rajendran = await drawSamples(patient);
      return {
        patient: patient.fullName,
        uhid: patient.uhid,
        bill: bill.billNumber,
        prescription: care.rajendran.prescription?.prescriptionNumber,
      };
    },
  );

  await at('10:21');
  await scene('OPD: Karthik Subramanian, cough; chest X-ray ordered, paid by card, taken', async () => {
    const patient = patients.karthik;
    await consult(
      patient,
      visits.karthik,
      {
        complaint: 'Cough with sputum for two weeks',
        history: 'Productive cough for two weeks, low-grade evening fever, smoker 10/day.',
        examination: 'Chest: bronchial breath sounds right upper zone.',
        assessment: 'Lower respiratory tract infection; rule out tuberculosis.',
        plan: 'Chest X-ray PA view; sputum AFB; azithromycin for 5 days.',
        diagnoses: [['J18.9', true]],
        imaging: ['RAD-XR-CHEST'],
        medicines: [
          {
            search: 'Azithromycin',
            code: 'DRG-AZI500',
            dose: '1 tablet',
            frequency: 'OD',
            days: 5,
            instructions: 'One hour before food',
          },
        ],
        advice: 'Stop smoking; cover the mouth while coughing.',
      },
      { done: '10:27' },
    );
    await at('10:28');
    await billVisit(visits.karthik.visitId, 'Card');
    care.karthikStudy = await takeXray(patient.id, 'X-ray 1', {
      arrive: '10:31',
      start: '10:32',
      acquired: '10:34',
      doseNote: 'Single PA exposure',
    });
    return {
      patient: patient.fullName,
      uhid: patient.uhid,
      study: care.karthikStudy.accessionNumber ?? care.karthikStudy.id,
    };
  });

  await at('10:35');
  await scene("Radiology: Fatima Begum's portable chest X-ray taken on the ward", async () => {
    const study = await takeXray(patients.fatima.id, 'Portable X-ray', {
      arrive: '10:37',
      start: '10:37',
      acquired: '10:39',
      doseNote: 'Single AP exposure, portable',
      notes: 'Taken at the bedside; patient unable to stand',
    });
    return { study: study.accessionNumber ?? study.id };
  });

  await at('10:40');
  await scene("Lab: Meenakshi Sundaram's CRP (18.6 mg/L) and CBC entered, verified and released", async () => {
    const resultIds = await enterResults(samples.meenakshi, results.meenakshi);
    await at('10:43');
    await release(resultIds);
    return { results: resultIds.length };
  });

  await at('10:44');
  await scene('Billing: Sunita Devi settles her final bill by card and goes home', async () => {
    const billing = await Api.as('billing');
    const account = await billing.get(`/api/v1/billing/ip-accounts/by-admission/${care.sunitaAdmission.id}`);
    let bill = await billing.post(`/api/v1/billing/ip-accounts/${account.id}/final-bill`);
    bill = await billing.post(`/api/v1/billing/bills/${bill.id}/finalize`, { version: bill.version });
    await openShift('IP billing');
    if (bill.dueAmount > 0) {
      await billing.post('/api/v1/billing/receipts', {
        billId: bill.id,
        payments: [
          {
            mode: 'Card',
            amount: bill.dueAmount,
            reference: 'POS-77213',
            cardLast4: '4821',
            cardNetwork: 'Visa',
            chequeNumber: null,
            chequeBank: null,
            chequeDate: null,
          },
        ],
        notes: null,
      });
    }
    await at('10:47');
    const nurse = await Api.as('nurse');
    await nurse.post(`/api/v1/inpatient/admissions/${care.sunitaAdmission.id}/discharge/complete`, {
      dischargeType: 'Normal',
      dischargedAt: null,
      notes: 'Discharged home in a stable condition',
      notifyPatient: false,
    });
    return { patient: patients.sunita.fullName, bill: bill.billNumber };
  });

  await at('10:48');
  await scene("Lab: Rajendran Pillai's diabetes panel entered and released (HbA1c 8.4%)", async () => {
    const resultIds = await enterResults(samples.rajendran, results.diabetes);
    await at('10:51');
    await release(resultIds);
    return { results: resultIds.length };
  });

  await at('10:52');
  await scene("Radiology: Karthik Subramanian's chest X-ray reported and signed", async () => {
    const radiologist = await Api.as('radiologist');
    const studyId = care.karthikStudy.id;
    let report = await radiologist.put(`/api/v1/radiology/studies/${studyId}/report`, {
      templateId: null,
      technique: 'Chest radiograph, PA view, erect.',
      clinicalHistory: 'Productive cough for two weeks with evening fever.',
      comparison: 'None available.',
      findings:
        'Patchy air-space opacity in the right upper zone with air bronchograms. No cavitation. Costophrenic angles clear. Heart size normal.',
      impression: 'Right upper zone consolidation — infective; correlate with sputum AFB.',
      isCriticalFinding: false,
      criticalFindingNote: null,
      version: null,
    });
    await at('10:55');
    report = await radiologist.post(`/api/v1/radiology/studies/${studyId}/report/submit`, { version: report.version });
    await radiologist.post(`/api/v1/radiology/studies/${studyId}/report/verify`, {
      criticalCall: null,
      version: report.version,
      release: true,
    });
    return {};
  });

  await at('10:56');
  await scene("Lab: Fatima Begum's CBC and CRP entered (CRP 64 mg/L), waiting for the pathologist", async () => {
    const resultIds = await enterResults(samples.fatima, results.pneumonia);
    return { results: resultIds.length };
  });

  // ------------------------------------------------------------------ 10:57, what is happening as the demo opens
  await at('10:57');
  await scene('Casualty: Senthil Kumar, fall from a two-wheeler; triaged level 2 and seen', async () => {
    const frontOffice = await Api.as('frontoffice');
    const patient = (patients.senthil = await register({
      title: 'Mr.',
      first: 'Senthil',
      last: 'Kumar',
      gender: 'Male',
      dob: '1987-11-21',
      city: 'Chennai',
      state: 'Tamil Nadu',
      pin: '600097',
      contact: 'Malar Kumar',
      contactRelation: 'Wife',
    }));
    const emergency = await department(frontOffice, 'EMER');
    const visit = await frontOffice.post('/api/v1/visits', {
      patientId: patient.id,
      type: 'Emergency',
      departmentId: emergency.id,
      practitionerId: null,
      appointmentId: null,
      payerType: 'Self',
      payerId: null,
      referralSource: 'Self',
      referredBy: null,
      chiefComplaint: 'Fall from two-wheeler',
      isMlc: false,
      mlcNumber: null,
      notes: null,
      priority: 'Emergency',
      issueToken: false,
      notifyPatient: false,
    });
    await at('10:58');
    const nurse = await Api.as('nurse');
    const row = (await nurse.get('/api/v1/emergency/board')).rows.find((r) => r.visitId === visit.id);
    const triaged = await nurse.post(`/api/v1/emergency/cases/${row.caseId}/triage`, {
      chiefComplaint: 'Fall from two-wheeler: forearm laceration, knee abrasion, brief giddiness',
      arrivalMode: 'GovernmentAmbulance',
      needsLifeSavingIntervention: false,
      highRiskSituation: false,
      confusedLethargicOrDisoriented: false,
      severeDistress: false,
      expectedResources: 2,
      pulse: 118,
      respiratoryRate: 18,
      spO2: 95,
      systolicBp: 128,
      diastolicBp: 82,
      temperatureCelsius: 37.1,
      gcs: 15,
      painScore: 6,
      level: null,
      overrideReason: null,
      version: row.version,
    });
    care.senthil = { caseId: row.caseId, version: triaged.version };
    return { patient: patient.fullName, uhid: patient.uhid, level: triaged.level };
  });

  await at('10:59');
  await scene('Front desk: Vimala Raghavan, chest discomfort on walking; walk-in token, vitals', async () => {
    const patient = (patients.vimala = await register({
      title: 'Mrs.',
      first: 'Vimala',
      last: 'Raghavan',
      gender: 'Female',
      dob: '1967-06-09',
      city: 'Chennai',
      state: 'Tamil Nadu',
      pin: '600020',
      occupation: 'Homemaker',
      contact: 'Raghavan S',
      contactRelation: 'Husband',
    }));
    visits.vimala = await walkIn(patient.id, 'Chest discomfort on walking for two weeks');
    await at('11:01');
    await vitals(patient.id, { temp: 36.8, pulse: 88, rr: 18, sbp: 152, dbp: 94, spo2: 97, weight: 72, height: 156 });
    return { patient: patient.fullName, uhid: patient.uhid, token: visits.vimala.tokenLabel };
  });

  await at('11:02');
  await scene('Casualty: the doctor sees Senthil Kumar', async () => {
    const doctor = await Api.as('doctor');
    await doctor.post(`/api/v1/emergency/cases/${care.senthil.caseId}/seen`, { version: care.senthil.version });
    return {};
  });

  await at('11:03');
  await scene('Front desk: Anand Joseph, walk-in token; vitals taken', async () => {
    const patient = (patients.anand = await register({
      title: 'Mr.',
      first: 'Anand',
      last: 'Joseph',
      gender: 'Male',
      dob: '1990-02-27',
      city: 'Kochi',
      state: 'Kerala',
      pin: '682016',
      occupation: 'Software engineer',
    }));
    visits.anand = await walkIn(patient.id, 'Sore throat and fever since yesterday');
    await at('11:04');
    await vitals(patient.id, { temp: 38.0, pulse: 96, rr: 16, sbp: 122, dbp: 80, spo2: 98, weight: 70 });
    return { patient: patient.fullName, uhid: patient.uhid, token: visits.anand.tokenLabel };
  });

  await at('11:05');
  await scene('OPD: Vimala Raghavan with the doctor now; ECG ordered', async () => {
    await consult(patients.vimala, visits.vimala, {
      complaint: 'Chest discomfort on walking for two weeks',
      history:
        'Tightness in the centre of the chest after walking about 200 metres, easing with rest in a few minutes. Hypertensive, irregular with tablets. No pain at rest.',
      examination: 'BP 152/94 mmHg, pulse 88/min regular. Heart sounds normal, chest clear.',
      assessment: 'Exertional chest discomfort: rule out stable angina.',
      plan: 'ECG now; fasting lipids and HbA1c; treadmill test depending on the ECG.',
      diagnoses: [['R07.4', true]],
      cardiology: ['CARD-ECG'],
      orderNotes: 'Exertional chest tightness for two weeks',
    });
    return {};
  });

  await at('11:07');
  await scene('Front desk: Farah Sheikh, walk-in token', async () => {
    const patient = (patients.farah = await register({
      title: 'Ms.',
      first: 'Farah',
      last: 'Sheikh',
      gender: 'Female',
      dob: '1998-12-03',
      city: 'Hyderabad',
      state: 'Telangana',
      pin: '500034',
      occupation: 'Graduate student',
    }));
    visits.farah = await walkIn(patient.id, 'Itchy rash on both arms for three days');
    return { patient: patient.fullName, uhid: patient.uhid, token: visits.farah.tokenLabel };
  });

  // The demo shows this moment: the clock stops, so every screen recorded from now on is of 11:10 am.
  outcome.demoClock = (await stopAt('11:10')).toISOString();
  outcome.finishedAt = (await askServerTime()).toISOString();
  const file = process.argv[2] ?? 'story.json';
  writeFileSync(file, JSON.stringify(outcome, null, 2));
  const failed = outcome.scenes.filter((s) => !s.ok);
  console.log(`\n${outcome.scenes.length - failed.length}/${outcome.scenes.length} scenes made; written to ${file}`);
  process.exitCode = failed.length ? 1 : 0;
}

await main();
