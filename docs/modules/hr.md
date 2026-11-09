# HR module (HRMS + biometric attendance)

Project: `src/server/Modules/HyFib.HealthCare360.Modules.HR` · schema `hr` · depends on Platform only (layer 2).
Covers SRS **HR-01..08** (employee master, designations, shift rostering, biometric attendance, leave, payroll with
PF/ESI/PT/TDS, payslips, credential expiry) and **INT-BIO** (ZKTeco-style attendance terminals).

## Permissions

| Permission | Default roles | Purpose |
|---|---|---|
| `hr.employees.read` | HrManager, HospitalAdmin | View employees, designations, credentials |
| `hr.employees.manage` | HrManager, HospitalAdmin | Add/edit employees, bank details, designations, leave policies; separation |
| `hr.attendance.manage` | HrManager, HospitalAdmin | Terminals, attendance, holidays, regularisation approvals |
| `hr.roster.manage` | HrManager, HospitalAdmin | Shifts and duty roster |
| `hr.leave.approve` | HrManager, HospitalAdmin | Approve leave for anyone; credit/adjust balances |
| `hr.payroll.process` | HrManager, HospitalAdmin | Salary structures, inputs, processing |
| `hr.payroll.approve` | HrManager, HospitalAdmin | Approve/reopen/pay/lock payroll, bank file, PT slabs |
| `hr.credentials.manage` | HrManager, HospitalAdmin | Licences/certifications; receives expiry alerts |
| `hr.self` | all staff roles | Self-service and reporting-manager approvals |

Reporting managers approve their direct reportees' leave and regularisations through `/me/team/...` with `hr.self`.
The service re-checks the reporting line. HR (`hr.leave.approve` / `hr.attendance.manage`) can decide requests only
for employees of the facility they are working in. Nobody can approve their own request. Payroll follows
maker-checker: the approver must be a different user from the processor.

**Maker-checker (slice 11, MAKER-CHECKER.md).** These wait in the approvals inbox (`platform.approval_requests`) for a
second person, never the one who asked (`approval.self`) and never the employee whose pay or leave it is; until approved,
what is in force stays in force, and the approval is audited with both names:

| Kind | Asked by | Approved by | What waits |
|---|---|---|---|
| `hr.bank_details` | `hr.employees.manage` | `hr.payroll.approve`, not the employee (`hr.bank_details_own`) | A salary's bank account, IFSC, bank or payment mode (cash, cheque, bank transfer); a new employee's first account too. Audited with the account masked to its last four digits; the employee is told |
| `hr.salary_structure` | `hr.payroll.process` | `hr.payroll.approve` | A salary structure (revision); made only over the latest structure as asked (`approval.stale`) |
| `hr.payroll_input` | `hr.payroll.process` | `hr.payroll.approve` | An additional earning, a loss-of-pay override or a TDS override. A deduction is added at once |
| `hr.payroll_import` | `hr.payroll.process` | `hr.payroll.approve` | An imported file with an earning in it, as a whole. A file of deductions only is imported at once |
| `hr.payroll_input_remove` | `hr.payroll.process` | `hr.payroll.approve` | A deduction another module posted (canteen credit), or another person entered, taken off. One's own entry, and an earning or override, are removed at once |
| `hr.leave_adjust` | `hr.leave.approve` | `hr.leave.approve`, not the employee (`hr.leave_own`) | A manual leave credit. A debit is made at once; the yearly credit (by rule) stays automatic |

Nobody makes a change to their own pay or salary structure, nor approves one (403 `hr.pay_own`), nor adjusts their own
leave balance or decides their own leave request (403 `hr.leave_own`). `GET /api/v1/hr/payroll/pending-changes?runId&employeeId`
and `GET /api/v1/hr/leave/balances/pending?employeeId` list what waits; `EmployeeDetail.pendingPaymentDetails` the
payment details asked for.

## Endpoints

All list endpoints accept `page, pageSize, search, sort, desc` and return `{ items, page, pageSize, totalCount }`.

