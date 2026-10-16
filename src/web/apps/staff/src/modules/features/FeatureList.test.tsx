import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import type { FeatureDto } from './api';
import { FeatureList } from './FeatureList';

// The list itself is tested; the confirmation around it is the house ConfirmDialog (Fluent dialogs flake in jsdom).
const portal: FeatureDto = {
  key: 'features.patientPortal',
  code: 'patientPortal',
  name: 'Patient portal',
  description: 'Patients sign in on their own phone to book appointments and see reports.',
  whenOff: 'Patients cannot sign in, register or book appointments online.',
  defaultEnabled: true,
  enabled: true,
  version: null,
};

const video: FeatureDto = {
  key: 'features.telehealth',
  code: 'telehealth',
  name: 'Video consultations',
  description: 'Doctors see patients over video.',
  whenOff: 'Video consultations disappear from the menus and nobody can join one.',
  defaultEnabled: true,
  enabled: false,
  version: null,
};

function renderList(onChange = vi.fn(), saving = false) {
  render(
    <FluentProvider theme={webLightTheme}>
      <FeatureList features={[portal, video]} saving={saving} onChange={onChange} />
    </FluentProvider>,
  );
  return onChange;
}

describe('FeatureList', () => {
  it('shows each feature with what it does, what stops while it is off, and whether it is on', () => {
    renderList();

    expect(screen.getByText('Patient portal')).toBeInTheDocument();
    expect(screen.getByText(portal.description)).toBeInTheDocument();
    expect(screen.getByText(portal.whenOff)).toBeInTheDocument();
    expect(screen.getAllByText('When it is off')).toHaveLength(2);

    const portalSwitch = screen.getByRole('switch', { name: 'Patient portal' });
    const videoSwitch = screen.getByRole('switch', { name: 'Video consultations' });
    expect(portalSwitch).toBeChecked();
    expect(videoSwitch).not.toBeChecked();
    // Beside each switch, as in Windows settings.
    expect(screen.getByText('On').closest('label')).toHaveAttribute('for', portalSwitch.id);
    expect(screen.getByText('Off').closest('label')).toHaveAttribute('for', videoSwitch.id);
  });

  it('asks to turn a feature off or on', async () => {
    const onChange = renderList();

    await userEvent.click(screen.getByRole('switch', { name: 'Patient portal' }));
    expect(onChange).toHaveBeenLastCalledWith(portal, false);

    await userEvent.click(screen.getByRole('switch', { name: 'Video consultations' }));
    expect(onChange).toHaveBeenLastCalledWith(video, true);
  });

  it('holds the switches while a change is being saved', () => {
    renderList(vi.fn(), true);

    expect(screen.getByRole('switch', { name: 'Patient portal' })).toBeDisabled();
    expect(screen.getByRole('switch', { name: 'Video consultations' })).toBeDisabled();
  });
});
