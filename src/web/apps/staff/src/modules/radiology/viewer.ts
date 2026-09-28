import { bridge } from '@hc360/core';

/** Opens the PACS viewer outside the app (HC360 Desktop opens the system browser or viewer). */
export async function openViewerUrl(url: string): Promise<void> {
  if (bridge.isDesktop) {
    await bridge.openExternal(url);
    return;
  }
  window.open(url, '_blank', 'noopener,noreferrer');
}