### Employees and masters
| Method | Path | Permission | Body → Response |
|---|---|---|---|
| GET | `/api/v1/hr/employees` | employees.read | filters `departmentId, designationId, status, employmentType, includeSeparated`; sort `code,name,joiningDate,department` → `EmployeeListItem` (phone masked) |
| GET | `/api/v1/hr/employees/{id}` | employees.read | → `EmployeeDetail` (bank account, IFSC and PAN masked only) |
| POST | `/api/v1/hr/employees` | employees.manage | `SaveEmployeeRequest` (optional `bank`) → 201 `EmployeeCreated {id, employeeCode, bankApprovalId}`: the payment mode is saved, a bank account given waits (`hr.bank_details`) |
| PUT | `/api/v1/hr/employees/{id}` | employees.manage | `SaveEmployeeRequest` (`version` required; `pan`: null = keep, "" = remove) → 204; a payment mode other than the one in force is asked for in the same save → 202 the approval request (`hr.bank_details`), the rest saved |
| PUT | `/api/v1/hr/employees/{id}/bank-details` | employees.manage | `UpdateBankDetailsRequest {paymentMode, bankName, accountNumber, ifsc, confirmSharedAccount, reason, version}` (no account keeps the one on record) → 202 the approval request (`hr.bank_details`); 409 `employee.bank_account_shared` when another current employee is paid into the same account, `approval.already_requested` while a change waits |
| POST | `/api/v1/hr/employees/{id}/separate` | employees.manage | `SeparateEmployeeRequest {lastWorkingDay, reason, note, version}` → 204 |
| GET/POST/PUT | `/api/v1/hr/designations[/{id}]` | read: employees.read or .manage; write: employees.manage | `SaveDesignationRequest {code, name, category, grade, isActive, version}` |
| GET | `/api/v1/hr/credentials` | credentials.manage or employees.read | `expiringWithinDays, type, departmentId` → `CredentialDto` (with `stage`, `daysToExpiry`), soonest expiry first |
| GET/POST | `/api/v1/hr/employees/{employeeId}/credentials` | GET: credentials.manage or employees.read; POST: credentials.manage | `SaveCredentialRequest {type, name, number, issuingAuthority, issuedOn, expiresOn, fileId, notes}` |
| PUT/DELETE | `/api/v1/hr/credentials/{id}` | credentials.manage | changing `expiresOn` restarts the reminder cycle |

### Shifts and roster
| Method | Path | Permission | Body → Response |
|---|---|---|---|
| GET | `/api/v1/hr/shifts` | roster.manage, attendance.manage or employees.read | → `ShiftDto[]` |
| POST/PUT | `/api/v1/hr/shifts[/{id}]` | roster.manage | `SaveShiftRequest {code, name, startTime, endTime, breakMinutes, graceMinutes, isNightShift, halfDayMinutes?, fullDayMinutes?, overtimeThresholdMinutes, color}` |
| GET | `/api/v1/hr/roster?from&to&departmentId&employeeId` | roster.manage or attendance.manage | ≤ 62 days → `RosterEntryDto[]` |
| POST | `/api/v1/hr/roster/check` | roster.manage | `SaveRosterRequest` → `RosterCheckResult {canSave, hasWarnings, conflicts}` (dry run) |
| PUT | `/api/v1/hr/roster` | roster.manage | `SaveRosterRequest {assignments[{employeeId,date,shiftId|isWeekOff,notes}], replaceExisting=true, acknowledgeWarnings}` → `RosterSaveResult`; 409 `roster.conflicts` (errors) or `roster.warnings` (acknowledge to save), `data` = `RosterConflictDto[]` |
| POST | `/api/v1/hr/roster/copy-week` | roster.manage | `CopyRosterWeekRequest {sourceWeekStart, targetWeekStart, departmentId, acknowledgeWarnings}` |
| DELETE | `/api/v1/hr/roster/{id}` | roster.manage | 204 |

Conflict rules (`RosterConflictDetector`): overlapping duties, a shift and a week off on the same day, duplicates,
and rostering outside employment are **errors**. Less than 8 h rest between duty days and duty during approved leave
are **warnings**. A contiguous double duty on one date is allowed.

### Biometric attendance (INT-BIO)

