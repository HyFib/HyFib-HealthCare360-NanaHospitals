import { Button, Tooltip } from '@fluentui/react-components';
import { MicSparkleRegular } from '@fluentui/react-icons';
import { useHotkey, useSession } from '@hc360/core';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { AssistantPanel, type AssistantExchange } from './AssistantPanel';

/**
 * Ctrl+Space. `KeyboardEvent.key` is ' ' for the space bar, so the combo ends in a space. Ctrl+K stays the search
 * palette; Ctrl+Space is not used by the browser, WebView2 or any other screen.
 */
const ASSISTANT_HOTKEY = 'mod+ ';

/**
 * Header button and shortcut for the voice/text assistant (SRS AIV-01..03), shown to users with `ai.assistant.use`.
 * The conversation lives only in memory for this sign-in: it is gone after signing out or reloading.
 */
export function AssistantButton() {
  const allowed = usePermission('ai.assistant.use');
  const userId = useSession((s) => s.user?.id ?? null);
  const [open, setOpen] = useState(false);
  const [mounted, setMounted] = useState(false);
  const [history, setHistory] = useState<AssistantExchange[]>([]);

  // Another user signed in on this workstation: never show them the previous conversation.
  const [owner, setOwner] = useState(userId);
  if (owner !== userId) {
    setOwner(userId);
    setHistory([]);
  }

  const show = () => {
    setMounted(true);
    setOpen(true);
  };

  useHotkey(ASSISTANT_HOTKEY, show, { allowInInputs: true, enabled: allowed });

  if (!allowed) {
    return null;
  }

  return (
    <>
      <Tooltip content="Assistant: say or type a command (Ctrl+Space)" relationship="description">
        <Button appearance="subtle" icon={<MicSparkleRegular />} aria-label="Assistant" aria-keyshortcuts="Control+Space" aria-haspopup="dialog" onClick={show} />
      </Tooltip>
      {mounted && <AssistantPanel open={open} onClose={() => setOpen(false)} history={history} onHistoryChange={setHistory} />}
    </>
  );
}
