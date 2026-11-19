const nav = { role: 'navigation', name: 'Main navigation' };

const story = {
  id: 'w1-purchase-officer',
  title: 'Purchase officer: draft',
  role: 'PurchaseOfficer',
  roleLabel: 'Purchase officer',
  user: 'purchase',
  wave: 1,
  audience: 'x',
  intro: 'x',
  objectives: ['a', 'b'],
  modules: ['purchase'],
  covers: ['/purchase'],
  setup: async ({ api }) => {
    const purchase = await api('purchase');
    const admin = await api('hospitaladmin');
    const keeper = await api('storekeeper');
    const stores = await purchase.get('/api/v1/inventory/stores');
    const central = (stores.items ?? stores).find((s) => s.code === 'CS');
    const vendors = await purchase.get('/api/v1/purchase/vendors');
    const vendor = vendors.items.find((v) => v.code === 'VEN-CSUR');
    const items = await purchase.get('/api/v1/inventory/items?pageSize=200');
    const item = (code) => items.items.find((i) => i.code === code);
    // An order that was sent to the vendor, and the goods the store keeper received against it this morning.
    const po = await purchase.post('/api/v1/purchase/orders', {
      vendorId: vendor.id,
      storeId: central.id,
      requisitionId: null,
      expectedDeliveryDate: null,
      paymentTermsDays: null,
      remarks: null,
      version: null,
      lines: [
        { itemId: item('CON-EXGLOVE').id, quantity: 20, freeQuantity: 0, rate: 210, discountPercent: 0, gstRate: null },
        { itemId: item('CON-SYR5').id, quantity: 30, freeQuantity: 0, rate: 95, discountPercent: 0, gstRate: null },
      ],
    });
    await purchase.post(`/api/v1/purchase/orders/${po.id}/submit`);
    await admin.post(`/api/v1/purchase/orders/${po.id}/approve`);
    await purchase.post(`/api/v1/purchase/orders/${po.id}/send`);
    const order = await purchase.get(`/api/v1/purchase/orders/${po.id}`);
    const line = (code) => order.lines.find((l) => l.itemId === item(code).id);
    await keeper.post('/api/v1/purchase/grns', {
      vendorId: vendor.id,
      storeId: central.id,
      purchaseOrderId: po.id,
      vendorInvoiceNumber: 'CS/2611/0417',
      vendorInvoiceDate: '2026-11-19',
      receivedOn: '2026-11-19',
      remarks: null,
      sendForApproval: true,
      vendorInvoiceAmount: 7403,
      lines: [
        { itemId: item('CON-EXGLOVE').id, purchaseOrderLineId: line('CON-EXGLOVE').id, batchNumber: 'EG26118', manufactureDate: null, expiryDate: '2029-10-31', quantity: 20, freeQuantity: 0, rate: 210, mrp: 0, discountPercent: 0, gstRate: null, acceptShortExpiry: false },
        { itemId: item('CON-SYR5').id, purchaseOrderLineId: line('CON-SYR5').id, batchNumber: 'SY26077', manufactureDate: null, expiryDate: '2029-08-31', quantity: 30, freeQuantity: 0, rate: 95, mrp: 0, discountPercent: 0, gstRate: null, acceptShortExpiry: false },
      ],
    });
    // A requisition the hospital administrator approved, waiting to be turned into an order.
    const req = await purchase.post('/api/v1/purchase/requisitions', {
      storeId: central.id,
      requiredBy: null,
      remarks: null,
      lines: [{ itemId: item('CON-GAUZE').id, quantity: 40, remarks: null }],
    });
    await admin.post(`/api/v1/purchase/requisitions/${req.id}/approve`);
  },
  scenes: [
    {
      title: 'Order',
      steps: [
        { click: { within: nav, role: 'button', name: 'Purchase' }, say: 'Open Purchase.' },
        { nav: 'Requisitions', say: 'Open Requisitions.', wait: { role: 'table', name: 'Requisitions' } },
        { click: { role: 'row', name: /Central Store/ }, say: 'Open it.', wait: { role: 'button', name: 'Create purchase order' } },
        { click: { role: 'button', name: 'Create purchase order' }, say: 'Create.', wait: { role: 'heading', name: 'New purchase order' } },
        { fill: [{ label: 'Vendor' }, 'Coromandel'], say: 'vendor', wait: { role: 'option' } },
        { click: { role: 'option', name: /Coromandel/ }, say: 'pick vendor' },
        { fill: [{ label: 'Rate of Gauze Swab 10 x 10 cm Sterile (8 ply)' }, '38'], say: 'rate' },
        { click: { role: 'button', name: 'Save and send for approval' }, say: 'save', wait: { role: 'heading', name: /^Purchase order/ } },
      ],
    },
    {
      title: 'Approve',
      steps: [
        { nav: 'Approvals', say: 'Open Approvals.', wait: { role: 'heading', name: 'Approvals' } },
        { click: { role: 'button', name: 'Approve' }, say: 'approve' },
        { click: { within: { role: 'dialog' }, role: 'button', name: 'Approve' }, say: 'approve2' },
      ],
    },
  ],
  recap: ['a', 'b', 'c'],
  check: [{ q: 'q', a: 'a' }, { q: 'q', a: 'a' }],
  next: 'x',
};
// DEVTAIL-BEGIN
import { appendFileSync as __a, writeFileSync as __w } from 'node:fs';
const __story = story;
if (process.env.HC360_DUMP) {
  __w(process.env.HC360_DUMP, '');
  let n = 0;
  __story.scenes = __story.scenes.map((sc) => ({
    ...sc,
    steps: sc.steps.map((st) => ({
      ...st,
      after_do: async (t) => {
        if (st.after_do) await st.after_do(t);
        await t.quiet(3000);
        const dlg = t.page.locator('[role="dialog"]:visible');
        const root = (await dlg.count()) ? dlg.first() : t.page.locator('#main, main').first();
        __a(process.env.HC360_DUMP, `\n##### step ${++n} ${JSON.stringify(st.say ?? '').slice(0, 80)} ${t.page.url()}\n${await root.ariaSnapshot().catch((e) => String(e))}\n`);
      },
    })),
  }));
}
export default __story;
// DEVTAIL-END
