/**
 * Refusals of maker-checker slice 11 in words, with what to do next: salary payment details, pay (structures, earnings,
 * overrides, deductions taken off) and leave credits are asked for by one person and approved by another, and nobody
 * changes or approves their own pay or leave.
 */
export function payRefusal(code: string | undefined): string | null {
  switch (code) {
    case 'hr.pay_own':
      return 'This is your own pay: someone else in HR enters the change, and another person who approves payroll approves it.';
    case 'hr.leave_own':
      return 'This is your own leave: someone else in HR adjusts the balance or decides the request.';
    case 'hr.bank_details_own':
      return 'These are your own salary payment details: someone else who approves payroll decides them.';
    case 'approval.already_requested':
      return 'A change of this kind is already waiting for approval. Withdraw it from Approvals first, or wait for its decision.';
    case 'approval.self':
      return 'You asked for this change, so someone else decides it.';
    case 'payroll.bank_details_not_fixed':
      return 'This payroll was approved before its payment details were fixed at approval. Reopen it and approve it again to get its bank file.';
    case 'leave.encash_not_allowed':
      return 'This leave type is not paid out while in service under the leave policy. Set a yearly in-service cap on the leave type first, if the policy allows it.';
    case 'leave.encash_over_cap':
      return 'That is more than the leave policy pays out in service this year. Encash fewer days.';
    case 'leave.encash_below_minimum':
      return 'That would leave less than the balance the leave policy keeps. Encash fewer days.';
    case 'leave.encash_leaving':
      return 'The employee is leaving: unused leave is paid in the full and final settlement instead.';
    case 'payroll.closed':
      return "That month's payroll is already approved. Choose a later month.";
    case 'concurrency':
      return 'Someone else changed this record meanwhile. Open it again and ask again.';
    default:
      return null;
  }
}