INT-BIO's other parts are elsewhere: checking a patient's identity with their Aadhaar fingerprint is in the Patients
module ([clinical.md](clinical.md#identity-checks-int-bio)); staff fingerprint sign-in at workstations is not built,
because it needs an Aadhaar AUA licence and an Aadhaar Data Vault (see
[integrations.md](integrations.md#not-implemented-here)). Attendance terminals match fingerprints on the terminal and
send HC360 only the punch.

| Method | Path | Permission | Notes |
|---|---|---|---|
| GET | `/api/v1/hr/biometric-devices` | attendance.manage | `BiometricDeviceDto` incl. `isOnline` (heard from in the last 10 min; a terminal HC360 reads: read successfully within its quiet window, below), `connection`, `pullAddress`, `pullPort`, `pullEveryMinutes`, `lastPullAt`, `lastPullError`, `clockOffsetSeconds`, `offlineSince` |
| GET | `/api/v1/hr/biometric-devices/reading` | attendance.manage | → `AttendanceTerminalReading {provider, problem}`: how this facility's terminals are read (`zkteco`, `simulator`), or why they cannot be |
| POST | `/api/v1/hr/biometric-devices` | attendance.manage | `SaveBiometricDeviceRequest {serialNumber, name, location, departmentId, model, allowedIpAddress, isActive, connection (Push \| Pull), pullAddress, pullPort (4370), pullEveryMinutes (10, at most 1440)}` → 201 `DeviceKeyIssued {deviceId, serialNumber, apiKey}`; the key is shown once and stored as a SHA-256 hash. A terminal HC360 reads gets no key (`apiKey` null) |
| PUT | `/api/v1/hr/biometric-devices/{id}` | attendance.manage | serial number is immutable |
| POST | `/api/v1/hr/biometric-devices/{id}/rotate-key` | attendance.manage | → new `DeviceKeyIssued` (audited) |
| POST | `/api/v1/hr/biometric-devices/{id}/read` | attendance.manage | Reads the terminal now → `TerminalPullResult {success, error, read, earlier, punches: PunchIngestResult, clockOffsetSeconds}` |
| POST | `/api/v1/hr/biometric-devices/{id}/set-clock` | attendance.manage | Sets a terminal HC360 reads to the hospital's time → `TerminalClockResult {success, error}`; audited as `biometric_device.clock_set` |
| POST | `/api/v1/hr/biometric-devices/{id}/training-punch` | attendance.manage | `TrainingPunchRequest {pin}` → 204: a punch now on the training terminal (simulator only); 422 `biometric.training_punch` with the reason otherwise |
| GET | `/iclock/cdata?SN=…&options=all` | **anonymous**, rate limit `device` (per serial number) | ADMS handshake (text). Requests attendance logs only; operation logs and photos are suppressed and fingerprint templates are never requested |
| POST | `/iclock/cdata?SN=…&table=ATTLOG&Stamp=…` | **anonymous**, `device` | body `PIN\tYYYY-MM-DD HH:MM:SS\tstatus\tverify…` per line, ≤ 1 MB → `OK: n`; other tables → `OK` (not stored) |
| GET | `/iclock/getrequest?SN=…` | **anonymous**, `device` | → `OK` (HC360 never sends terminal commands) |
| POST | `/api/v1/hr/attendance/device-punches` | **anonymous**, `device` (per key); headers `X-Device-Serial`, `X-Device-Key` | `DevicePunchBatch {punches[{pin, punchedAt (ISO with offset), direction?, verifyMode?}]}` (≤ 1000) → `PunchIngestResult {accepted, duplicates, unknownPins, rejected}` |

Only registered, active serial numbers are served. A device can optionally be pinned to one IP address. Unknown
serials get 403, and the attempt is written to `integrations.integration_logs`. Punches are de-duplicated per device.
Punches more than 10 min in the future or more than 90 days old are refused. Punches from PINs that are not yet
mapped are kept and linked when HR assigns the PIN. Terminal times are interpreted in IST.

**Terminals HC360 reads (pull).** A terminal that cannot push is registered with `connection: Pull`, its IP address
on the device network and port. `BiometricPullService` reads it through the Platform's `IAttendanceTerminalClient`
(ZKTeco/eSSL protocol on TCP 4370, or the training simulator; see
[integrations.md](integrations.md#biometric-attendance-terminals-int-bio), settings `integrations.biometric`):

* `BiometricPullJob` reads every active terminal whose interval has passed, once a minute at most, four terminals at a
  time so an unreachable one waiting out its timeout does not hold up the others; Read now does the same at once. One
  read of a terminal runs at a time, across nodes (advisory lock per terminal).
* A terminal keeps every punch, so a read returns them all. Punches from more than two days before the latest one
  already read (`pulled_up_to`), or more than 90 days old, are left out (`earlier`); the rest go through the same intake
  as pushed punches (source `Pull`), which leaves out those already received.
* After a read, `clockOffsetSeconds` says how far the terminal's clock is out. More than two minutes is pointed out on
  the screen, and HR can set the terminal's clock to the hospital's time.
* A read that fails keeps its reason in `lastPullError`. Reads update the terminal with set-based updates, as pushes do,
  so reading every few minutes does not fill the audit trail.
* Addresses are IP addresses only, and never a loopback, link-local (cloud metadata), multicast or broadcast address.
* A terminal set to be read sends nothing, so it gets no device key, and punches pushed in its name (ADMS or the punch
  API, even with a key it had while it pushed) are refused and logged as `device.refused`.

**Quiet terminals.** An active terminal that was working and is not heard from for 30 minutes (a terminal HC360 reads:
two missed reads plus five minutes, if longer) is reported once to `hr.attendance.manage` holders of its facility, with
why when a read failed, and logged as `terminal.quiet`. `offlineSince` records it; it clears, and `terminal.back` is
logged, as soon as the terminal calls or is read again.

### Attendance and regularisation
| Method | Path | Permission | Body → Response |
|---|---|---|---|
| GET | `/api/v1/hr/attendance?from&to&employeeId&departmentId&status` | attendance.manage | → `AttendanceDayDto` (status, first in/last out, worked/late/early/OT minutes, day fractions, flags) |
| GET | `/api/v1/hr/attendance/summary?year&month&departmentId` | attendance.manage | → `AttendanceSummaryRow[]` (the pre-payroll check sheet) |
| POST | `/api/v1/hr/attendance/compute` | attendance.manage | `ComputeAttendanceRequest {from, to (≤ today, ≤ 62 days), employeeId?}` → `{employeeDays}` |
| GET | `/api/v1/hr/attendance/punches?employeeId&from&to` | attendance.manage | ≤ 31 days → `PunchDto[]` |
| GET | `/api/v1/hr/attendance/unmatched-pins` | attendance.manage | PINs seen on terminals without an employee |
| GET | `/api/v1/hr/attendance/regularisations?status` | attendance.manage | → `RegularisationDto` |
| POST | `/api/v1/hr/attendance/regularisations/{id}/approve` · `/reject` | attendance.manage (or the reporting manager via `/me/team`) | `DecisionRequest {note}` (note required to reject); approval recomputes the day |
| POST | `/api/v1/hr/employees/{employeeId}/regularisations` | attendance.manage | `RequestRegularisationRequest {date, kind: MissedPunch|OnDuty, requestedIn, requestedOut, reason}` |

Daily rules (`AttendanceCalculator`, unit-tested): first punch is in and last punch is out. Worked time is the span
minus the shift break. A single punch is a missed punch (absent until regularised). Worked time ≥ full-day minutes
is Present, ≥ half-day minutes is HalfDay, otherwise Absent. Late is counted from shift start once the grace period
is exceeded, and early exit likewise. Overtime is worked time beyond the net shift once it passes the threshold. All
work on a holiday or week off is overtime. Approved leave, including half days, and on-duty override punches. The
fractions `presentDays + paidLeaveDays + unpaidLeaveDays + absentDays + offDays` always add up to 1 and drive
loss-of-pay. Night shifts are handled by `PunchAllocator` (each day owns a punch window around its shift).
Day planning order: rostered shift (wins even on holidays), rostered week off, holiday, fixed weekly off, default
shift.

### Leave and holidays
| Method | Path | Permission | Body → Response |
|---|---|---|---|
| GET | `/api/v1/hr/leave/types` | hr.self, leave.approve or employees.read | → `LeaveTypeDto[]` |
| POST/PUT | `/api/v1/hr/leave/types[/{id}]` | employees.manage | `SaveLeaveTypeRequest {code, name, annualQuota, carryForwardCap, isEncashable, isPaid, allowHalfDay, tracksBalance, countsNonWorkingDays, maxDaysPerRequest, documentRequiredAfterDays, applicableGender, isActive, inServiceEncashmentDaysPerYear, minimumBalanceAfterEncashment}` |
| GET | `/api/v1/hr/holidays?year` | hr.self, attendance.manage or leave.approve | facility holidays → `HolidayDto[]` |
| POST/DELETE | `/api/v1/hr/holidays[/{id}]` | attendance.manage | `SaveHolidayRequest {date, name, kind: National|Festival|Restricted}` (restricted holidays are working days) |
| GET | `/api/v1/hr/leave/balances?employeeId&year` | leave.approve or employees.read | → `LeaveBalanceDto {opening, credited, adjusted, availed, encashed, closing, pending, available}` |
| POST | `/api/v1/hr/leave/balances/credit` | leave.approve | `CreditLeaveRequest {year}` → `CreditLeaveResult`; carry-forward (capped) + pro-rated quota; re-runnable |
| POST | `/api/v1/hr/leave/balances/adjust` | leave.approve | `AdjustLeaveBalanceRequest {employeeId, leaveTypeId, year, days (±, half days), reason}` (comp-off credits, corrections; audited): a debit → 204; a credit → 202 (`hr.leave_adjust`); one's own → 403 `hr.leave_own` |
| GET | `/api/v1/hr/leave/balances/pending?employeeId` | leave.approve or employees.read | → `PendingLeaveAdjustment[]` |
| POST | `/api/v1/hr/leave/encashments` | payroll.process | `EncashLeaveRequest {employeeId, leaveTypeId, year, days, payYear, payMonth, reason}` → 202 (`hr.leave_encash`), approved by someone else who holds payroll.approve; one's own → 403 `hr.pay_own`; refused by the policy `leave.encash_not_allowed` / `leave.encash_over_cap` / `leave.encash_below_minimum`, for a leaver `leave.encash_leaving`, for an approved month `payroll.closed` |
| GET | `/api/v1/hr/leave/encashments?employeeId` · `/pending?employeeId` | payroll.process, payroll.approve or employees.read | → `LeaveEncashmentDto[]` (in service and on leaving) · `PendingLeaveEncashment[]` |
| GET | `/api/v1/hr/leave/encashments/encashable?employeeId&year` | payroll.process or payroll.approve | → `EncashableLeaveDto[]`: per type that pays out in service, the cap, encashed this year, waiting, the most days now and the daily rate |
| GET | `/api/v1/hr/leave/requests?status&employeeId&from&to` | leave.approve | → `LeaveRequestDto` (pending first) |
| POST | `/api/v1/hr/employees/{employeeId}/leave-requests` | leave.approve | `ApplyLeaveRequest {leaveTypeId, fromDate, fromSession, toDate, toSession, reason, attachmentFileId}` (HR on behalf) |
| POST | `/api/v1/hr/leave/requests/{id}/approve` · `/reject` · `/cancel` | leave.approve | `DecisionRequest` / `CancelLeaveRequest {reason}` |

Rules: days exclude holidays and week offs (fixed or rostered) unless the type counts calendar days (maternity).
Starting in the second half or ending in the first half removes half a day. Pending requests reserve balance.
Overlapping requests are refused. The balance is re-checked at approval. Documents are required above
`documentRequiredAfterDays`. Leave cannot cross the new year. Decisions on past dates recompute attendance
immediately.

**Leave encashment (HR-05).** A leave type that is encashable is paid out on leaving, in the full and final settlement;
it is paid out while in service only when the type names `inServiceEncashmentDaysPerYear` (the yearly cap; none
seeded, so each hospital opts in). An in-service encashment is asked for by HR who process payroll and approved by
someone else who approves payroll, never the employee. It is checked against the policy when asked and again when
approved (`LeaveEncashmentPolicy`): whole days unless the type allows half days; what was encashed in service this year
plus what waits stays within the cap; and the balance left, after leave and encashments still waiting, stays at or
above `minimumBalanceAfterEncashment`. Its days are reserved while it waits, as pending leave is (shown in the balance's
`pending`). Approved, the days are debited (`LeaveBalance.Encashed`), the amount (days × the month's wages ÷ 30, the
full and final settlement's wages) is posted into the month's payroll inputs as the `LEAVE_ENC` earning, and the
encashment is kept in `hr.leave_encashments` with its approval and earning. In-service encashment is taxable salary:
s.10(10AA) exempts leave encashment only on retirement or leaving, so `LEAVE_ENC` is a taxable pay head. It is kept
out of PF and ESI wages (*Manipal Academy of Higher Education v. Provident Fund Commissioner*, Supreme Court, 2008). One
encashment waits per employee at a time. An employee with a leaving date is refused (`leave.encash_leaving`): their
leave is settled in the full and final settlement, which debits what it pays out ([statutory-returns.md](statutory-returns.md)).

### Payroll
| Method | Path | Permission | Body → Response |
|---|---|---|---|
| GET | `/api/v1/hr/payroll/components` | payroll.process or .approve | → `SalaryComponentDto[]` |
| POST/PUT | `/api/v1/hr/payroll/components[/{id}]` | payroll.process | `SaveSalaryComponentRequest {code, name, kind, calculationType: Fixed|PercentOfBasic, defaultValue, isTaxable, sortOrder, isActive, wageTreatment?: Wages|Excluded|PaidOnLeaving}` (none keeps it; BASIC and deductions are `Wages`); PF/ESI/PT/TDS are reserved |
| GET | `/api/v1/hr/payroll/professional-tax` | payroll.process or .approve | → `ProfessionalTaxScheduleDto[]` |
| PUT | `/api/v1/hr/payroll/professional-tax/{stateCode}` | payroll.approve | `SaveProfessionalTaxRequest {stateName, frequency: Monthly|HalfYearly, deductionMonths, bands[{incomeFrom, incomeTo, amount, februaryAmount}]}` replaces the state's table (audited) |
| GET/POST | `/api/v1/hr/employees/{employeeId}/salary-structures` | GET: process/approve; POST: payroll.process | `SaveSalaryStructureRequest {effectiveFrom, annualCtc, monthlyTds, pfApplicable, pfOnActualBasic, esiApplicable, professionalTaxState, notes, lines[{salaryComponentId, value}]}` → 202 (`hr.salary_structure`); once approved, the new structure closes the previous one. The pay heads and their amounts are in the structure's audit event (`lines`) |
| GET | `/api/v1/hr/payroll/runs?year` | process or approve | → `PayrollRunDto` (incl. `payslipsWithErrors`) |
| GET | `/api/v1/hr/payroll/runs/{id}` | process or approve | → `PayrollRunDto` |
| POST | `/api/v1/hr/payroll/runs` | payroll.process | `CreatePayrollRunRequest {year, month, notes}` (one per facility and month, not in the future) |
| DELETE | `/api/v1/hr/payroll/runs/{id}` | payroll.process | Draft/Processed only |
| POST | `/api/v1/hr/payroll/runs/{id}/process` | payroll.process | → `PayrollProcessResult {payslips, skipped, payslipsWithErrors, messages}` (re-runnable until approved) |
| POST | `/api/v1/hr/payroll/runs/{id}/approve` | payroll.approve | maker-checker; refused while any payslip has errors |
| POST | `/api/v1/hr/payroll/runs/{id}/reopen` | payroll.approve | `ReopenPayrollRequest {reason}` (Approved → Processed) |
| POST | `/api/v1/hr/payroll/runs/{id}/mark-paid` | payroll.approve | `MarkPayrollPaidRequest {paidOn, reference}`; notifies employees |
| POST | `/api/v1/hr/payroll/runs/{id}/lock` | payroll.approve | Paid → Locked; also locks the month's attendance |
| GET | `/api/v1/hr/payroll/runs/{id}/bank-transfer.csv` | payroll.approve | Approved/Paid/Locked only; CSV `EmployeeCode,BeneficiaryName,AccountNumber,IFSC,Amount,Narration` with decrypted accounts; never stored, every export audited, CSV-injection safe. The accounts are the ones in force when the run was approved, fixed on its payslips then (`payslips.bank_account_cipher`, `payroll_runs.bank_details_fixed_at`); a run approved before that has none (422 `payroll.bank_details_not_fixed`: reopen and approve again) |
| GET/POST | `/api/v1/hr/payroll/runs/{id}/inputs` | GET: process/approve; POST: payroll.process | `SavePayrollInputRequest {employeeId, kind: Earning|Deduction|LopDaysOverride|TdsOverride, componentCode, description, amount}`: a deduction → 201 its id; anything else → 202 (`hr.payroll_input`) |
| POST | `/api/v1/hr/payroll/runs/{id}/inputs/import` | payroll.process | `ImportPayrollInputsRequest {csv}`: `employeeCode,componentCode,amount[,description]`; all-or-nothing, 400 with line-numbered errors; with an earning in it → 202 `{imported: 0, approval}` (`hr.payroll_import`) |
| DELETE | `/api/v1/hr/payroll/inputs/{inputId}` | payroll.process | before approval only; a deduction another module posted or another person entered → 202 (`hr.payroll_input_remove`) |
| GET | `/api/v1/hr/payroll/pending-changes?runId&employeeId` | process or approve | → `PendingPayChange[]`: a run's inputs, or an employee's salary structures, waiting for approval |
| GET | `/api/v1/hr/payroll/runs/{id}/payslips?errorsOnly&search` | process or approve | → `PayslipListItem` |
| GET | `/api/v1/hr/payroll/payslips/{id}` | process or approve | → `PayslipDto` (payslip JSON: earnings, deductions, employer PF/ESI, masked PAN/account, warnings) |

Run states: `Draft → Processed ⇄ (re-process) → Approved → Paid → Locked`, with `Approved → Processed` via reopen.

Calculators (pure, unit-tested):
* **LOP** (`LopCalculator`): calendar-day basis. LOP = absent days + unpaid leave within the employment period. Days
  with no computed attendance count as absent and are reported. Attendance-exempt staff lose pay only for unpaid
  leave. A `LopDaysOverride` input wins.
* **PF** (`ProvidentFund`): 12 % of the month's PF wages, capped at the ₹15,000 wage ceiling unless `pfOnActualBasic`
  (which now means actual wages), rounded to the rupee. The employer share is reported. PF wages follow the Code on
  Social Security 2020, s.2(88), in force since 21 November 2025. Each earning's `wageTreatment` says how it counts:
  * `Wages`: basic pay, dearness allowance, retaining allowance, and any other pay the definition does not leave out
    (a special allowance, for instance). The default for a new earning.
  * `Excluded`: the pay clauses (a) to (i) leave out: bonus, house rent, conveyance, special expenses of the job,
    overtime, commission. What these come to above half of all the month's pay is added to wages.
  * `PaidOnLeaving`: gratuity, retrenchment compensation and other payments on leaving (clauses (j), (k)): neither
    wages nor counted in the half.

  For example, ₹20,000 of pay with ₹8,000 of wages and ₹12,000 left out gives PF wages of ₹10,000. The seeded house rent
  (`HRA`), conveyance (`CONV`) and overtime (`OT`) are `Excluded`; the other seeded earnings are wages. One-off inputs
  count as their pay head does, which they name by its code: a pay head with payroll inputs keeps its code (its name can
  change). Payroll months before November 2025, the month the Code came into force, keep PF on earned basic, as it was
  paid and filed then, when processed late or reopened.
* **ESI** (`EmployeeStateInsurance`): 0.75 % of earned gross (employer 3.25 %) when *contracted* gross ≤ ₹21,000,
  rounded up to the next rupee. Someone covered earlier in the contribution period (April–September, October–March)
  stays covered to its end after a raise above the ceiling (s.2(9) proviso).
* **Professional tax** (`ProfessionalTaxCalculator`): state slab table. Monthly states apply to the month's gross,
  with an optional February amount. Half-yearly states (Tamil Nadu) apply in the deduction months to the half-year's
  gross from earlier payslips, projected ×6 when there is no history.
* **TDS** (`PayrollTaxContext`, `TdsProjector`, `IncomeTaxCalculator`): computed every month under s.192, not typed:
  the year's tax under the employee's regime and declaration (new regime if none), on the salary paid so far this
  financial year, this month's and the structure's full month for each month left to March (or the last working day),
  less the TDS already deducted (here and by a previous employer), spread over this and the months left. Rates come
  from the dated table `IncomeTaxRates` (FY 2024-25 to 2026-27). Without a PAN, at least 20 % (s.206AA). This month's
  `TdsOverride` still wins; a year without rates falls back to the structure's `monthlyTds` with a warning. See
  [statutory-returns.md](statutory-returns.md).
* **Payslip** (`PayrollCalculator`): earnings are pro-rated by payable ÷ calendar days, and %-of-basic heads follow
  basic. One-off inputs are not pro-rated. Negative net pay is an error that blocks approval.

### Statutory returns, TDS and separation dues (`/api/v1/hr/statutory`)

The ECR, ESIC file, Form 24Q (FVU input), Form 16 Part B, bank upload files for SBI, HDFC and ICICI, tax declarations
and computation (both regimes), gratuity, the bonus register and the full and final statement, with the employer's TAN,
EPFO and ESIC codes and the TDS challans. Endpoints, rules and file layouts (with sources and dates) are in
[statutory-returns.md](statutory-returns.md). Employees see and change their own declaration and computation at
`GET/PUT /api/v1/hr/me/tax-declarations/{fy}` and `GET /api/v1/hr/me/tax-computation/{fy}`.

### Self-service (`/api/v1/hr/me`, permission `hr.self`)
`GET profile` · `GET roster?from&to` · `GET attendance?from&to` · `GET credentials` · `GET leave/balances?year` ·
`GET/POST leave/requests` · `POST leave/requests/{id}/cancel` · `GET/POST attendance/regularisations` ·
`POST attendance/regularisations/{id}/cancel` · `GET payslips` (approved months only) · `GET payslips/{id}` ·
`GET team/leave-requests?status` · `POST team/leave-requests/{id}/approve|reject` · `GET team/regularisations?status` ·
`POST team/regularisations/{id}/approve|reject`. The employee is always resolved from the signed-in user (403
`hr.self.not_linked` when the user has no employee record).

## Domain events
| Event | Raised when | Notes |
|---|---|---|
| `EmployeeJoined(EmployeeId, EmployeeCode, FacilityId, UserId)` | employee created | for canteen credit accounts and user provisioning |
| `EmployeeSeparated(EmployeeId, EmployeeCode, FacilityId, UserId, LastWorkingDay, Reason)` | separation | pending leave is cancelled, future roster is removed, user administrators are asked to disable the sign-in |

No integration events are handled.

## Services for other modules
Both are scoped services registered by `HRModule.AddServices` in namespace `HyFib.HealthCare360.Modules.HR.Services`.

**`IEmployeeDirectory`** is a read-only lookup that returns identification only. It never returns bank, PAN,
statutory, contact or salary data, so callers need no HR permission.

| Method | Returns |
|---|---|
| `FindByCodeAsync(string code, CancellationToken)` | `EmployeeDirectoryEntry?` for a code such as `NHEMP00042`. The code is trimmed and upper-cased. Anything other than 1–24 ASCII letters or digits returns `null` without querying the database. |
| `FindByIdAsync(Guid employeeId, CancellationToken)` | `EmployeeDirectoryEntry?`, so a caller can re-check an id it stored earlier. |

`EmployeeDirectoryEntry(EmployeeId, EmployeeCode, FullName, FacilityId, DepartmentId, DepartmentName,
DesignationName, IsActive)`:
* `IsActive` is true only when all of these hold:
  * the status is Active, Probation or OnNotice;
  * the joining date is on or before today (IST);
  * today is not after the last working day.
* Suspended and separated employees are therefore inactive, and callers should refuse new credit for them.
* Codes carry the facility prefix and are unique across the group. Deductions also post to the employee's own
  facility. For both reasons the lookup ignores the caller's facility filter. A caller that must restrict to its own
  site compares `FacilityId`.
* Soft-deleted employees are never returned.

Canteen staff credit (SupportServices, CAN-03) uses both services; SupportServices (layer 6) references HR (layer 2):
1. A staff-credit sale looks the staff code up with `IEmployeeDirectory.FindByCodeAsync`. Unknown codes and inactive
   employees are refused, and the bill carries the name from HR.
2. Month-end settlement posts each employee's balance with `PayrollInputService.PostDeductionAsync(employeeId, year,
   month, "CANTEEN", amount, description, "canteen:{period}:{code}")`. Balances that cannot be posted (employee not in
   HR, payroll month already approved) are left outstanding and listed in the settlement result.

**`PayrollInputService.PostDeductionAsync(employeeId, year, month, componentCode, amount, description,
sourceReference)`** records a payroll deduction, for example canteen staff credit (CAN-03):
* It creates the month's draft run if needed.
* It is idempotent on `sourceReference`.
* Once the month is approved, it returns 422 `payroll.closed`.

## Background jobs (only when `PlatformOptions.RunsJobs`; cluster-safe through PostgreSQL advisory locks)
* `AttendanceComputationJob`: after 01:30 IST, recomputes the previous two days for every facility.
* `CredentialExpiryAlertJob`: after 08:00 IST. Each credential alerts once at 30 days, once at 7 days and once on
  expiry. The employee receives a personal notice and `hr.credentials.manage` holders receive one summary per
  facility.
* `BiometricPullJob`: every minute, reads the terminals HC360 reads whose interval has passed (four at a time, each
  under its own lock) and reports terminals that have gone quiet.

## Notifications
In-app only (`NotifyUsersAsync` / `NotifyPermissionHoldersAsync`): leave and regularisation requests (to the manager,
or HR when there is no manager), decisions (to the employee), salary bank account changed (to the employee, as a
fraud alert), salary credited, credential expiry, separated employee (to `admin.users.manage` holders), and an
attendance terminal not heard from (to `hr.attendance.manage` holders, once per outage). No SMS templates.

## Number series
`hr.employee`: `{FAC}EMP{SEQ:5}`, per facility, never resets (e.g. `NHEMP00042`).

## Seed data (`HrSeeder`, order 110, idempotent)
23 designations, leave types (CL 12, SL 12 with 30 carry-forward, EL 15 with 45 carry-forward and encashable, ML
182 calendar days for women, CO, LOP unpaid), pay heads (BASIC, HRA 40 %, CONV, MEDICAL, SPECIAL, NIGHT, OT,
ARREARS, INCENTIVE, LEAVE_ENC (taxable, outside PF and ESI wages); deductions CANTEEN, ADVANCE, LOAN, UNIFORM, OTHER_DED; statutory PF/ESI/PT/TDS), professional
tax for **TN** (half-yearly, collected in Sep and Mar: ₹0 / 180 / 430 / 930 / 1,025 / 1,250 at ₹21,001 / 30,001 /
45,001 / 60,001 / 75,001) and **KA** (₹200 a month from ₹25,000, ₹300 in February), and shifts GEN 09:00–17:30, MOR
07:00–14:00, EVE 14:00–21:00 and NGT 21:00–07:00 per facility. National holidays (26 Jan, 1 May, 15 Aug, 2 Oct) are
seeded for this year and next. Five demo employees with salary structures are added only when `SeedDemoData` is on.
**Verify the professional-tax slabs with the local body before go-live.**

## Security and privacy
* Bank account, IFSC and PAN are AES-GCM encrypted with `IFieldProtector` and marked `[SensitiveData]`. Screens show
  them masked only. Blind indexes detect shared salary accounts and duplicate PANs. Changing bank details or the
  payment mode waits for a second person who approves payroll (never the employee), is audited with both names and the
  account's last four digits, and the employee is always notified. A change waiting keeps its account encrypted in the
  approval request.
* Only the bank-transfer CSV contains decrypted values. It requires `hr.payroll.approve` and an approved run, and
  every export is audited (PRV-04).
* Punches (`attendance_punches`) and computed days (`attendance_days`) are `[NotAudited]`. Regularisations, leave,
  payroll and masters are data-audited.
* Terminals send only attendance logs, and HC360 reads only attendance records and the clock from the terminals it
  reads. Biometric templates never reach the server (DPDP data minimisation).
