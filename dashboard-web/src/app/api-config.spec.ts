import { API_BASE_URL, HUB_URL } from './api-config';

describe('api-config', () => {
  it('calls the API on the page origin, with no host or port baked in', () => {
    expect(API_BASE_URL).toBe('');
  });

  it('points the hub at a path relative to the page origin', () => {
    expect(HUB_URL).toBe('/hubs/saga');
  });
});
