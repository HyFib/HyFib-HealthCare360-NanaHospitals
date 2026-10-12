import type { UserNotification } from '@/shell/NotificationsButton';
import { paged, type FakeAnswers } from '../../fakeApi';
import { PATIENTS } from './frontOffice';

// The staff app's frame: notifications, what the command palette finds, the lock screen and the sign-in page.

const minutesAgo = (minutes: number) => new Date(Date.now() - minutes * 60_000).toISOString();

export const NOTIFICATIONS: UserNotification[] = [
  {
    id: 'n1',
    title: 'Critical value: Potassium 6.8 mmol/L',
    body: 'Asha Devi (NH26000123), sample LAB2600123',
    link: '/lab/critical-calls',
    category: 'Lab',
    severity: 'Critical',
    createdAt: minutesAgo(5),
    readAt: null,
  },
  {
    id: 'n2',
    title: 'Discount approved',
    body: null,
    link: null,
    category: 'Billing',
    severity: 'Info',
    createdAt: minutesAgo(90),
    readAt: minutesAgo(60),
  },
];

export const SHELL_SCREEN_ANSWERS: FakeAnswers = {
  'GET /api/v1/patients': paged(PATIENTS.slice(0, 1)),
  'GET /api/v1/notifications': paged(NOTIFICATIONS),
  'POST /api/v1/auth/lock': null,
  'GET /api/v1/system/info': {
    product: 'HyFib HealthCare360',
    version: '1.0.0',
    serverName: 'HC360-SRV01',
    facilityName: 'Nana Hospitals, Pune',
  },
};
