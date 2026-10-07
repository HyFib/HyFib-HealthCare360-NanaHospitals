import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { useSession } from '@hc360/core';
import { render } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { CallControls } from './CallControls';
import type { CallState, TelehealthCall } from './TelehealthCall';

// A joined call with the microphone and camera on: only what the controls read.
const state = {
  phase: 'joined',
  media: { status: 'ready', message: null, hasAudio: true, hasVideo: true },
  micOn: true,
  cameraOn: true,
  audioOnly: false,
  quality: null,
  peer: 'connected',
} as unknown as CallState;

describe('CallControls', () => {
  afterEach(() => useSession.setState({ locked: false }));

  it('leaves the microphone and camera alone while the screen is locked', async () => {
    const call = { setMicrophone: vi.fn(), setCamera: vi.fn(), setAudioOnly: vi.fn() };
    render(
      <FluentProvider theme={webLightTheme}>
        <CallControls
          state={state}
          call={call as unknown as TelehealthCall}
          canEnd
          onEnd={() => undefined}
          onLeave={() => undefined}
        />
      </FluentProvider>,
    );

    useSession.setState({ locked: true });
    await userEvent.keyboard('{Control>}{Shift>}M{/Shift}{/Control}');
    await userEvent.keyboard('{Control>}{Shift>}O{/Shift}{/Control}');
    expect(call.setMicrophone).not.toHaveBeenCalled();
    expect(call.setCamera).not.toHaveBeenCalled();

    useSession.setState({ locked: false });
    await userEvent.keyboard('{Control>}{Shift>}M{/Shift}{/Control}');
    await userEvent.keyboard('{Control>}{Shift>}O{/Shift}{/Control}');
    expect(call.setMicrophone).toHaveBeenCalledWith(false);
    expect(call.setCamera).toHaveBeenCalledWith(false);
  });
});
