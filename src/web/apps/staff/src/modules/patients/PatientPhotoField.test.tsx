import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { PatientPhotoField, type PatientPhotoFieldProps } from './PatientPhotoField';
import type * as PhotoModule from './photo';
import { preparePhoto, uploadPhoto } from './photo';
import { usePatientPhotoUrl } from './usePatientPhotoUrl';

// The camera, canvas and file store are browser and server features; the control's own behaviour is what is tested here.
vi.mock('./photo', async (importOriginal) => ({
  ...(await importOriginal<typeof PhotoModule>()),
  preparePhoto: vi.fn(),
  uploadPhoto: vi.fn(),
}));
vi.mock('./usePatientPhotoUrl', () => ({ usePatientPhotoUrl: vi.fn(() => null) }));

const prepared = new File(['jpeg'], 'patient-photo.jpg', { type: 'image/jpeg' });

function renderField(props: Partial<PatientPhotoFieldProps> = {}) {
  const onChange = vi.fn();
  const view = render(
    <FluentProvider theme={webLightTheme}>
      <PatientPhotoField value={null} onChange={onChange} name="Asha Rao" {...props} />
    </FluentProvider>,
  );
  return {
    onChange,
    input: screen.getByLabelText('Choose a photo file') as HTMLInputElement,
    container: view.container,
  };
}

describe('PatientPhotoField', () => {
  beforeEach(() => {
    vi.mocked(preparePhoto).mockReset().mockResolvedValue(prepared);
    vi.mocked(uploadPhoto).mockReset().mockResolvedValue('file-1');
    vi.mocked(usePatientPhotoUrl).mockReset().mockReturnValue(null);
  });

  it('prepares and uploads a chosen photo, then reports its file id', async () => {
    const { onChange, input } = renderField();
    const chosen = new File(['raw'], 'me.png', { type: 'image/png' });

    await userEvent.upload(input, chosen);

    await waitFor(() => expect(onChange).toHaveBeenCalledWith('file-1'));
    expect(preparePhoto).toHaveBeenCalledWith(chosen);
    expect(uploadPhoto).toHaveBeenCalledWith(prepared);
  });

  it('refuses a file that is not a photo, without uploading anything', async () => {
    const { onChange, input } = renderField();
    const user = userEvent.setup({ applyAccept: false });

    await user.upload(input, new File(['%PDF'], 'report.pdf', { type: 'application/pdf' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('report.pdf is not a photo');
    expect(uploadPhoto).not.toHaveBeenCalled();
    expect(onChange).not.toHaveBeenCalled();
  });

  it('says so when the photo could not be saved, and keeps the current one', async () => {
    vi.mocked(uploadPhoto).mockRejectedValue(new Error('network'));
    const { onChange, input } = renderField({ value: 'file-0' });

    await userEvent.upload(input, new File(['raw'], 'me.jpg', { type: 'image/jpeg' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('The photo could not be used. Try another photo.');
    expect(onChange).not.toHaveBeenCalled();
  });

  it('offers to remove the photo only when there is one', () => {
    renderField();

    expect(screen.queryByRole('button', { name: 'Remove' })).not.toBeInTheDocument();
  });

  it('removes the photo when asked', async () => {
    const { onChange } = renderField({ value: 'file-0' });

    await userEvent.click(screen.getByRole('button', { name: 'Remove' }));

    expect(onChange).toHaveBeenCalledWith(null);
  });

  it('shows the patient photo when there is one', () => {
    vi.mocked(usePatientPhotoUrl).mockReturnValue('data:image/jpeg;base64,AAAA');

    const { container } = renderField({ value: 'file-0' });

    expect(container.querySelector('img')).toHaveAttribute('src', 'data:image/jpeg;base64,AAAA');
  });

  it('shows a message from the server about the photo', () => {
    renderField({ error: 'This photo already belongs to another patient. Take or upload a new one.' });

    expect(screen.getByRole('alert')).toHaveTextContent('already belongs to another patient');
  });

  it('cannot be used while it is disabled', () => {
    renderField({ disabled: true, value: 'file-0' });

    for (const name of ['Take photo', 'Upload', 'Remove']) {
      expect(screen.getByRole('button', { name })).toBeDisabled();
    }
  });

  // What the camera dialog does is tested on its own (PhotoCaptureDialog.test.tsx); here it only has to open.
  it('opens the camera when asked to take a photo', { timeout: 20_000 }, async () => {
    renderField();

    await userEvent.click(screen.getByRole('button', { name: 'Take photo' }));

    expect(await screen.findByText('Take a photo', {}, { timeout: 10_000 })).toBeInTheDocument();
  });
});
