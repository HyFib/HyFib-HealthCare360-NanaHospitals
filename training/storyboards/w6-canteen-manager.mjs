// Wave 6 (back office): the canteen manager's day. Billing at the counter (a cash sale and a staff credit sale),
// keeping the menu, and the day's sales and staff credit. Recorded as the canteen manager (Muthu Kumar) at 11:10 am.

const nav = { role: 'navigation', name: 'Main navigation' };

export default {
  id: 'w6-canteen-manager',
  title: 'Canteen manager: bill at the counter, keep the menu, check sales and staff credit',
  role: 'CanteenManager',
  roleLabel: 'Canteen manager',
  user: 'canteen',
  wave: 6,
  audience: 'For the canteen manager and counter staff of Nana Hospitals.',
  intro:
    'The canteen serves patients’ attendants, visitors and staff. HC360 bills each sale with GST, keeps the menu and prices, and records what staff owe so that HR can deduct it from salary.',
  objectives: [
    'Bill a sale at the counter, for cash and on staff credit.',
    'Add an item to the menu and take another off.',
    'Read the day report and the staff credit.',
  ],
  modules: ['canteen'],
  covers: ['/canteen', '/canteen/menu', '/canteen/report', '/canteen/credit'],
  scenes: [
    {
      title: 'Bill at the counter',
      steps: [
        {
          click: { within: nav, role: 'button', name: 'Canteen' },
          say: 'Open Canteen from the menu.',
        },
        {
          nav: 'Billing',
          say: 'Open Billing.',
          after: 'The menu is on the left, by category. The bill is on the right.',
          wait: { role: 'heading', name: 'Canteen billing' },
        },
        {
          click: { role: 'checkbox', name: 'Print the receipt automatically' },
          say: 'The receipt prints by itself. Clear this box if no printer is connected.',
          zoom: 1.4,
        },
        {
          click: { role: 'button', name: /^Tea, / },
          say: 'A visitor wants tea and idli. Click Tea.',
          wait: { role: 'button', name: 'Charge ₹10.00 (F9)' },
        },
        {
          click: { role: 'button', name: /^Idli \(2 pcs\) with sambar, / },
          say: 'Click Idli with sambar.',
          after: 'The bill adds up to ₹40.00, with GST included in the prices.',
          wait: { role: 'button', name: 'Charge ₹40.00 (F9)' },
        },
        {
          show: { role: 'radiogroup', name: 'Paid by' },
          say: 'Choose how the visitor pays. Cash is chosen already.',
          zoom: 1.4,
        },
        {
          click: { role: 'button', name: 'Charge ₹40.00 (F9)' },
          say: 'Click Charge, or press F9.',
          after: 'The bill is saved, with its number and the amount.',
          wait: { text: /^Last bill / },
        },
        {
          click: { role: 'radio', name: 'Staff' },
          say: 'Next, a member of staff buys a dosa on credit. Choose Staff.',
          zoom: 1.4,
        },
        {
          click: { role: 'radio', name: 'Credit' },
          say: 'Choose Credit as the way of paying.',
          zoom: 1.4,
        },
        {
          fill: [{ label: /^Employee code/ }, 'NHEMP00001'],
          say: 'Type the employee code. HC360 checks it with HR, and takes the name from HR’s records.',
          zoom: 1.4,
        },
        {
          click: { role: 'button', name: /^Plain dosa, / },
          say: 'Click Plain dosa.',
          wait: { role: 'button', name: 'Charge ₹40.00 (F9)' },
        },
        {
          click: { role: 'button', name: 'Charge ₹40.00 (F9)' },
          say: 'Click Charge. The amount is not collected now. It is deducted from the salary at the month end.',
          after: 'The last bill is on staff credit, in the name of Deepa Raman.',
          wait: { text: /Staff credit/ },
        },
      ],
    },
    {
      title: 'Keep the menu',
      steps: [
        {
          nav: 'Menu',
          say: 'Open Menu.',
          after: 'Every item has its price, GST rate and code. A switch shows whether it is on today’s menu.',
          wait: { role: 'table', name: 'Menu items' },
        },
        {
          click: { role: 'button', name: 'Add item' },
          say: 'To add an item, click Add item.',
          wait: { role: 'dialog', name: 'Add menu item' },
        },
        {
          fill: [{ label: 'Code' }, 'RAGI-KOOZH'],
          say: 'Give the item a short code.',
          zoom: 1.3,
        },
        {
          fill: [{ label: 'Name' }, 'Ragi koozh'],
          say: 'And its name, as it should appear on the bill.',
          zoom: 1.3,
        },
        {
          select: [{ label: 'Category' }, 'Beverages'],
          native: true,
          say: 'Choose the category.',
          zoom: 1.3,
        },
        {
          fill: [{ label: 'Price (₹)' }, '20'],
          say: 'Type the price. It includes GST, as on the menu board.',
          zoom: 1.3,
        },
        {
          click: { within: { role: 'dialog', name: 'Add menu item' }, role: 'button', name: 'Add item' },
          say: 'Click Add item.',
          after: 'Ragi koozh is on the menu, and the counter can bill it now.',
          wait: { role: 'row', name: /Ragi koozh/ },
        },
        {
          click: { role: 'switch', name: "Poha on today's menu" },
          say: 'When an item is not made today, switch it off. Poha is finished, so switch it off.',
          after: 'Poha now shows No under On menu.',
          wait: { text: 'No', exact: true },
          zoom: 1.3,
        },
      ],
    },
    {
      title: 'The day report and staff credit',
      steps: [
        {
          nav: 'Day report',
          say: 'Open Day report.',
          after: 'Net sales are ₹80.00 from two bills, split by payment mode, customer and category. Cancelled bills are not in the totals.',
          wait: { role: 'heading', name: 'Canteen day report' },
        },
        {
          nav: 'Staff credit',
          say: 'Open Staff credit.',
          after: 'Deepa Raman owes ₹40.00 for the dosa. HR deducts it from her salary.',
          wait: { role: 'row', name: /Deepa Raman/ },
        },
        {
          show: { text: 'Payroll file' },
          say: 'Each month, download the payroll file here. It lists what every employee owes, for HR’s salary deduction.',
          zoom: 1.3,
        },
      ],
    },
  ],
  recap: [
    'Bill each sale at the counter. Staff can buy on credit with their employee code.',
    'Add new items to the menu, and switch off what is not made today.',
    'The day report shows the sales. Staff credit shows what staff owe, for the salary deduction.',
  ],
  check: [
    {
      q: 'A member of staff buys a dosa on credit. When is the money collected?',
      a: 'At the month end. HC360 keeps it as staff credit, and HR deducts it from the salary.',
    },
    {
      q: 'Poha is finished for today. What do you do on the Menu screen?',
      a: 'Switch off Poha under On menu, so that it is not on today’s menu.',
    },
  ],
};
