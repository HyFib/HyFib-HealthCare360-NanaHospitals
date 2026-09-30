import { Tab, TabList } from '@fluentui/react-components';
import { useNavigate } from '@tanstack/react-router';

export type IntegrationsTab = 'status' | 'logs' | 'simulator' | 'inbound';

/** The integrations screens: status dashboard, log, simulator inbox (training) and the WhatsApp inbox. */
export function IntegrationsTabs({ selected }: { selected: IntegrationsTab }) {
  const navigate = useNavigate();
  const open = (tab: IntegrationsTab) => {
    switch (tab) {
      case 'logs':
        return navigate({ to: '/admin/integrations/logs' });
      case 'simulator':
        return navigate({ to: '/admin/integrations/simulator' });
      case 'inbound':
        return navigate({ to: '/admin/integrations/inbound' });
      default:
        return navigate({ to: '/admin/integrations' });
    }
  };
  return (
    <TabList
      selectedValue={selected}
      onTabSelect={(_, data) => void open(data.value as IntegrationsTab)}
      aria-label="Integration screens"
    >
      <Tab value="status">Status</Tab>
      <Tab value="logs">Integration log</Tab>
      <Tab value="simulator">Simulator (training)</Tab>
      <Tab value="inbound">WhatsApp inbox</Tab>
    </TabList>
  );
}
