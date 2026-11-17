# Laboratory: the hospital's answers of 17 November 2026

Nana Hospitals, Theni, answered HyFib's questions about its laboratory on 17 November 2026. This file lists every
answer in the hospital's own words (a misspelling or two corrected), what HC360 now does about it, what is set up at go-live, and what stays open. The
decisions were confirmed by HyFib. The server and the lab PC are in [NANA-THENI-READINESS.md](NANA-THENI-READINESS.md);
how each feature works is in [diagnostics.md](../modules/diagnostics.md) and [integrations.md](../modules/integrations.md).

Nothing is live yet, so the go-live database is seeded with everything below. An installation seeded before these
changes keeps its own catalogue (the seeder only adds what is missing): there, the new channel codes are added in
Lab setup.

## Summary

| | Answer | HC360 now | At go-live | Open |
|---|---|---|---|---|
| L1–L3 | Not answered | — | — | Ask again if they matter |
| L4 | RS-232 cable | Serial analysers through the lab agent | Set up H500 and C150 on COM ports | — |
| L5, L6 | No barcodes; 10-digit sample ids | Every tube number has 10 digits | — | — |
| L7 | High and low by an arrow | ↑ H, ↑ HH, ↓ L, ↓ LL on the report and in the app | — | — |
| L8 | QC run passed | No change | QC lots per analyser | — |
| L9 | No reruns | No change | — | — |
| L10 | "Sent to real images" | Nothing | — | What is meant |
| H500-1, H500-2, N6 | Every H500 parameter on the report | The CBC has all of them | Review the intervals | — |
| C150-1 | Every C150 parameter interfaced | Each result goes on the test its channel names | Map or ignore the channels no test maps | — |
| C150-2 | GCT on the GLU code | LAB-GCT on channel GLU | — | — |
| EXIAS e\|1 | Bicarbonate | HCO3 on the electrolytes panel | — | — |
| i-CHROMA, N10 (second part) | VIT-D, VIT D-25, VIT-D3, VIT 25 OH are one test | Channels of LAB-VITD | — | Search words on the order screen |
| N7 | Calculated parameters | IBIL, GLOB, A/G, VLDL (and LDL) calculated | — | — |
| OPTI CCA, N8 | Arterial only; FiO2; P/F, AaDO2 | LAB-ABG, with P/F and A-a calculated | Barometric pressure | — |
| Mission U120 | "-", "+-", "++" read by hand | Read as nil, trace, 2+ | — | — |
| Wondfo OCG-102 | PT, INR, APTT in seconds; normal plasma control | PT and APTT in seconds | — | The control time (proposal below) |
| N1, N2 | Lab PC on the network, on its own plug point | — | The lab PC's power | — |
| N3 | Ranges by sex | Already supported | Enter the lab's ranges | — |
| N4 | QC monthly, before patient samples, QC register | Westgard QC, lockout, register | QC lots | The QC schedule |
| N5 | Price list | Seeded tariffs | — | The hospital's price list |
| N9 | 49 tests outsourced to BIOLINE (NABL) | Referral labs | Add BIOLINE | The 49 tests; the NABL number |
| N10 (first part) | Spot, 24 hours, one order with three results | Nothing | — | Which tests |

## The analysers and the lab PC

**L1 to L3** were not answered.

