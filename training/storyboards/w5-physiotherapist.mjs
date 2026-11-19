// Wave 5 (in-patient services): the physiotherapist's day. A doctor's referral for Lakshmi Narayanan (knee pain, seen
// in Dr. Arjun Mehta's OPD this morning) is made in setup, as the doctor would make it. The video assesses her, writes
// the treatment plan, books the sessions and records the first one from the worklist. Recorded as the physiotherapist
// at 11:10 am.

import { writeFileSync } from 'node:fs';
const dump = (name) => async ({ page }) => writeFileSync(`/tmp/claude-0/-home-user-HyFib-HealthCare360-NanaHospitals/4cf56889-54f3-5e4e-8287-5c222ea81477/scratchpad/${name}.txt`, await page.locator('body').ariaSnapshot());

const nav = { role: 'navigation', name: 'Main navigation' };

export default {
  id: 'w5-physiotherapist',
  title: 'Physiotherapist: assess, plan, book and record sessions',
  role: 'Physiotherapist',
  roleLabel: 'Physiotherapist',
  user: 'physio',
  wave: 5,
  audience: 'For the physiotherapists of Nana Hospitals.',
  intro:
    'Doctors refer patients to physiotherapy from HC360. You assess the patient, write the treatment plan, book the sessions and record each session on the day. HC360 bills the treatment given.',
  objectives: [
    'Open a referral and record the assessment.',
    'Write the treatment plan and book the sessions.',
    'Record a session from today’s worklist.',
  ],
  modules: ['physio'],
  covers: ['/physio', '/physio/referrals'],
  setup: async ({ api }) => {
    // Dr. Arjun Mehta's plan for Lakshmi Narayanan (knee pain) says "physiotherapy referral": the doctor makes it.
    const doctor = await api('doctor');
    const frontOffice = await api('frontoffice');
    const found = await frontOffice.get('/api/v1/patients?search=Lakshmi%20Narayanan&pageSize=5');
    const patient = (found.items ?? found).find((p) => p.fullName.includes('Lakshmi'));
    const practitionerId = doctor.me.practitionerId ?? (await doctor.get('/api/v1/auth/me')).practitionerId;
    await doctor.post('/api/v1/physio/referrals', {
      patientId: patient.id,
      visitId: null,
      admissionId: null,
      referredByPractitionerId: practitionerId,
      priority: 'Routine',
      reason: 'Osteoarthritis of both knees: pain on climbing stairs. Strengthen the thigh muscles and improve walking.',
      sessionsOrdered: 6,
    });
  },
  scenes: [
    {
      title: 'Open the referral and assess the patient',
      steps: [
        {
          click: { within: nav, role: 'button', name: 'Physiotherapy' },
          say: 'Open Physiotherapy from the menu.',
        },
        {
          nav: 'Physiotherapy referrals',
          say: 'Open Physiotherapy referrals.',
          after:
            'Dr. Arjun Mehta has referred Lakshmi Narayanan for osteoarthritis of both knees. Her referral is New, to be assessed.',
          wait: { role: 'row', name: /Lakshmi Narayanan/ },
          result: { role: 'row', name: /Lakshmi Narayanan/ },
        },
        {
          click: { role: 'row', name: /Lakshmi Narayanan/ },
          say: 'Click her referral.',
          wait: { role: 'button', name: 'Record assessment' },
        },
        {
          show: { role: 'group', name: 'Next' },
          say: 'HC360 tells you the next step. Treatment is never planned without an assessment.',
          zoom: 1.4,
        },
        {
          click: { role: 'button', name: 'Record assessment' },
          say: 'Click Record assessment.',
          wait: { role: 'dialog', name: /Record assessment/ },
        },
        {
          fill: [{ label: 'Pain now (0–10)' }, '6'],
          say: 'Ask Mrs. Narayanan how bad the pain is, from zero to ten. She says six.',
          zoom: 1.3,
        },
        {
          fill: [{ label: 'Pain location' }, 'Both knees'],
          say: 'Write where it hurts.',
          zoom: 1.3,
        },
        {
          fill: [{ label: 'Chief complaint' }, 'Pain in both knees on climbing stairs for six months.'],
          say: 'Write her complaint in her own words.',
          zoom: 1.3,
        },
        {
          fill: [{ label: 'Functional status' }, 'Walks 200 metres. Stairs are painful.'],
          say: 'Write what she can do now.',
          zoom: 1.3,
        },
        {
          fill: [{ label: 'Goals' }, 'Climb stairs with less pain.'],
          say: 'Write what she wants to achieve.',
          zoom: 1.3,
        },
        {
          click: { role: 'button', name: 'Save assessment' },
          say: 'Click Save assessment.',
          after: 'The assessment is saved with your name and the time. The referral is now Assessed, and a plan is needed.',
          wait: { role: 'button', name: 'Write treatment plan' },
        },
      ],
    },
    {
      title: 'Write the plan and book the sessions',
      steps: [
        {
          click: { role: 'button', name: 'Write treatment plan' },
          say: 'Click Write treatment plan.',
          wait: { role: 'dialog', name: 'Treatment plan' },
        },
        {
          click: { role: 'checkbox', name: 'Exercises' },
          say: 'Choose the modalities you will use. First, Exercises.',
          zoom: 1.3,
        },
        {
          click: { role: 'checkbox', name: 'Hot pack' },
          say: 'Then Hot pack.',
          zoom: 1.3,
        },
        {
          fill: [{ label: 'Exercise programme' }, 'Quadriceps strengthening, twice a day.'],
          say: 'Write the exercise programme. Sessions planned is six, the number the doctor prescribed.',
          zoom: 1.3,
        },
        {
          click: { role: 'button', name: 'Save plan' },
          say: 'Click Save plan.',
          after: 'The plan is saved. HC360 now asks you to book the sessions.',
          wait: { role: 'button', name: 'Book sessions' },
        },
        {
          click: { role: 'button', name: 'Book sessions' },
          say: 'Click Book sessions.',
          wait: { role: 'dialog', name: 'Book sessions' },
        },
        {
          click: { label: 'First session' },
          after_do: async ({ page }) => {
            await page.getByLabel('First session').fill('2026-11-19T14:00');
          },
          say: 'Choose the date and time of the first session. Here, it is today at 2:00 pm.',
          after: 'HC360 lists every session it will book.',
          wait: { text: /^1\. /, exact: false },
          zoom: 1.3,
        },
        {
          click: { role: 'button', name: 'Book sessions', last: true },
          say: 'The plan allows six sessions. Click Book sessions.',
          after: 'Six sessions are booked. The first one is today.',
          wait: { role: 'heading', name: 'Sessions (6)' },
        },
      ],
    },
    {
      title: 'Record a session',
      steps: [
        {
          nav: 'Physiotherapy worklist',
          say: 'On the day, open the Physiotherapy worklist.',
          after: 'Today’s sessions are listed by time. Lakshmi Narayanan is booked for 2:00 pm.',
          wait: { role: 'row', name: /Lakshmi Narayanan/ },
          result: { role: 'row', name: /Lakshmi Narayanan/ },
        },
        {
          click: { within: { role: 'row', name: /Lakshmi Narayanan/ }, role: 'button', name: /Record/ },
          say: 'When she has had the treatment, click Record.',
          wait: { role: 'dialog', name: /Record session/ },
          after_do: dump('py-a'),
        },
        {
          fill: [{ label: 'Pain before (0–10)' }, '6'],
          say: 'Write her pain before the treatment.',
          zoom: 1.3,
        },
        {
          fill: [{ label: 'Pain after (0–10)' }, '4'],
          say: 'And her pain after it.',
          zoom: 1.3,
        },
        {
          fill: [{ label: 'Progress note' }, 'Hot pack and quadriceps exercises given. Tolerated well. Home exercises explained.'],
          say: 'Write a progress note: the treatment given, how she responded, and the home exercises.',
          zoom: 1.3,
        },
        {
          click: { role: 'button', name: 'Record session' },
          say: 'Click Record session.',
          after: 'The session is recorded. HC360 bills the modalities given.',
          wait: { text: /Attended/ },
          after_do: dump('py-b'),
        },
      ],
    },
  ],
  recap: [
    'Assess the patient before you plan. HC360 shows the next step on the referral.',
    'The plan holds the modalities and the number of sessions. Book the sessions from it.',
    'Record each session on the day, with the pain before and after, and a progress note.',
  ],
  check: [
    {
      q: 'Why does HC360 ask for an assessment before the treatment plan?',
      a: 'Treatment is never planned without an assessment. The assessment records the pain, the complaint and the goals the plan works towards.',
    },
    {
      q: 'Where do you record a session on the day it is given?',
      a: 'On the Physiotherapy worklist: click Record on the patient’s row, write the pain before and after, and the progress note.',
    },
  ],
  next: 'Doctor: refer a patient to physiotherapy',
};
