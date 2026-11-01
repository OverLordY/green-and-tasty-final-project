import { JwtHelperService } from '@auth0/angular-jwt';
import { applyDemoLoginFromUrl, createDemoTokens, DEMO_USER, isDemoAuthorization } from './demo-auth';

describe('demo auth (dev-only)', () => {
  const jwt = new JwtHelperService();

  it('creates a token the app accepts: decodable, not expired, with the Customer role', () => {
    const { idToken, accessToken } = createDemoTokens();
    expect(accessToken).toBe(idToken);
    expect(jwt.isTokenExpired(idToken)).toBeFalse();
    expect(jwt.decodeToken(idToken)['cognito:groups']).toEqual([DEMO_USER.role]);
  });

  it('recognises only demo tokens', () => {
    expect(isDemoAuthorization('Bearer ' + createDemoTokens().idToken)).toBeTrue();
    expect(isDemoAuthorization('Bearer real.jwt.token')).toBeFalse();
    expect(isDemoAuthorization(null)).toBeFalse();
  });

  it('logs in from ?demo and removes the parameter from the address', () => {
    const storage = jasmine.createSpyObj<Storage>('Storage', ['setItem']);
    const history = jasmine.createSpyObj<History>('History', ['replaceState']);
    const applied = applyDemoLoginFromUrl({ href: 'http://localhost:4200/reservations/demo-reservation/group-order?demo' }, storage, history);

    expect(applied).toBeTrue();
    expect(storage.setItem).toHaveBeenCalledWith('user', JSON.stringify(DEMO_USER));
    expect(history.replaceState).toHaveBeenCalledWith(undefined, '', '/reservations/demo-reservation/group-order');
  });

  it('does nothing without ?demo', () => {
    const storage = jasmine.createSpyObj<Storage>('Storage', ['setItem']);
    const history = jasmine.createSpyObj<History>('History', ['replaceState']);
    expect(applyDemoLoginFromUrl({ href: 'http://localhost:4200/main' }, storage, history)).toBeFalse();
    expect(storage.setItem).not.toHaveBeenCalled();
  });
});
