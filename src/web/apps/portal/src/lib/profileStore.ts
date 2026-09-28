import { create } from 'zustand';

const STORAGE_KEY = 'hc360:portal:profile';

function read(): string | null {
  try {
    return sessionStorage.getItem(STORAGE_KEY);
  } catch {
    return null;
  }
}

function write(value: string | null): void {
  try {
    if (value) {
      sessionStorage.setItem(STORAGE_KEY, value);
    } else {
      sessionStorage.removeItem(STORAGE_KEY);
    }
  } catch {
    // Storage unavailable: the choice lasts until the page is reloaded.
  }
}

interface ProfileState {
  /** The family member whose records are shown (an opaque patient id; kept for this browser tab only). */
  profileId: string | null;
  select(profileId: string | null): void;
}

export const useProfileStore = create<ProfileState>((set) => ({
  profileId: read(),
  select: (profileId) => {
    write(profileId);
    set({ profileId });
  },
}));
