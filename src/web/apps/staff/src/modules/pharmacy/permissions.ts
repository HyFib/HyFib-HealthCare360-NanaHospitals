/** Server permission names of the Pharmacy module (docs/modules/pharmacy.md). The server checks them again on every call. */
export const PHARMACY_PERMISSIONS = {
  queueRead: 'pharmacy.queue.read',
  dispense: 'pharmacy.dispense',
  sales: 'pharmacy.sales',
  returns: 'pharmacy.returns',
  ipIssue: 'pharmacy.ip.issue',
  ipIndent: 'pharmacy.ip.indent',
  registers: 'pharmacy.registers',
  refills: 'pharmacy.refills',
  ndpsCount: 'pharmacy.ndps.count',
} as const;

/** "Counter staff" in the module doc: stock look-ups and batch lists at the pharmacy counters. */
export const COUNTER_STAFF = [
  PHARMACY_PERMISSIONS.queueRead,
  PHARMACY_PERMISSIONS.dispense,
  PHARMACY_PERMISSIONS.sales,
  PHARMACY_PERMISSIONS.ipIssue,
] as const;

/** Readers of the e-prescription queue. */
export const QUEUE_READERS = [
  PHARMACY_PERMISSIONS.queueRead,
  PHARMACY_PERMISSIONS.dispense,
  PHARMACY_PERMISSIONS.ipIssue,
] as const;

/** Readers of ward indents and IP issues. */
export const IP_READERS = [PHARMACY_PERMISSIONS.ipIssue, PHARMACY_PERMISSIONS.ipIndent] as const;

/** Readers of invoices (sales list, detail, returns). */
export const SALE_READERS = [PHARMACY_PERMISSIONS.sales, PHARMACY_PERMISSIONS.returns] as const;
