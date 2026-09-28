/** Server permission names of the SupplyChain module (docs/modules/supplychain.md). The server checks them again on every call. */
export const SUPPLY_PERMISSIONS = {
  itemsManage: 'inventory.items.manage',
  stockRead: 'inventory.stock.read',
  indentsRaise: 'inventory.indents.raise',
  indentsIssue: 'inventory.indents.issue',
  transfers: 'inventory.transfers',
  adjustmentsApprove: 'inventory.adjustments.approve',
  counts: 'inventory.counts',
  purchaseManage: 'purchase.manage',
  purchaseApprove: 'purchase.approve',
  grnReceive: 'purchase.grn.receive',
  assetsRead: 'assets.read',
  assetsManage: 'assets.manage',
  assetsMaintenance: 'assets.maintenance',
} as const;

const S = SUPPLY_PERMISSIONS;

/** Item master, categories and store look-ups. */
export const CATALOG_READERS = [
  S.itemsManage,
  S.stockRead,
  S.indentsRaise,
  S.indentsIssue,
  S.transfers,
  S.counts,
  S.purchaseManage,
  S.grnReceive,
] as const;

/** Store list (catalog readers plus adjustment approvers). */
export const STORE_READERS = [...CATALOG_READERS, S.adjustmentsApprove] as const;

/** Batch lists and reservations. */
export const BATCH_READERS = [S.stockRead, S.indentsIssue, S.transfers, S.counts, S.grnReceive, S.adjustmentsApprove] as const;

export const INDENT_READERS = [S.indentsRaise, S.indentsIssue, S.stockRead] as const;
export const TRANSFER_READERS = [S.transfers, S.stockRead] as const;
export const ADJUSTMENT_READERS = [S.counts, S.adjustmentsApprove, S.stockRead] as const;
export const REPORT_READERS = [S.stockRead, S.purchaseManage] as const;

export const VENDOR_READERS = [S.purchaseManage, S.purchaseApprove, S.grnReceive, S.assetsManage, S.assetsMaintenance] as const;
export const PURCHASE_READERS = [S.purchaseManage, S.purchaseApprove, S.grnReceive] as const;
export const ASSET_READERS = [S.assetsRead, S.assetsManage, S.assetsMaintenance] as const;
