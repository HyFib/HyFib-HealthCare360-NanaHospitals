import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { useSession, type AuthUser } from '@hc360/core';
import { render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { RestrictedSession } from './RestrictedSession';

vi.mock('@tanstack/react-router', () => ({ useNavigate: () => vi.fn() }));
vi.mock('@/modules/account/security', () => ({
  ChangePasswordForm: () => <form aria-label="Change password" />,
  MfaSetup: () => null,
}));

const user = (passwordBreached: boolean): AuthUser => ({
  id: 'u1',
  userName: 'asha',
  displayName: 'Asha Rao',
  type: 'staff',
  facilityId: null,
  facilityName: 'Nana Hospitals',
  practitionerId: null,
  patientId: null,
  roles: [],
  permissions: [],
  facilities: [],
  mfaEnabled: false,
  hasPin: false,
  mustChangePassword: true,
  restriction: 'password',
  passwordBreached,
});

function renderFor(passwordBreached: boolean) {
  useSession.setState({ user: user(passwordBreached) });
  render(
    <FluentProvider theme={webLightTheme}>
      <RestrictedSession restriction="password" />
    </FluentProvider>,
  );
}

describe('RestrictedSession', () => {
  afterEach(() => useSession.setState({ user: null }));

  it('asks for a new password after a temporary one', () => {
    renderFor(false);

    expect(screen.getByText(/You signed in with a temporary password/)).toBeInTheDocument();
    expect(screen.getByRole('form', { name: 'Change password' })).toBeInTheDocument();
  });

  it('says why when the password is on breach lists', () => {
    renderFor(true);

    expect(screen.getByText(/Your password is on published lists of passwords from data breaches/)).toBeInTheDocument();
    expect(screen.queryByText(/temporary password/)).not.toBeInTheDocument();
  });
});
