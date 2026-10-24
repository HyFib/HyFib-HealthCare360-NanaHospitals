import { Tab, TabList } from '@fluentui/react-components';
import { useNavigate } from '@tanstack/react-router';

export type NotificationsTab = 'templates' | 'log';

/** The notification screens: the message templates, and the delivery log of the messages sent with them. */
export function NotificationsTabs({ selected }: { selected: NotificationsTab }) {
  const navigate = useNavigate();
  return (
    <TabList
      selectedValue={selected}
      onTabSelect={(_, data) =>
        void navigate({ to: data.value === 'log' ? '/admin/notification-logs' : '/admin/notification-templates' })
      }
      aria-label="Notification screens"
    >
      <Tab value="templates">Templates</Tab>
      <Tab value="log">Delivery log</Tab>
    </TabList>
  );
}
