import type { FluentIcon } from '@fluentui/react-icons';
import { hasPermission, type AuthUser } from '@hc360/core';
import type { ComponentType } from 'react';

/** Navigation sections, in the order staff meet them during a patient's journey. */
export const NAV_SECTIONS = [
  { id: 'patients', label: 'Patient services' },
  { id: 'clinical', label: 'Clinical' },
  { id: 'diagnostics', label: 'Diagnostics' },
  { id: 'inpatient', label: 'In-patient' },
  { id: 'pharmacy', label: 'Pharmacy & stores' },
  { id: 'finance', label: 'Billing & finance' },
  { id: 'virtual', label: 'Virtual care' },
  { id: 'support', label: 'Support services' },
  { id: 'engagement', label: 'Engagement & quality' },
  { id: 'people', label: 'People' },
  { id: 'insights', label: 'Insights' },
  { id: 'admin', label: 'Administration' },
] as const;

export type NavSection = (typeof NAV_SECTIONS)[number]['id'];

export interface NavLink {
  label: string;
  /** Route path, e.g. "/front-office/registration". */
  to: string;
  permission?: string | readonly string[];
  /** Extra words for the command palette ("OP", "token", "UHID"). */
  keywords?: string[];
}

export interface QuickAction extends NavLink {
  icon: FluentIcon;
  description?: string;
}

export interface DashboardWidget {
  id: string;
  permission?: string | readonly string[];
  /** Grid columns on wide screens (the dashboard has 4). */
  span?: 1 | 2 | 4;
  order?: number;
  component: ComponentType;
}

/**
 * What a feature module contributes to the shell. Each module folder has a `manifest.ts(x)` exporting one of
 * these as default; the shell discovers them at build time, so modules never edit shared files.
 */
export interface ModuleManifest {
  id: string;
  title: string;
  section: NavSection;
  icon: FluentIcon;
  order?: number;
  links: NavLink[];
  quickActions?: QuickAction[];
  widgets?: DashboardWidget[];
}

/** Modules, links and quick actions the user may open (anything without permission is hidden, not disabled). */
export function visibleModules(user: AuthUser | null, manifests: ModuleManifest[]): ModuleManifest[] {
  return manifests
    .map((manifest) => ({
      ...manifest,
      links: manifest.links.filter((link) => hasPermission(user, link.permission)),
      quickActions: manifest.quickActions?.filter((action) => hasPermission(user, action.permission)),
    }))
    .filter((manifest) => manifest.links.length > 0);
}

/** The link that best matches a path (longest prefix), used to highlight navigation. */
export function matchLink(
  pathname: string,
  manifests: ModuleManifest[],
): { module: ModuleManifest; link: NavLink } | null {
  let best: { module: ModuleManifest; link: NavLink } | null = null;
  for (const module of manifests) {
    for (const link of module.links) {
      const matches = pathname === link.to || pathname.startsWith(`${link.to}/`);
      if (matches && (!best || link.to.length > best.link.to.length)) {
        best = { module, link };
      }
    }
  }
  return best;
}
