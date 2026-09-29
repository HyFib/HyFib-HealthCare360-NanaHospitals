import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { CameraView } from './PhotoCaptureDialog';

// A stand-in camera: jsdom has none, so the browser's media API is replaced for each test.
function fakeCamera(open: () => Promise<{ getTracks: () => { stop: () => void }[] }>) {
  const getUserMedia = vi.fn(open);
  Object.defineProperty(navigator, 'mediaDevices', { configurable: true, value: { getUserMedia } });
  return getUserMedia;
}

function liveStream() {
  const track = { stop: vi.fn() };
  return { track, stream: { getTracks: () => [track] } };
}

function renderCamera() {
  const onClose = vi.fn();
  const onCaptured = vi.fn();
  const view = render(
    <FluentProvider theme={webLightTheme}>
      <CameraView onClose={onClose} onCaptured={onCaptured} />
    </FluentProvider>,
  );
  return { onClose, onCaptured, unmount: view.unmount };
}

const originalVideoWidth = Object.getOwnPropertyDescriptor(HTMLVideoElement.prototype, 'videoWidth');

describe('CameraView', () => {
  afterEach(() => {
    delete (navigator as { mediaDevices?: unknown }).mediaDevices;
    if (originalVideoWidth) {
      Object.defineProperty(HTMLVideoElement.prototype, 'videoWidth', originalVideoWidth);
    }
    vi.unstubAllGlobals();
  });

  it('explains when the browser has no camera support, and can be cancelled', async () => {
    const { onClose } = renderCamera();

    expect(screen.getByText('This browser cannot use the camera. Upload a photo instead.')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Take photo' })).toBeDisabled();
    await userEvent.click(screen.getByRole('button', { name: 'Cancel' }));
    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it('explains a blocked camera and points to the upload', async () => {
    fakeCamera(() => Promise.reject(Object.assign(new Error('denied'), { name: 'NotAllowedError' })));

    renderCamera();

    expect(await screen.findByText(/The camera is blocked/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Take photo' })).toBeDisabled();
  });

  it('takes the frame on screen and hands it over, then closes', async () => {
    const { stream } = liveStream();
    fakeCamera(() => Promise.resolve(stream));
    Object.defineProperty(HTMLVideoElement.prototype, 'videoWidth', { configurable: true, get: () => 640 });
    const frame = { width: 640, height: 480, close: vi.fn() } as unknown as ImageBitmap;
    vi.stubGlobal('createImageBitmap', vi.fn().mockResolvedValue(frame));
    const { onCaptured, onClose } = renderCamera();

    await waitFor(() => expect(screen.getByRole('button', { name: 'Take photo' })).toBeEnabled());
    await userEvent.click(screen.getByRole('button', { name: 'Take photo' }));

    await waitFor(() => expect(onCaptured).toHaveBeenCalledWith(frame));
    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it('does not take a frame before the camera has started delivering pictures', async () => {
    const { stream } = liveStream();
    fakeCamera(() => Promise.resolve(stream));
    // No videoWidth: the preview has not produced a frame yet.
    const createImageBitmap = vi.fn();
    vi.stubGlobal('createImageBitmap', createImageBitmap);
    const { onCaptured } = renderCamera();

    await waitFor(() => expect(screen.getByRole('button', { name: 'Take photo' })).toBeEnabled());
    await userEvent.click(screen.getByRole('button', { name: 'Take photo' }));

    expect(createImageBitmap).not.toHaveBeenCalled();
    expect(onCaptured).not.toHaveBeenCalled();
  });

  it('switches the camera off when it is closed', async () => {
    const { stream, track } = liveStream();
    fakeCamera(() => Promise.resolve(stream));
    const { unmount } = renderCamera();
    await waitFor(() => expect(screen.getByRole('button', { name: 'Take photo' })).toBeEnabled());

    unmount();

    expect(track.stop).toHaveBeenCalledTimes(1);
  });

  it('switches the camera off if it starts after the dialog was already closed', async () => {
    const { stream, track } = liveStream();
    let start: (value: typeof stream) => void = () => undefined;
    fakeCamera(() => new Promise((resolve) => (start = resolve)));
    const { unmount } = renderCamera();

    unmount();
    start(stream);

    await waitFor(() => expect(track.stop).toHaveBeenCalledTimes(1));
  });
});
