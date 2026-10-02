import { describe, expect, it } from 'vitest';
import { fitWithin, imageSourceProblem, MAX_SOURCE_BYTES, sidesToTry } from './images';

describe('images on the masters', () => {
  it('accepts PNG, JPEG and WebP files that are not empty or huge', () => {
    expect(imageSourceProblem({ name: 'logo.png', size: 20_000, type: 'image/png' })).toBeNull();
    expect(imageSourceProblem({ name: 'logo.png', size: 0, type: 'image/png' })).toBe('logo.png is empty.');
    expect(imageSourceProblem({ name: 'logo.svg', size: 2_000, type: 'image/svg+xml' })).toContain(
      'Choose a PNG, JPEG or WebP file',
    );
    expect(imageSourceProblem({ name: 'scan.png', size: MAX_SOURCE_BYTES + 1, type: 'image/png' })).toContain(
      'larger than 20 MB',
    );
  });

  it('shrinks to fit, keeping the shape and never enlarging', () => {
    expect(fitWithin(1600, 400, 800)).toEqual({ width: 800, height: 200 });
    expect(fitWithin(300, 900, 600)).toEqual({ width: 200, height: 600 });
    expect(fitWithin(320, 120, 800)).toEqual({ width: 320, height: 120 });
  });

  it('tries smaller sizes in turn until the image is small enough', () => {
    expect(sidesToTry(800)).toEqual([800, 600, 450, 300, 200]);
    expect(sidesToTry(600)).toEqual([600, 450, 300, 200]);
  });
});
