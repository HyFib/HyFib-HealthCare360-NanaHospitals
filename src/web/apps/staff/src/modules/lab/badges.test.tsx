import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { FlagChip } from './badges';

describe('FlagChip', () => {
  // Nana Hospitals reads high and low results by an arrow (L7), as the printed report shows them.
  it('shows the arrow beside the letter, and screen readers read the words, not the arrow', () => {
    render(
      <FluentProvider theme={webLightTheme}>
        <FlagChip flag="High" />
        <FlagChip flag="CriticalLow" />
        <FlagChip flag="Abnormal" />
        <FlagChip flag="Normal" />
      </FluentProvider>,
    );

    expect(screen.getByText('↑ H')).toHaveAttribute('aria-hidden', 'true');
    expect(screen.getByText('High')).toBeInTheDocument();
    expect(screen.getByText('↓ LL')).toHaveAttribute('aria-hidden', 'true');
    expect(screen.getByText('Critically low')).toBeInTheDocument();
    expect(screen.getByText('A')).toBeInTheDocument();
    expect(screen.queryByText('Within the reference range')).not.toBeInTheDocument();
  });
});
