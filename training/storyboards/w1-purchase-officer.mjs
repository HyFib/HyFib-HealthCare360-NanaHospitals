const nav = { role: 'navigation', name: 'Main navigation' };

// Wave 1 (the stores and purchase go live with the pharmacy at Nana Hospitals): the purchase officer's day. Turning an
// approved requisition into a purchase order, and checking the goods receipt the store keeper entered before the goods
// go into stock. Recorded as the purchase officer (Malathi Sekar) at 11:10 am. Setup, as the others would have done it
// earlier: an order to Coromandel Surgicals (approved by the hospital administrator, sent to the vendor) and the store
// keeper's goods receipt against it, sent for approval; and a requisition for gauze swabs that the hospital
// administrator approved.

const story = {
  id: 'w1-purchase-officer',
  title: 'Purchase officer: order from a requisition, approve goods receipts',
  role: 'PurchaseOfficer',
  roleLabel: 'Purchase officer',
  user: 'purchase',
  wave: 1,
  audience: 'For the purchase officers and purchase staff of Nana Hospitals.',
  intro:
    'Purchase turns what the stores need into orders to vendors, and checks that the goods that arrive match the vendor’s invoice. In HC360 every step has a record, and the goods enter stock only after a second person checks them.',
  objectives: [
    'Turn an approved requisition into a purchase order.',
    'Check and approve the goods receipt that the store keeper entered.',
    'See the received goods in stock.',
  ],
  modules: ['purchase', 'inventory', 'approvals'],
  covers: ['/purchase', '/purchase/requisitions', '/purchase/grns', '/inventory/stock', '/approvals'],
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
      title: 'Turn a requisition into a purchase order',
      steps: [
        {
          click: { within: nav, role: 'button', name: 'Purchase' },
          say: 'Open Purchase from the menu.',
        },
        {
          nav: 'Purchase overview',
          say: 'Open the Purchase overview.',
          after: 'One approved requisition is waiting to become an order, and one goods receipt is not posted yet.',
          wait: { role: 'button', name: /^Approved, not ordered 1/ },
          result: { role: 'button', name: /^Approved, not ordered 1/ },
        },
        {
          nav: 'Requisitions',
          say: 'Open Requisitions.',
          wait: { role: 'table', name: 'Requisitions' },
        },
        {
          click: { role: 'row', name: /Central Store/ },
          say: 'Open the requisition for the Central Store.',
          after: 'It asks for 40 packets of gauze swabs. The hospital administrator has approved it.',
          wait: { role: 'button', name: 'Create purchase order' },
        },
        {
          click: { role: 'button', name: 'Create purchase order' },
          say: 'Click Create purchase order.',
          after: 'The item is already on the order. Choose the vendor and enter the rate.',
          wait: { role: 'heading', name: 'New purchase order' },
        },
        {
          fill: [{ label: 'Vendor' }, 'Coromandel'],
          say: 'Type the name of the vendor.',
          wait: { role: 'option' },
          zoom: 1.4,
        },
        {
          click: { role: 'option', name: /Coromandel/ },
          say: 'Choose Coromandel Surgicals from the list.',
          zoom: 1.4,
        },
        {
          fill: [{ label: 'Rate of Gauze Swab 10 x 10 cm Sterile (8 ply)' }, '38'],
          say: 'Type the rate for one packet, from the vendor’s quotation. The rate does not include GST.',
          after: 'HC360 adds the GST and shows the total of the order.',
          wait: { text: /Estimated total/ },
          zoom: 1.3,
        },
        {
          click: { role: 'button', name: 'Save and send for approval' },
          say: 'Click Save and send for approval.',
          after: 'The order waits for a purchase approver. It goes to the vendor only after it is approved and sent.',
          wait: { text: /Waiting for approval by a purchase approver/ },
        },
      ],
    },
    {
      title: 'Approve the goods receipt',
      steps: [
        {
          nav: 'Approvals',
          say: 'Open Approvals.',
          after:
            'The goods receipt that Selvaraj Pandian entered waits for your decision. Nobody approves their own request.',
          wait: { role: 'row', name: /GRN NHGRN26000001/ },
        },
        {
          show: { role: 'row', name: /GRN NHGRN26000001/ },
          say: 'The receipt is for ₹7,403.00 from Coromandel Surgicals. Check it against the vendor’s invoice before you approve: the items, the batches and the total.',
          zoom: 1.3,
        },
        {
          click: { role: 'button', name: 'Approve' },
          say: 'Click Approve.',
          after: 'HC360 asks for an optional note.',
          wait: { text: /^Approve: goods receipt/ },
        },
        {
          click: { role: 'button', name: 'Approve', last: true },
          say: 'Click Approve again to confirm.',
          after: 'Nothing is waiting for your decision now.',
          wait: { text: /Nothing is waiting for your decision/ },
        },
        {
          nav: 'Goods receipts',
          say: 'Open Goods receipts.',
          after: 'The receipt is Posted. Posting adds the batches to stock.',
          wait: { role: 'row', name: /Coromandel Surgicals.*Posted/ },
        },
      ],
    },
    {
      title: 'See the goods in stock',
      steps: [
        {
          click: { within: nav, role: 'button', name: 'Stores and inventory' },
          say: 'Open Stores and inventory from the menu.',
        },
        {
          nav: 'Stock and batches',
          say: 'Open Stock and batches.',
          wait: { role: 'heading', name: 'Stock and batches' },
        },
        {
          fill: [{ label: 'Search' }, 'Gloves'],
          say: 'Search for the gloves that were received.',
          after: 'The Central Store has 4,000 gloves on hand, and the new batch is part of them.',
          wait: { role: 'row', name: /Examination Gloves/ },
        },
      ],
    },
  ],
  recap: [
    'Turn an approved requisition into an order: choose the vendor, enter the rate, and send the order for approval.',
    'Approve a goods receipt only after you have checked it against the vendor’s invoice. You cannot approve a receipt that you entered.',
    'An approved receipt is Posted, and its batches are in stock.',
  ],
  check: [
    {
      q: 'Why can the store keeper not approve a goods receipt they entered?',
      a: 'Goods go into stock only when someone other than whoever entered them has checked them against the vendor’s invoice.',
    },
    {
      q: 'What do you check before you click Approve on a goods receipt?',
      a: 'The items, the batches and the total, against the vendor’s invoice.',
    },
  ],
};

export default story;
