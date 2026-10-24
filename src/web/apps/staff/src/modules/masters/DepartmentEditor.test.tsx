import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import type * as Ui from '@hc360/ui';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterAll, beforeAll, describe, expect, it, vi } from 'vitest';
import { createFakeFetch } from '@/test/fakeApi';
import type * as MastersApi from './api';
import { mastersApi, mastersQueries, type DepartmentDto } from './api';
import { DepartmentEditorDrawer } from './DepartmentEditor';

// The save and the toasts are outside the test; the department and the specialities come from a fake server.
vi.mock('./api', async (importOriginal) => ({
  ...(await importOriginal<typeof MastersApi>()),
  mastersApi: { saveDepartment: vi.fn(() => Promise.resolve()) },
}));
const notify = vi.hoisted(() => ({ success: vi.fn(), info: vi.fn(), warning: vi.fn(), error: vi.fn() }));
vi.mock('@hc360/ui', async (importOriginal) => ({ ...(await importOriginal<typeof Ui>()), useNotify: () => notify }));

const medicine: DepartmentDto = {
  id: 'd1',
  facilityId: 'f1',
  code: 'GM',
  name: 'General medicine',
  kind: 'Clinical',
  specialtyId: null,
  headPractitionerId: null,
  location: null,
  phone: null,
  tokenPrefix: null,
  displayOrder: 10,
  isActive: true,
  version: 2,
};

beforeAll(() => {
  // Fluent's drawers measure themselves; jsdom has no ResizeObserver.
  vi.stubGlobal(
    'ResizeObserver',
    class {
      observe() {}
      unobserve() {}
      disconnect() {}
    },
  );
  vi.stubGlobal(
    'fetch',
    createFakeFetch({
      'GET /api/v1/masters/departments/d1': medicine,
      'GET /api/v1/masters/specialties': [],
    }).fetch,
  );
});

afterAll(() => {
  vi.unstubAllGlobals();
});

describe('the department editor', () => {
  it('saves on the version it was filled from, though the department was read again since (REL-04)', async () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
      <QueryClientProvider client={queryClient}>
        <FluentProvider theme={webLightTheme}>
          <DepartmentEditorDrawer departmentId="d1" nextOrder={20} onClose={vi.fn()} />
        </FluentProvider>
      </QueryClientProvider>,
    );

    await userEvent.type(await screen.findByRole('textbox', { name: 'Location' }), 'Block A, ground floor');
    // Someone else renamed the department, and the editor's copy was read again (as on going back to the window).
    act(() => {
      queryClient.setQueryData(mastersQueries.department('d1').queryKey, {
        ...medicine,
        name: 'Internal medicine',
        version: 3,
      });
    });
    await userEvent.click(screen.getByRole('button', { name: 'Save' }));

    await waitFor(() => expect(mastersApi.saveDepartment).toHaveBeenCalledTimes(1));
    // Sent with the values shown and the version they came with: the server refuses it, rather than taking it over
    // the renamed department.
    expect(mastersApi.saveDepartment).toHaveBeenCalledWith(
      'd1',
      expect.objectContaining({ name: 'General medicine', location: 'Block A, ground floor', version: 2 }),
    );
  });
});
