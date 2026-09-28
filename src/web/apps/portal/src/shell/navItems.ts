import type { FluentIcon } from '@fluentui/react-icons';
import {
  CalendarLtrRegular,
  DocumentTextRegular,
  HeartPulseRegular,
  HomeRegular,
  PersonCircleRegular,
  PersonFeedbackRegular,
  PillRegular,
  QrCodeRegular,
  ReceiptRegular,
  ShieldCheckmarkRegular,
  StethoscopeRegular,
  VideoRegular,
} from '@fluentui/react-icons';
import type { MessageKey } from '@/i18n';

export type NavPath =
  | '/home'
  | '/doctors'
  | '/appointments'
  | '/video'
  | '/reports'
  | '/prescriptions'
  | '/bills'
  | '/records'
  | '/my-feedback'
  | '/profile'
  | '/verify'
  | '/privacy';

export interface NavItem {
  to: NavPath;
  label: MessageKey;
  icon: FluentIcon;
  /** Shown in the phone's bottom bar (the rest sit under "More"). */
  primary?: boolean;
}

/** Signed-in destinations, in the order people look for them. */
export const NAV_ITEMS: readonly NavItem[] = [
  { to: '/home', label: 'nav.home', icon: HomeRegular, primary: true },
  { to: '/appointments', label: 'nav.appointments', icon: CalendarLtrRegular, primary: true },
  { to: '/doctors', label: 'nav.findDoctor', icon: StethoscopeRegular },
  { to: '/video', label: 'nav.video', icon: VideoRegular },
  { to: '/reports', label: 'nav.reports', icon: DocumentTextRegular, primary: true },
  { to: '/prescriptions', label: 'nav.prescriptions', icon: PillRegular },
  { to: '/bills', label: 'nav.bills', icon: ReceiptRegular, primary: true },
  { to: '/records', label: 'nav.records', icon: HeartPulseRegular },
  { to: '/my-feedback', label: 'nav.feedback', icon: PersonFeedbackRegular },
  { to: '/profile', label: 'nav.profile', icon: PersonCircleRegular },
];

/** Public pages linked from the menu and footers. */
export const EXTRA_ITEMS: readonly NavItem[] = [
  { to: '/verify', label: 'nav.verify', icon: QrCodeRegular },
  { to: '/privacy', label: 'nav.privacy', icon: ShieldCheckmarkRegular },
];

/** True when `pathname` is the item's page or one of its sub-pages. */
export function isActivePath(pathname: string, to: NavPath): boolean {
  return pathname === to || pathname.startsWith(`${to}/`);
}
