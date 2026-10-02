import type * as Core from '@hc360/core';
import { bridge } from '@hc360/core';
import { act, renderHook, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type * as Ai from '@/lib/ai';
import { transcribeAudio, type TranscriptionResponse } from '@/lib/ai';
import type * as Recorder from './recorder';
import { startRecording, type AudioClip, type Recording, type VoiceUse } from './recorder';
import { useVoiceCapture } from './useVoiceCapture';

// The microphone, HC360 Desktop's speech and the server are replaced: what is tested is what happens when the user
// closes the panel at each step, above all while the browser is still asking for the microphone.
vi.mock('./recorder', async (importOriginal) => ({
  ...(await importOriginal<typeof Recorder>()),
  startRecording: vi.fn(),
}));
vi.mock('@/lib/ai', async (importOriginal) => ({
  ...(await importOriginal<typeof Ai>()),
  transcribeAudio: vi.fn(),
}));
vi.mock('@hc360/core', async (importOriginal) => {
  const core = await importOriginal<typeof Core>();
  return { ...core, bridge: { ...core.bridge, dictate: vi.fn() } };
});

const USE: VoiceUse = { maxRecordingMs: 90_000, typeInstead: 'type the dictation', describeError: () => 'It failed.' };
const CLIP: AudioClip = { blob: new Blob(['audio'], { type: 'audio/webm' }), fileName: 'voice.webm' };

function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((done) => {
    resolve = done;
  });
  return { promise, resolve };
}

/** A recording in progress: stopping it gives the clip, cancelling it gives nothing. */
function recording() {
  const audio = deferred<AudioClip | null>();
  return {
    stop: vi.fn(() => audio.resolve(CLIP)),
    cancel: vi.fn(() => audio.resolve(null)),
    result: audio.promise,
  } satisfies Recording;
}

const heard = (text: string): TranscriptionResponse => ({ text, provider: 'whisper', language: 'en', durationMs: 900 });

describe('useVoiceCapture', () => {
  beforeEach(() => {
    vi.mocked(startRecording).mockReset();
    vi.mocked(transcribeAudio).mockReset();
    vi.mocked(bridge.dictate).mockReset();
  });

  it('types what was heard once the recording stops', async () => {
    const onText = vi.fn();
    const session = recording();
    vi.mocked(startRecording).mockResolvedValue(session);
    vi.mocked(transcribeAudio).mockResolvedValue(heard(' Fever for two days '));
    const { result } = renderHook(() => useVoiceCapture('server', 'p1', onText, USE));

    act(() => result.current.start());
    await waitFor(() => expect(result.current.phase).toBe('listening'));
    act(() => result.current.stop());

    await waitFor(() => expect(result.current.phase).toBe('idle'));
    expect(transcribeAudio).toHaveBeenCalledWith(CLIP.blob, CLIP.fileName, 'p1');
    expect(onText).toHaveBeenCalledWith('Fever for two days');
  });

  it('closes a microphone that opens after the panel was closed, and records nothing', async () => {
    const onText = vi.fn();
    const opening = deferred<Recording>();
    vi.mocked(startRecording).mockReturnValue(opening.promise);
    const { result } = renderHook(() => useVoiceCapture('server', 'p1', onText, USE));

    act(() => result.current.start());
    expect(result.current.phase).toBe('starting');
    act(() => result.current.cancel());
    expect(result.current.phase).toBe('idle');

    const late = recording();
    await act(async () => opening.resolve(late));

    expect(late.cancel).toHaveBeenCalled();
    expect(transcribeAudio).not.toHaveBeenCalled();
    expect(onText).not.toHaveBeenCalled();
    expect(result.current.phase).toBe('idle');
  });

  it('closes a microphone that opens after the panel went away', async () => {
    const opening = deferred<Recording>();
    vi.mocked(startRecording).mockReturnValue(opening.promise);
    const { result, unmount } = renderHook(() => useVoiceCapture('server', 'p1', vi.fn(), USE));

    act(() => result.current.start());
    unmount();
    const late = recording();
    await act(async () => opening.resolve(late));

    expect(late.cancel).toHaveBeenCalled();
    expect(transcribeAudio).not.toHaveBeenCalled();
  });

  it('records again at once, and the microphone that opens late does not disturb the new recording', async () => {
    const opening = deferred<Recording>();
    const next = recording();
    vi.mocked(startRecording).mockReturnValueOnce(opening.promise).mockResolvedValueOnce(next);
    const { result } = renderHook(() => useVoiceCapture('server', 'p1', vi.fn(), USE));

    act(() => result.current.start());
    act(() => result.current.cancel());
    act(() => result.current.start());
    await waitFor(() => expect(result.current.phase).toBe('listening'));
    const late = recording();
    await act(async () => opening.resolve(late));

    expect(late.cancel).toHaveBeenCalled();
    expect(next.cancel).not.toHaveBeenCalled();
    expect(result.current.phase).toBe('listening');
    act(() => result.current.stop());
    expect(next.stop).toHaveBeenCalled();
  });

  it('drops a transcript that arrives after the panel was closed', async () => {
    const onText = vi.fn();
    vi.mocked(startRecording).mockResolvedValue(recording());
    const transcript = deferred<TranscriptionResponse>();
    vi.mocked(transcribeAudio).mockReturnValue(transcript.promise);
    const { result } = renderHook(() => useVoiceCapture('server', 'p1', onText, USE));

    act(() => result.current.start());
    await waitFor(() => expect(result.current.phase).toBe('listening'));
    act(() => result.current.stop());
    await waitFor(() => expect(result.current.phase).toBe('transcribing'));
    act(() => result.current.cancel());
    await act(async () => transcript.resolve(heard('Fever')));

    expect(onText).not.toHaveBeenCalled();
    expect(result.current.error).toBeNull();
    expect(result.current.phase).toBe('idle');
  });

  it('drops what HC360 Desktop hears after the panel was closed', async () => {
    const onText = vi.fn();
    const dictation = deferred<{ text: string }>();
    vi.mocked(bridge.dictate).mockReturnValue(dictation.promise);
    const { result } = renderHook(() => useVoiceCapture('desktop', 'p1', onText, USE));

    act(() => result.current.start());
    expect(result.current.phase).toBe('listening');
    act(() => result.current.cancel());
    await act(async () => dictation.resolve({ text: 'Fever' }));

    expect(onText).not.toHaveBeenCalled();
    expect(result.current.phase).toBe('idle');
  });
});
