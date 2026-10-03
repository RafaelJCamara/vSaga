import * as apiConfig from './api-config';
import { API_BASE_URL, HUB_URL } from './api-config';

describe('api-config', () => {
  it('calls the API on the page origin, with no host or port baked in', () => {
    expect(API_BASE_URL).toBe('');
  });

  it('points the hub at a path relative to the page origin', () => {
    expect(HUB_URL).toBe('/hubs/saga');
  });

  // The SPA signs in with a username and password. A key constant here would put a credential in every
  // visitor's bundle, which is what the sign-in work removed.
  it('exports addresses only: no credential is compiled into the bundle', () => {
    expect(Object.keys(apiConfig).sort()).toEqual(['API_BASE_URL', 'HUB_URL']);
  });
});
