import { typeScan } from '@hc360/core';
import { act, render, screen } from '@testing-library/react';
import { useState } from 'react';
import { describe, expect, it, vi } from 'vitest';

// A scan from a serial scanner (through HC360 Desktop) reaches the staff app's scan boxes as a keyboard scanner's does:
// the code typed into the focused field, then Enter.
function ScanBox({ onScan }: { onScan: (code: string) => void }) {
  const [text, setText] = useState('');
  return (
    <input
      aria-label="Scan"
      value={text}
      onChange={(e) => setText(e.target.value)}
      onKeyDown={(e) => {
        if (e.key === 'Enter') {
          e.preventDefault();
          onScan(text);
          setText('');
        }
      }}
    />
  );
}

describe('a serial scan', () => {
  it('is typed into a React scan box, which takes it on Enter', () => {
    const onScan = vi.fn();
    render(<ScanBox onScan={onScan} />);
    const box = screen.getByLabelText<HTMLInputElement>('Scan');
    box.focus();

    act(() => {
      expect(typeScan(document.activeElement, 'PCM500#AB1234')).toBe(true);
    });

    expect(onScan).toHaveBeenCalledWith('PCM500#AB1234');
    expect(box.value).toBe('');
  });

  it('goes in at the cursor, replacing a selection, as typing would', () => {
    render(<input aria-label="Notes" defaultValue="Batch ?? checked" />);
    const field = screen.getByLabelText<HTMLInputElement>('Notes');
    field.setSelectionRange(6, 8);

    typeScan(field, 'AB1234');

    expect(field.value).toBe('Batch AB1234 checked');
    expect(field.selectionStart).toBe(12);
  });

  it('starts a new line in a text area, for a list of codes', () => {
    render(<textarea aria-label="Codes" defaultValue="" />);
    const area = screen.getByLabelText<HTMLTextAreaElement>('Codes');

    typeScan(area, '000123456');
    typeScan(area, '000123457');

    expect(area.value).toBe('000123456\n000123457\n');
  });

  it('is never typed into a password, a disabled or read-only field, or anything but a field', () => {
    render(
      <>
        <input aria-label="Password" type="password" />
        <input aria-label="Locked" disabled />
        <input aria-label="Shown" readOnly value="x" />
        <button type="button">Save</button>
      </>,
    );

    expect(typeScan(screen.getByLabelText('Password'), 'PCM500')).toBe(false);
    expect(typeScan(screen.getByLabelText('Locked'), 'PCM500')).toBe(false);
    expect(typeScan(screen.getByLabelText('Shown'), 'PCM500')).toBe(false);
    expect(typeScan(screen.getByRole('button'), 'PCM500')).toBe(false);
    expect(typeScan(null, 'PCM500')).toBe(false);
  });
});
