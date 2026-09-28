import { BowlChopsticksRegular, FoodRegular } from '@fluentui/react-icons';
import type { ModuleManifest } from '@/lib/modules';
import { MealRoundWidget } from './widgets';

const manifest: ModuleManifest = {
  id: 'dietary',
  title: 'Dietary',
  section: 'support',
  icon: FoodRegular,
  order: 20,
  links: [
    { label: 'Diet orders', to: '/dietary', permission: 'dietary.orders.read', keywords: ['diet', 'nil by mouth', 'NPO', 'therapeutic diet', 'dietitian'] },
    { label: 'Meal plans', to: '/dietary/meal-plans', permission: ['dietary.meals.manage', 'dietary.orders.read'], keywords: ['ward meals', 'delivery', 'meal round'] },
    { label: 'Kitchen production', to: '/dietary/kitchen', permission: 'dietary.meals.manage', keywords: ['kitchen', 'cooking list', 'veg non-veg count'] },
    { label: 'Diet types', to: '/dietary/diet-types', permission: ['dietary.masters.manage', 'dietary.orders.read'], keywords: ['diet master'] },
  ],
  quickActions: [{ label: 'Meal round', to: '/dietary/meal-plans', permission: 'dietary.meals.manage', icon: BowlChopsticksRegular, description: 'Mark meals delivered ward by ward' }],
  widgets: [{ id: 'dietary-meal-round', permission: ['dietary.meals.manage', 'dietary.orders.read'], span: 2, order: 61, component: MealRoundWidget }],
};

export default manifest;
