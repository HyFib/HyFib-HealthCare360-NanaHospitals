import { CartRegular, DrinkCoffeeRegular } from '@fluentui/react-icons';
import type { ModuleManifest } from '@/lib/modules';
import { CanteenTodayWidget } from './widgets';

const manifest: ModuleManifest = {
  id: 'canteen',
  title: 'Canteen',
  section: 'support',
  icon: DrinkCoffeeRegular,
  order: 30,
  links: [
    { label: 'Billing', to: '/canteen', permission: 'canteen.sales.create', keywords: ['POS', 'canteen bill', 'cafeteria', 'food counter'] },
    { label: 'Bills', to: '/canteen/sales', permission: ['canteen.sales.create', 'canteen.reports.read'], keywords: ['reprint', 'cancel bill'] },
    { label: 'Menu', to: '/canteen/menu', permission: ['canteen.menu.manage', 'canteen.sales.create'], keywords: ['prices', 'GST', 'stock', 'availability'] },
    { label: 'Day report', to: '/canteen/report', permission: 'canteen.reports.read', keywords: ['day end', 'collections', 'sales report'] },
    { label: 'Staff credit', to: '/canteen/credit', permission: 'canteen.credit.manage', keywords: ['salary deduction', 'payroll', 'staff balance'] },
  ],
  quickActions: [{ label: 'Canteen billing', to: '/canteen', permission: 'canteen.sales.create', icon: CartRegular, description: 'Quick sale with GST receipt' }],
  widgets: [{ id: 'canteen-today', permission: 'canteen.reports.read', span: 1, order: 62, component: CanteenTodayWidget }],
};

export default manifest;
