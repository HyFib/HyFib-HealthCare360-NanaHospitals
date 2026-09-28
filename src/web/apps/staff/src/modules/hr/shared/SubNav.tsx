import { Tab, TabList } from '@fluentui/react-components';
import { hasPermission, useSession } from '@hc360/core';
import { useLocation, useNavigate } from '@tanstack/react-router';

export interface SubNavItem {
  label: string;
  /** Route path of a sibling screen, e.g. "/hr/attendance/summary". */
  to: string;
  permission?: string | readonly string[];
}

/**
 * Tabs that switch between sibling screens of one area (attendance, leave, payroll, self-service). Each tab is its own
 * route, so Back/Forward and bookmarks work and only the open screen loads its data.
 */
export function SubNav({ items, ariaLabel }: { items: SubNavItem[]; ariaLabel: string }) {
  const user = useSession((s) => s.user);
  const pathname = useLocation({ select: (location) => location.pathname });
  const navigate = useNavigate();
  const visible = items.filter((item) => hasPermission(user, item.permission));
  const current = visible
    .filter((item) => pathname === item.to || pathname === `${item.to}/` || pathname.startsWith(`${item.to}/`))
    .sort((a, b) => b.to.length - a.to.length)[0]?.to;

  if (visible.length < 2) {
    return null;
  }

  return (
    <TabList
      aria-label={ariaLabel}
      selectedValue={current ?? null}
      onTabSelect={(_, data) => void navigate({ href: String(data.value) })}
      style={{ flexWrap: 'wrap' }}
    >
      {visible.map((item) => (
        <Tab key={item.to} value={item.to}>
          {item.label}
        </Tab>
      ))}
    </TabList>
  );
}
