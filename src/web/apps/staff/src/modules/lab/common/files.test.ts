import { describe, expect, it } from 'vitest';
import { deviceFileProblem, fileProblem } from './files';

const file = (name: string, type: string, size = 2048) => new File([new Uint8Array(size)], name, { type });

describe('fileProblem', () => {
  it('takes PDFs and images', () => {
    expect(fileProblem(file('report.pdf', 'application/pdf'))).toBeNull();
    expect(fileProblem(file('scan.jpg', 'image/jpeg'))).toBeNull();
  });

  it('refuses other files by their type', () => {
    expect(fileProblem(file('notes.docx', 'application/msword'))).toBe(
      'notes.docx is not a PDF, PNG, JPEG or WebP file.',
    );
    expect(fileProblem(file('echo.dcm', 'application/dicom'))).toBe('echo.dcm is not a PDF, PNG, JPEG or WebP file.');
  });

  it('refuses an empty file', () => {
    expect(fileProblem(file('report.pdf', 'application/pdf', 0))).toBe('report.pdf is empty.');
  });
});

describe('deviceFileProblem', () => {
  it('takes what cardiology machines export', () => {
    expect(deviceFileProblem(file('tracing.pdf', 'application/pdf'))).toBeNull();
    expect(deviceFileProblem(file('echo.dcm', 'application/dicom'))).toBeNull();
    expect(deviceFileProblem(file('echo.dcm', ''))).toBeNull();
    expect(deviceFileProblem(file('ecg-0412.scp', ''))).toBeNull();
  });

  it('knows SCP-ECG and DICOM files by their extension whatever type the computer gives them', () => {
    expect(deviceFileProblem(file('ECG-0412.SCP', 'text/plain'))).toBeNull();
    expect(deviceFileProblem(file('loop.DCM', 'application/octet-stream'))).toBeNull();
  });

  it('refuses anything else', () => {
    expect(deviceFileProblem(file('notes.docx', 'application/msword'))).toBe(
      'notes.docx is not a PDF, image, DICOM or SCP-ECG file.',
    );
  });
});
