import { describe, expect, it } from 'vitest';
import { cameraProblem, fitWithin, PHOTO_MAX_SOURCE_BYTES, photoChange, photoSourceProblem } from './photo';

describe('photoChange', () => {
  it('sends nothing about the photo when a save leaves it alone', () => {
    // This is the bug being fixed: an edit of the other details must never clear the photo.
    expect(photoChange('photo-1', 'photo-1')).toEqual({ photoFileId: null, removePhoto: false });
    expect(photoChange(null, null)).toEqual({ photoFileId: null, removePhoto: false });
  });

  it('sends a new photo, whether or not there was one', () => {
    expect(photoChange('photo-1', 'photo-2')).toEqual({ photoFileId: 'photo-2', removePhoto: false });
    expect(photoChange(null, 'photo-2')).toEqual({ photoFileId: 'photo-2', removePhoto: false });
  });

  it('asks for removal only when there was a photo and it was taken away', () => {
    expect(photoChange('photo-1', null)).toEqual({ photoFileId: null, removePhoto: true });
  });
});

describe('fitWithin', () => {
  it('shrinks the longest side to the limit and keeps the shape', () => {
    expect(fitWithin(4000, 3000, 800)).toEqual({ width: 800, height: 600 });
    expect(fitWithin(3000, 4000, 800)).toEqual({ width: 600, height: 800 });
    expect(fitWithin(1000, 1000, 800)).toEqual({ width: 800, height: 800 });
  });

  it('never enlarges a small image', () => {
    expect(fitWithin(640, 480, 800)).toEqual({ width: 640, height: 480 });
    expect(fitWithin(800, 800, 800)).toEqual({ width: 800, height: 800 });
  });

  it('keeps a very thin image at least one pixel wide', () => {
    expect(fitWithin(10_000, 3, 800)).toEqual({ width: 800, height: 1 });
  });

  it('leaves an empty size alone', () => {
    expect(fitWithin(0, 0, 800)).toEqual({ width: 0, height: 0 });
  });
});

describe('photoSourceProblem', () => {
  const file = (over: Partial<{ name: string; size: number; type: string }> = {}) => ({ name: 'me.jpg', size: 200_000, type: 'image/jpeg', ...over });

  it('accepts JPEG, PNG and WebP photos', () => {
    for (const type of ['image/jpeg', 'image/png', 'image/webp']) {
      expect(photoSourceProblem(file({ type }))).toBeNull();
    }
  });

  it('accepts a large phone photo, which is shrunk before it is uploaded', () => {
    expect(photoSourceProblem(file({ size: 12 * 1024 * 1024 }))).toBeNull();
  });

  it('refuses an empty file, something that is not a photo, and a huge file', () => {
    expect(photoSourceProblem(file({ size: 0 }))).toBe('me.jpg is empty.');
    expect(photoSourceProblem(file({ name: 'report.pdf', type: 'application/pdf' }))).toBe('report.pdf is not a photo. Choose a JPEG, PNG or WebP image.');
    expect(photoSourceProblem(file({ type: '' }))).toContain('is not a photo');
    expect(photoSourceProblem(file({ size: PHOTO_MAX_SOURCE_BYTES + 1 }))).toBe('me.jpg is larger than 25 MB. Choose a smaller photo.');
  });
});

describe('cameraProblem', () => {
  it('explains a blocked camera and points to the upload', () => {
    expect(cameraProblem({ name: 'NotAllowedError' })).toContain('blocked');
    expect(cameraProblem({ name: 'NotAllowedError' })).toContain('upload a photo instead');
  });

  it('explains a missing camera and one in use', () => {
    expect(cameraProblem({ name: 'NotFoundError' })).toContain('No camera was found');
    expect(cameraProblem({ name: 'NotReadableError' })).toContain('another program');
  });

  it('has a plain answer for anything else, including a value that is not an error', () => {
    expect(cameraProblem(new Error('boom'))).toBe('The camera could not be started. Upload a photo instead.');
    expect(cameraProblem(null)).toBe('The camera could not be started. Upload a photo instead.');
    expect(cameraProblem('nope')).toBe('The camera could not be started. Upload a photo instead.');
  });
});