**L4: "RS 232 Cable connected".** The analysers are on serial cables to the lab PC, so the lab PC runs the HC360 lab
agent, which relays each analyser's COM port to the server (DEPLOYMENT.md section 6, *Lab analysers*). At go-live, in
**Lab setup › Analysers**, add each analyser, then in its **Connection** choose *Serial cable to a lab PC*, the lab PC's
agent and the COM port Windows gives it (Device Manager › Ports; a USB-to-serial adapter keeps its number only on the
same USB socket), with the line settings the docs give
([integrations.md](../modules/integrations.md#laboratory-analysers-int-lab)):

| Analyser | ASTM dialect | Speed | Data bits, parity, stop bits | Handshake |
|---|---|---|---|---|
| HORIBA Yumizen H500 (haematology and ESR) | `YumizenH500` | 38400 baud | 8, none, 1 | None |
| HORIBA Yumizen C150 (clinical chemistry) | `YumizenC150` | 9600 baud | 8, none, 1 | None |

These are the analysers' defaults as the docs give them: check each against the analyser's own communication settings
(the handshake included), and test each analyser on site before go-live (DEPLOYMENT.md section 6). The other analysers (EXIAS e|1, i-CHROMA, OPTI CCA, Mission
U120, Wondfo OCG-102) are not interfaced: their results are entered by hand.

**N1: "For lab PC connected to network".** The lab PC is on the hospital's LAN, and reaches the server as every client
does. It runs HC360 Desktop and the lab agent (NANA-THENI-READINESS.md section 7).

**N2: "Lab PC connected to separate plug point".** Noted in NANA-THENI-READINESS.md section 7, *The lab PC*: a socket of
its own is not a UPS, and while the PC is off it relays nothing.

## Sample numbers

**L5: "No Barcode use"; L6: "Sample ID used for 10 Digits".** Every tube number HC360 gives has exactly 10 digits and no
leading zero: the series `lab.sample` is `{SEQ:10}` and starts at 1000000000 (an installation whose counter was lower
jumps to 1000000000 on upgrade). The technician writes the number on the tube and types it on the analyser as its
sample id (the H500 takes 16 characters; on the C150 it is the *Patient ID*), and each result comes back under it and
is matched to the tube by the exact number. See [numbering.md](../modules/numbering.md).

## The report

**L7: "Reports view in High, Low in Aeromark"** (an arrow mark). The PDF report prints ↑ before H and HH and ↓ before
L and LL, and the staff app's flags show the same. The letter and its words stay, so the meaning never rests on the
arrow, and the report carries the arrows' font, so every server prints them.

**L8: "Qc Run passed"; L9: "No reruns if need report clarification after rerun sample".** No change.

**L10: "Sent to real images".** Open: HC360 stores no image from an analyser (no histogram or scattergram from the H500).
Ask what is meant: the H500's histograms on the report, or something else.

## The tests

**H500-1: "Analyser sent all parameter to test code"; H500-2: "The parameter are commonly used to patient clinical
diagnosis"; N6: "PCT-Plateletcrit, PLT – Platelet All parameters are show patient reports".** The CBC (LAB-CBC) now has
every parameter the H500 reports, each on its own analyte and channel: RDW-CV, RDW-SD, MPV, plateletcrit (PCT), PDW,
P-LCR, immature granulocytes in % and as a count, and the absolute neutrophil, lymphocyte, monocyte, eosinophil and
basophil counts, beside those it had. The differential takes the H500's codes (NEU%, LYM%, MON%, EOS%, BAS%). Its
research-use parameters (ALY, LIC, MIC, MAC) and the large platelet count (P-LCC) are on no report. The new analytes
have adult intervals only, from the sources named in the seed (Dacie and Lewis, 12th ed.; Sachdev et al. 2014; Monteiro
et al. 2021), as defaults the lab reviews: the H500 counts platelets by impedance and may give PDW in %, so compare with
HORIBA's intervals for the H500.

**C150-1: "Yes, all the parameters are interface".** Every result the C150 sends goes on the test of the tube whose
channel it names. A parameter none of the seeded tests maps is listed in Lab setup › Analysers among the *channels no
test maps* ([diagnostics.md](../modules/diagnostics.md)), with the mapping to add, from the first day the C150 sends
it: map each to its test, or ignore it.

**C150-2: "GCT sample marked for –GLU code not one code".** LAB-GCT, "Glucose challenge test (50 g, 1 hour)", ₹80, is
read on the C150's glucose channel GLU. It is fluoride plasma drawn into its own tube, as the post-prandial sugar is,
so a GLU result goes on the test the tube was drawn for. 140 mg/dL or more flags high (ACOG Practice Bulletin 190; the
ADA's two-step approach).

**EXIAS e|1: "Manual measure the all reports, bicarbonate is automated in machine".** Bicarbonate (HCO3, mmol/L, adult
22 - 29 after Tietz, 5th ed.) is on the electrolytes panel (LAB-ELECTROLYTES), entered by hand as its other tests are,
with the channel `HCO3` should the analyser ever be interfaced.

**i-CHROMA: "Same test name for VIT-D, VIT D-25, VIT-D3"; N10: "VIT-D, VIT 25 OH, VIT D3 are same test name".** One test,
LAB-VITD, with every name as a channel: `VITD`, `VIT-D`, `VIT D-25`, `VIT-D3`, `VIT D3`, `VIT 25 OH`. Open: the order
screen finds tests by code, name and short name only and has no search words, so typing "VIT 25 OH" there finds
nothing; "vitamin D" or "VIT D" does.

**N7: "Some Parameters are not run which mean calculated for exp: indirect bilirubin, globulin, a/g ratio, vldl".** HC360
calculates them as soon as their inputs on the same sample are entered or come from an analyser: indirect bilirubin =
total − direct; globulin = total protein − albumin; A/G ratio = albumin ÷ globulin; VLDL = triglycerides ÷ 5 below
400 mg/dL of triglycerides (Friedewald). It calculates LDL by Friedewald too, as the seed names it "LDL cholesterol
(calculated)": HyFib to confirm the lab reports it so. Each is rounded to the test's decimals, flagged against its
interval, shown to the verifier with its formula and inputs, never verified automatically, calculated again when an
input changes, and taken back to unverified when an input is corrected after verification. A value the analyser
measures or the technician types for it stands instead.

**OPTI CCA: "Sample type Artrial only, FIO2 if need patient give value"; N8: "Only need FIO2 patients contion, P/F,
AaDo2 can be reported".** LAB-ABG, "Arterial blood gas (ABG)", ₹1,200 (a default tariff): pH, pCO2, bicarbonate, base
excess, pO2, sO2 and FiO2, on arterial blood in a heparinised syringe, each drawn on its own. The FiO2 is entered for
the sample: the result sheet fills in 21 (room air), which the technician changes if the patient was on oxygen, and
it is saved only when they save. HC360 never assumes it. From it HC360 calculates the P/F ratio = pO2 ÷ (FiO2/100) and
the A-a gradient (AaDO2) = (FiO2/100 × (Patm − 47) − pCO2/0.8) − pO2. The OPTI CCA is not interfaced, so the results are
entered by hand; each measured analyte has the channel a blood gas analyser names it by. The adult intervals and
critical limits are cited in diagnostics.md (*Seed data*); the sO2 interval, 95 - 100 %, has no cited source yet.
At go-live, set **`lab.barometricPressureMmHg`** (Administration › Settings, for the facility) to the laboratory's
pressure: about **733** at Theni, some 300 m above the sea, against the default 760 for sea level. If the OPTI CCA
shows the barometric pressure it measures, use that value.

**Mission U120: "Not lis"; "negative –( - ), trace – (+-), ++ -( 2+)".** A urine is entered by hand with the strip's
words: "-" is read as Nil (or Negative, or Absent, whichever the test offers), "+-" as Trace and "++" as 2+. On the
result sheet, those keys typed into a test's answer list choose its answer; a value with those words entered through
the API, or sent by a strip reader, is read the same way.

**Wondfo OCG-102: "PT, INR, APTT reports are measured at seconds. yes, once normal plasma compare to value for
critical".** PT and APTT are reported in seconds, and INR as a ratio, as they were. Open: a "Control" time printed beside
the patient's PT and APTT. HC360 has nowhere to keep it today, so it was not done. The proposal:

- The control time belongs to a reagent or control lot and changes with it, so keep it with the lot: in Lab › QC, a
  normal-plasma lot of PT or APTT marked "print as the report's control" (one new column, a migration), its target mean
  being the normal plasma time the lab established for that lot.
- The report prints, beside the patient's PT and APTT, "Control: 12.8 s" from the lot in use on the day the sample was
  tested on that analyser, on the PDF and in the staff app's preview, with tests of both.
- Optionally the lot keeps the reagent's ISI too, so that HC360 can calculate INR = (PT ÷ control)^ISI where the
  analyser does not report it (the OCG-102 does).

## Ranges, QC and prices

**N3: "Normal ranges are insert software mension to sex difference value".** Already supported: each test's intervals
are by sex and by age (Lab setup › Tests), and the lab enters its own. The seeded intervals are defaults to review,
first those added now (the H500's parameters, bicarbonate, the GCT cut-off and the blood gas).

**N4: "Once QC run monthly 4 days check, QC passed then patient sample run, QC run for Lab incharge in our lab and
maintenance QC Register".** HC360's QC (Lab › QC): QC lots per test and analyser with their target mean and SD, each run
entered and checked against the Westgard rules (1-2s warning; 1-3s, 2-2s, R-4s, 4-1s and 10x reject), Levey-Jennings
charts, and a review of each run, with a corrective action for a rejected one. While the day's latest run of an assay
is rejected, its results are verified only with a pathologist's audited override, and an analyser's results are
verified automatically only when that analyser passed QC on the assay that day. The runs, reviews and corrective
actions are the QC register. Runs are entered by hand: the analysers' own QC files are not imported. At go-live, set
up the QC lots of each analyser. Open: the QC schedule. HC360 does not say when QC is due, nor hold patient results
when no QC was run that day. Ask what "monthly 4 days check" means (four QC days a month, or QC kept for four days?),
and whether results should wait for the day's QC.

**N5: "Lab price list added in default in software".** Open: the hospital's price list was not received. Until it is,
the tests carry the seeded tariffs (2026 tariffs of a tier-2 city private hospital), which the hospital replaces in
the service master.

**N9: "Out source entry list is 49", to the NABL lab BIOLINE.** At go-live, add BIOLINE in **Lab setup › Referral labs**
(code, name, contact, NABL accreditation number, agreed turnaround in days), and make it the default referral lab of
each outsourced test (Lab setup › Tests). The tubes of an outsourced test are sent out with a dispatch reference, and
its report is received back against them (Lab › Outsourcing). Open: the list of the 49 tests, and BIOLINE's NABL
certificate number.

**N10: "This to separate tests, spot, 24 hours, one order with three results".** Open: ask which tests are meant, and
what the three results are (a spot and a 24-hour sample of the same urine test, and their ratio?).
