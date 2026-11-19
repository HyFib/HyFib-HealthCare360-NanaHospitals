const nav = { role: 'navigation', name: 'Main navigation' };

// Wave 1 (the stores go live with the pharmacy at Nana Hospitals): the store keeper's day. Receiving a vendor's goods
// against a purchase order, issuing a ward's indent from the central store, and the refrigerator temperatures.
// Recorded as the store keeper (Selvaraj Pandian) at 11:10 am. Setup, as the others would have done it earlier: the
// purchase officer's order to Coromandel Surgicals (approved by the hospital administrator, sent to the vendor), and
// the nurse's indent from the ICU Store (Deepa Raman).

const story = {
  id: 'w1-store-keeper',
  title: 'Store keeper: receive goods, issue indents, keep the cold chain',
  role: 'StoreKeeper',
  roleLabel: 'Store keeper',
  user: 'storekeeper',
  wave: 1,
  audience: 'For the store keepers of the central store and the sub-stores at Nana Hospitals.',
  intro:
    'The stores go live together with the pharmacy. HC360 keeps every receipt, issue and temperature reading, so the stock on the screen is the stock on the shelf.',
  objectives: [
    'Receive a vendor’s goods against a purchase order and send the receipt for approval.',
    'Approve and issue a ward store’s indent.',
    'Add a refrigerator and record its temperature.',
  ],
  modules: ['purchase', 'inventory'],
  covers: ['/purchase/orders', '/purchase/grns', '/inventory/indents', '/inventory/cold-chain'],
  setup: async ({ api }) => {
    const purchase = await api('purchase');
    const admin = await api('hospitaladmin');
    const stores = await purchase.get('/api/v1/inventory/stores');
    const central = (stores.items ?? stores).find((s) => s.code === 'CS');
    const vendors = await purchase.get('/api/v1/purchase/vendors');
    const vendor = vendors.items.find((v) => v.code === 'VEN-CSUR');
    const items = await purchase.get('/api/v1/inventory/items?pageSize=200');
    const item = (code) => items.items.find((i) => i.code === code);
    const created = await purchase.post('/api/v1/purchase/orders', {
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
    console.log('PO', JSON.stringify(created));
    await purchase.post(`/api/v1/purchase/orders/${created.id}/submit`);
    await admin.post(`/api/v1/purchase/orders/${created.id}/approve`);
    await purchase.post(`/api/v1/purchase/orders/${created.id}/send`);
    const nurse = await api('nurse');
    const icu = (stores.items ?? stores).find((s) => s.code === 'ICUS');
    await nurse.post('/api/v1/inventory/indents', {
      fromStoreId: central.id,
      toStoreId: icu.id,
      departmentId: null,
      priority: 'Routine',
      remarks: null,
      lines: [
        { itemId: item('CON-IVC22').id, quantity: 50, remarks: null },
        { itemId: item('CON-IVSET').id, quantity: 40, remarks: null },
      ],
    });
  },
  scenes: [
    {
      title: 'Receive goods against a purchase order',
      steps: [
        {
          click: { within: nav, role: 'button', name: 'Purchase' },
          say: 'Open Purchase from the menu.',
        },
        {
          nav: 'Purchase orders',
          say: 'Open Purchase orders.',
          after: 'The purchase order to Coromandel Surgicals is Sent to vendor.',
          wait: { role: 'row', name: /Coromandel Surgicals.*Sent to vendor/ },
        },
        {
          click: { role: 'row', name: /Coromandel Surgicals/ },
          say: 'The vendor’s delivery has arrived. Open its order, and count the boxes against its lines.',
          wait: { role: 'button', name: 'Receive goods' },
        },
        {
          click: { role: 'button', name: 'Receive goods' },
          say: 'Click Receive goods.',
          after: 'The items of the order are already on the receipt.',
          wait: { role: 'table', name: 'Items received' },
        },
        {
          fill: [{ label: 'Invoice number' }, 'CS/2611/0417'],
          say: 'Type the invoice number printed on the vendor’s invoice.',
          zoom: 1.4,
        },
        {
          fill: [{ label: 'Invoice total (as printed)' }, '7403'],
          say: 'Type the invoice total as printed. HC360 compares it with the lines.',
          after: 'HC360 says that it matches the printed invoice total.',
          wait: { text: /Matches the printed invoice total/ },
          zoom: 1.4,
        },
        {
          fill: [{ label: 'Batch of Disposable Syringe 5 ml with Needle' }, 'SY26077'],
          say: 'For each item, type the batch number printed on the pack.',
          zoom: 1.5,
        },
        {
          fill: [{ label: 'Expiry of Disposable Syringe 5 ml with Needle' }, '2029-08-31'],
          say: 'Then type the expiry date. HC360 refuses goods that have expired.',
          zoom: 1.5,
        },
        {
          fill: [{ label: 'Batch of Examination Gloves Nitrile Medium' }, 'EG26118'],
          say: 'Do the same for the gloves. Type their batch number.',
          zoom: 1.5,
        },
        {
          fill: [{ label: 'Expiry of Examination Gloves Nitrile Medium' }, '2029-10-31'],
          say: 'And their expiry date.',
          zoom: 1.5,
        },
        {
          click: { role: 'button', name: 'Send for approval' },
          say: 'Click Send for approval.',
          after: 'HC360 asks you to confirm. A receipt cannot be edited once it is sent.',
          wait: { text: 'Send this receipt for approval?' },
        },
        {
          click: { role: 'button', name: 'Send for approval', last: true },
          say: 'Confirm.',
          after:
            'The receipt waits for approval. Goods go into stock only when someone other than you has checked them against the invoice, for example the purchase officer.',
          wait: { role: 'group', name: 'Waiting for approval' },
        },
        {
          nav: 'Goods receipts',
          say: 'Open Goods receipts.',
          after: 'Your receipt is listed as Draft. It is not in stock yet.',
          wait: { role: 'row', name: /Coromandel Surgicals.*Draft/ },
        },
      ],
    },
    {
      title: 'Approve and issue an indent',
      steps: [
        {
          click: { within: nav, role: 'button', name: 'Stores and inventory' },
          say: 'Open Stores and inventory from the menu.',
        },
        {
          nav: 'Indents',
          say: 'Open Indents.',
          after: 'Deepa Raman has asked the Central Store for items for the ICU Store.',
          wait: { role: 'row', name: /Deepa Raman.*Pending/ },
        },
        {
          click: { role: 'row', name: /Deepa Raman/ },
          say: 'Open the indent. You cannot issue an indent that you raised yourself: someone else at the issuing store does that.',
          wait: { role: 'button', name: 'Approve' },
        },
        {
          show: { role: 'table', name: 'Indent lines' },
          say: 'The indent asks for 50 IV cannulas and 40 infusion sets. The last column shows how many the store has free.',
          zoom: 1.3,
        },
        {
          click: { role: 'button', name: 'Approve' },
          say: 'Click Approve.',
          wait: { role: 'dialog', name: /^Approve indent/ },
        },
        {
          click: { within: { role: 'dialog' }, role: 'button', name: 'Approve' },
          say: 'You can lower a quantity. Here, approve both lines as asked. The stock is set aside, so no one else takes it.',
          wait: { role: 'button', name: 'Issue items' },
        },
        {
          click: { role: 'button', name: 'Issue items' },
          say: 'Click Issue items.',
          wait: { role: 'dialog', name: /^Issue items/ },
        },
        {
          fill: [{ label: 'Collected by' }, 'Deepa Raman'],
          say: 'Type the name of the person who collects the goods.',
          zoom: 1.4,
        },
        {
          click: { role: 'button', name: 'Issue and print slip' },
          say: 'Click Issue and print slip.',
          after:
            'The indent is Issued. The issue slip is listed with its number, who collected the goods, and its value, ₹2,320.00.',
          wait: { role: 'table', name: 'Issue slips' },
          result: { role: 'table', name: 'Issue slips' },
        },
      ],
    },
    {
      title: 'Record the refrigerator temperature',
      steps: [
        {
          nav: 'Cold chain',
          say: 'Open Cold chain.',
          after: 'No refrigerator is listed yet.',
          wait: { role: 'heading', name: 'Cold chain' },
        },
        {
          click: { role: 'button', name: 'Add unit' },
          say: 'Click Add unit to list a refrigerator.',
          wait: { role: 'dialog', name: 'Add a refrigerator or freezer' },
        },
        {
          fill: [{ label: 'Code' }, 'CS-FR1'],
          say: 'Type a code.',
          zoom: 1.4,
        },
        {
          fill: [{ label: 'Name' }, 'Central Store refrigerator 1'],
          say: 'And a name. A refrigerator is kept between 2 and 8 °C.',
          zoom: 1.4,
        },
        {
          click: { role: 'button', name: 'Save' },
          say: 'Click Save.',
          wait: { role: 'row', name: /Central Store refrigerator 1/ },
        },
        {
          click: { role: 'button', name: 'Record' },
          say: 'Read the thermometer twice a day. Click Record.',
          wait: { role: 'dialog', name: /^Temperature of/ },
        },
        {
          fill: [{ label: /^Reading/ }, '5.2'],
          say: 'Type the reading. A reading outside the allowed range alerts the store’s in-charge.',
          zoom: 1.4,
        },
        {
          click: { within: { role: 'dialog' }, role: 'button', name: 'Record' },
          say: 'Click Record.',
          after: 'The reading, 5.2 °C, is listed with the time it was taken.',
          wait: { role: 'row', name: /5\.2/ },
        },
      ],
    },
  ],
  recap: [
    'Receive goods against the purchase order. Type the batch and expiry of every item, and send the receipt for approval.',
    'Goods enter stock only when someone else approves the receipt. You cannot issue an indent that you raised.',
    'Record each refrigerator’s temperature twice a day.',
  ],
  check: [
    {
      q: 'Why are the goods not in stock after you send the receipt for approval?',
      a: 'Goods go into stock only when someone other than you has checked the receipt against the vendor’s invoice and approved it.',
    },
    {
      q: 'You raised an indent for your own store. Can you issue it?',
      a: 'No. Someone else at the issuing store issues it.',
    },
  ],
};

export default story;
