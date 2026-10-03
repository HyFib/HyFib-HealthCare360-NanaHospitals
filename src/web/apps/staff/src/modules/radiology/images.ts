/**
 * The image viewer's rules (pure; unit-tested): the pictures of a series in order, moving through them, zoom, the
 * size to ask the server for, the keys, and the labels.
 */
import type { ImageInstance, ImageSeries } from './api';

/** One picture to show: a single-frame object, or one frame of a multi-frame object (`frame` set). */
export interface ViewerFrame {
  instanceUid: string;
  instanceNumber: number | null;
  /** 1-based frame number, only for objects with more than one frame. */
  frame: number | null;
  frames: number;
}

/** Most pictures a viewer steps through in one series (a long cine loop is cut here). */
export const MAX_FRAMES = 3000;

export const MIN_ZOOM = 1;
export const MAX_ZOOM = 8;
const ZOOM_STEP = 1.25;

/** Every picture of a series in order: one per single-frame object, one per frame of a multi-frame object. */
export function framesOf(instances: readonly ImageInstance[], max = MAX_FRAMES): ViewerFrame[] {
  const frames: ViewerFrame[] = [];
  for (const instance of instances) {
    const count = Math.max(1, Math.floor(instance.frames));
    for (let frame = 1; frame <= count && frames.length < max; frame++) {
      frames.push({
        instanceUid: instance.sopInstanceUid,
        instanceNumber: instance.instanceNumber,
        frame: count > 1 ? frame : null,
        frames: count,
      });
    }
  }
  return frames;
}

/** Moves within a list without falling off either end. */
export function stepIndex(index: number, count: number, delta: number): number {
  if (count <= 0) {
    return 0;
  }
  return Math.min(Math.max(index + delta, 0), count - 1);
}

export function zoomIn(zoom: number): number {
  return Math.min(MAX_ZOOM, Math.round(zoom * ZOOM_STEP * 100) / 100);
}

export function zoomOut(zoom: number): number {
  return Math.max(MIN_ZOOM, Math.round((zoom / ZOOM_STEP) * 100) / 100);
}

/**
 * The size to ask the server for: enough pixels for the space on screen at this zoom, in steps of 256 so neighbouring
 * sizes share a cached image, from 512 to the server's 2048.
 */
export function imageSize(width: number, height: number, pixelRatio: number, zoom: number): number {
  const needed = Math.max(width, height) * Math.max(1, pixelRatio) * Math.max(MIN_ZOOM, zoom);
  if (!Number.isFinite(needed) || needed <= 0) {
    return 1024;
  }
  return Math.min(2048, Math.max(512, Math.ceil(needed / 256) * 256));
}

export type ViewerAction = 'next' | 'previous' | 'first' | 'last' | 'zoomIn' | 'zoomOut' | 'fit';

/** The keys of the viewer: arrows and Page Up/Down move, Home and End jump, + and − zoom, 0 fits the screen. */
export function viewerAction(key: string): ViewerAction | null {
  switch (key) {
    case 'ArrowRight':
    case 'ArrowDown':
    case 'PageDown':
      return 'next';
    case 'ArrowLeft':
    case 'ArrowUp':
    case 'PageUp':
      return 'previous';
    case 'Home':
      return 'first';
    case 'End':
      return 'last';
    case '+':
    case '=':
      return 'zoomIn';
    case '-':
    case '_':
      return 'zoomOut';
    case '0':
      return 'fit';
    default:
      return null;
  }
}

/** "Series 2 · Axial 5 mm · 120 images" */
export function seriesLabel(series: ImageSeries): string {
  const parts = [
    series.seriesNumber !== null ? `Series ${series.seriesNumber}` : 'Series',
    series.description?.trim() || series.modality || null,
    series.imageCount !== null ? `${series.imageCount} ${series.imageCount === 1 ? 'image' : 'images'}` : null,
  ];
  return parts.filter(Boolean).join(' · ');
}

/** "Image 3 of 8" or "Frame 4 of 12" for the frames of one object. */
export function framePosition(frames: readonly ViewerFrame[], index: number): string {
  const current = frames[index];
  if (!current) {
    return '';
  }
  return current.frame !== null && frames.every((frame) => frame.instanceUid === current.instanceUid)
    ? `Frame ${current.frame} of ${current.frames}`
    : `Image ${index + 1} of ${frames.length}`;
}
