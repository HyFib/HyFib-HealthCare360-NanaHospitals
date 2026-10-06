import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import type { VerifiedClaimCoding } from './api';
import { VerifiedCodingNotice } from './VerifiedCoding';

const verified: VerifiedClaimCoding = {
  diagnosisCodes: 'L03.1 Cellulitis of other parts of limb',
  procedureCodes: null,
  verifiedAt: '2026-10-06T09:30:00Z',
};

function renderNotice(props: {
  verified: VerifiedClaimCoding | null;
  diagnosis: string;
  procedures?: string;
  onUse?: (verified: VerifiedClaimCoding) => void;
}) {
  render(
    <FluentProvider theme={webLightTheme}>
      <VerifiedCodingNotice procedures="" {...props} />
    </FluentProvider>,
  );
}

describe('VerifiedCodingNotice', () => {
  it('offers the verified codes while the claim has others', async () => {
    const onUse = vi.fn();
    renderNotice({ verified, diagnosis: 'L03.1 Cellulitis of other parts of limb (left leg)', onUse });

    expect(screen.getByText(/^Coding verified by medical records on/)).toBeInTheDocument();
    expect(screen.getByText('Diagnosis: L03.1 Cellulitis of other parts of limb')).toBeInTheDocument();
    expect(screen.getByText('Procedures: none coded')).toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: 'Use these codes' }));
    expect(onUse).toHaveBeenCalledWith(verified);
  });

  it('says when the claim has the verified codes', () => {
    renderNotice({ verified, diagnosis: ' L03.1 Cellulitis of other parts of limb ', onUse: vi.fn() });

    expect(screen.getByText(/^These are the codes medical records verified on/)).toBeInTheDocument();
    expect(screen.queryByRole('button')).not.toBeInTheDocument();
  });

  it('says when nothing is verified yet', () => {
    renderNotice({ verified: null, diagnosis: '', onUse: vi.fn() });

    expect(screen.getByText('No coding verified by medical records for this stay or visit yet.')).toBeInTheDocument();
  });

  it('offers nothing to change on a closed claim', () => {
    renderNotice({ verified, diagnosis: 'L03.1 Cellulitis of other parts of limb (left leg)' });

    expect(screen.queryByText(/verified/)).not.toBeInTheDocument();
    expect(screen.queryByRole('button')).not.toBeInTheDocument();
  });
});
