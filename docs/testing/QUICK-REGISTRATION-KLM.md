# Quick registration time: Keystroke-Level Model (SRS FO-01)

FO-01 asks for a quick registration within 60 seconds. A stopwatch at the desk is the real measure; until the
hospital times it at acceptance, a test predicts it with the Keystroke-Level Model (KLM) of Card, Moran and Newell
(*The Psychology of Human-Computer Interaction*, 1983, chapter 8), and fails the build if the prediction passes 60 s.

The test is [quickRegistration.klm.test.tsx](../../src/web/apps/staff/src/modules/patients/quickRegistration.klm.test.tsx);
the counting is [klm.ts](../../src/web/apps/staff/src/test/klm.ts).

## What is timed

The registration screen (`/patients/register`), whole and with the server faked, from the empty form (the cursor is
in *First name*, as the screen opens) to the UHID on screen, for a typical walk-in patient: Mrs Sunita Kulkarni,
female, 46 years (age, not date of birth), mobile 9876543210. Finding the patient first (to avoid a duplicate) and
checking in afterwards are separate tasks; UX-06's journey test counts their screens.

## How it is counted

The test drives the form with user-event through a wrapper that tallies each action as the operators a person needs
for it. Nothing is a hand-written total: change the form or the steps and the counts change with them.

| Operator | Seconds | Counted when |
|---|---|---|
| K, a keystroke | 0.28 | Each key typed: every character, each named key (`Tab`), a held modifier (`Ctrl`) once. 0.28 s is CMN's average non-secretary typist (about 40 words a minute). |
| P, pointing with the mouse | 1.1 | Each click on a field, radio button, list or list option. |
| B, a mouse button press or release | 0.1 | Two per click (press and release). |
| H, homing between keyboard and mouse | 0.4 | Each time the hand must move to the other device for the next action. |
| M, mental preparation | 1.35 | One per field to fill and one per decision, placed by CMN's rule 0 (before each unit of typing and each choice) with rules 1–4 removing the M's inside one unit: a name or a number typed in one go has one M. |

System response time (R) is left out: the duplicate check runs while the clerk types and does not wait for them, and
the save answers within the 600 ms PERF-01 sets for writes.

## What the model predicts

| Step | Operators | Seconds |
|---|---|---|
| Field: first name | M 6K | 3.03 |
| Field: last name (two Tabs past the middle name) | M 10K | 4.15 |
| Decision: title (Mrs, from the list) | M H P 2B P 2B | 4.35 |
| Decision: sex | M P 2B | 2.65 |
| Decision: age, not date of birth | M P 2B | 2.65 |
| Field: age in years | M P 2B H 2K | 3.61 |
| Field: mobile number | M H P 2B H 10K | 6.25 |
| Decision: check and save (Ctrl+S) | M 2K | 1.91 |
| **Total** | 30 K, 6 P, 12 B, 4 H, 8 M | **28.60** |

28.6 s, under half the 60 s allowed. The test prints this table on every run, and a failure shows it with the
assertion.

## What it does not show

The KLM predicts an expert's error-free time. It does not include talking to the patient, spelling a name back,
correcting a typo, a slow network or a clerk new to the screen. FO-01's acceptance is therefore still the hospital's:
time a few registrations at the desk with a stopwatch, from the empty form to the UHID, with the clerks who will use
it, and record the times with the acceptance results.
