import { describe, expect, it } from 'vitest';
import type { ImageInstance } from './api';
import {
  framePosition,
  framesOf,
  imageSize,
  MAX_ZOOM,
  MIN_ZOOM,
  seriesLabel,
  stepIndex,
  viewerAction,
  zoomIn,
  zoomOut,
} from './images';

const instance = (uid: string, frames = 1, instanceNumber: number | null = null): ImageInstance => ({
  sopInstanceUid: uid,
  instanceNumber,
  frames,
});

describe('the pictures of a series', () => {
  it('lists single images in order and every frame of a cine loop', () => {
    const frames = framesOf([instance('1.1', 1, 1), instance('1.2', 3, 2)]);

    expect(frames.map((frame) => `${frame.instanceUid}:${frame.frame ?? '-'}`)).toEqual([
      '1.1:-',
      '1.2:1',
      '1.2:2',
      '1.2:3',
    ]);
  });

  it('stops at a limit for a very long loop', () => {
    expect(framesOf([instance('1.1', 10_000)], 50)).toHaveLength(50);
  });

  it('says where the viewer is', () => {
    expect(framePosition(framesOf([instance('1.1'), instance('1.2')]), 1)).toBe('Image 2 of 2');
    expect(framePosition(framesOf([instance('1.9', 12)]), 3)).toBe('Frame 4 of 12');
    expect(framePosition([], 0)).toBe('');
  });
});

describe('moving and zooming', () => {
  it('never falls off either end', () => {
    expect(stepIndex(0, 5, -1)).toBe(0);
    expect(stepIndex(4, 5, 1)).toBe(4);
    expect(stepIndex(2, 5, 1)).toBe(3);
    expect(stepIndex(3, 0, 1)).toBe(0);
  });

  it('zooms in steps between fitting the screen and eight times', () => {
    expect(zoomIn(MIN_ZOOM)).toBe(1.25);
    expect(zoomOut(1.25)).toBe(MIN_ZOOM);
    expect(zoomOut(MIN_ZOOM)).toBe(MIN_ZOOM);
    let zoom = MIN_ZOOM;
    for (let i = 0; i < 20; i++) {
      zoom = zoomIn(zoom);
    }
    expect(zoom).toBe(MAX_ZOOM);
  });

  it('asks for enough pixels for the screen and zoom, within what the server gives', () => {
    expect(imageSize(800, 600, 1, 1)).toBe(1024);
    expect(imageSize(1920, 1080, 1, 1)).toBe(2048);
    expect(imageSize(300, 200, 1, 1)).toBe(512);
    expect(imageSize(800, 600, 2, 3)).toBe(2048);
    expect(imageSize(Number.NaN, 600, 1, 1)).toBe(1024);
  });

  it('answers the keys radiologists use', () => {
    expect(viewerAction('ArrowRight')).toBe('next');
    expect(viewerAction('PageDown')).toBe('next');
    expect(viewerAction('ArrowUp')).toBe('previous');
    expect(viewerAction('Home')).toBe('first');
    expect(viewerAction('End')).toBe('last');
    expect(viewerAction('+')).toBe('zoomIn');
    expect(viewerAction('-')).toBe('zoomOut');
    expect(viewerAction('0')).toBe('fit');
    expect(viewerAction('a')).toBeNull();
  });
});

describe('series labels', () => {
  it('says which series and how many images', () => {
    expect(
      seriesLabel({
        seriesInstanceUid: '1',
        seriesNumber: 2,
        modality: 'CT',
        description: 'Axial 5 mm',
        bodyPart: null,
        imageCount: 120,
      }),
    ).toBe('Series 2 · Axial 5 mm · 120 images');
    expect(
      seriesLabel({
        seriesInstanceUid: '1',
        seriesNumber: null,
        modality: 'CR',
        description: ' ',
        bodyPart: null,
        imageCount: 1,
      }),
    ).toBe('Series · CR · 1 image');
  });
});
