import { Tokens } from '@app/auth/models/tokens.model';
import { UserModel } from '@app/auth/models/user.model';
import { Roles } from '@app/shared/constants/roles';

/**
 * DEV-ONLY мок-авторизація для домашки (рівні 1–2).
 * Відкрий будь-яку сторінку з параметром ?demo — і ти залогінений як Owner демо-бронювання.
 * Реєстрація, логін і бекенд не потрібні. Вийти — кнопка Sign out у шапці.
 * Для рівня 3 (справжній бекенд) вийди з демо і залогінься справжнім акаунтом.
 */
export const DEMO_LOGIN_PARAM = 'demo';
export const DEMO_ISSUER = 'green-and-tasty-demo';

export const DEMO_USER: UserModel = {
  id: 'demo-owner',
  firstName: 'Yevhenii',
  lastName: 'Demo',
  email: 'owner@demo.test',
  role: Roles.CUSTOMER,
};

const base64Url = (value: object): string =>
  btoa(unescape(encodeURIComponent(JSON.stringify(value)))).replace(/=+$/, '').replace(/\+/g, '-').replace(/\//g, '_');

/** Непідписаний JWT: фронтенд токен лише декодує (exp, ролі), а перевіряє його тільки бекенд. */
export function createDemoTokens(now = Date.now()): Tokens {
  const iat = Math.floor(now / 1000);
  const payload = {
    iss: DEMO_ISSUER,
    sub: DEMO_USER.id,
    email: DEMO_USER.email,
    given_name: DEMO_USER.firstName,
    family_name: DEMO_USER.lastName,
    'cognito:groups': [DEMO_USER.role],
    iat,
    exp: iat + 30 * 24 * 60 * 60,
  };
  const token = `${base64Url({ alg: 'none', typ: 'JWT' })}.${base64Url(payload)}.demo`;
  return { idToken: token, accessToken: token };
}

/** true, якщо запит іде з демо-токеном (його бачить мок-бекенд після tokenInterceptor). */
export function isDemoAuthorization(header: string | null): boolean {
  if (!header?.startsWith('Bearer ')) return false;
  try {
    const payload = JSON.parse(atob(header.slice(7).split('.')[1].replace(/-/g, '+').replace(/_/g, '/')));
    return payload.iss === DEMO_ISSUER;
  } catch {
    return false;
  }
}

/**
 * Викликається один раз на старті застосунку (лише в dev). Якщо в URL є ?demo —
 * кладе демо-токени і користувача в localStorage і прибирає параметр з адреси.
 */
export function applyDemoLoginFromUrl(
  location: Pick<Location, 'href'> = window.location,
  storage: Pick<Storage, 'setItem'> = localStorage,
  history: Pick<History, 'replaceState' | 'state'> = window.history,
): boolean {
  const url = new URL(location.href);
  if (!url.searchParams.has(DEMO_LOGIN_PARAM)) return false;

  storage.setItem('auth', JSON.stringify(createDemoTokens()));
  storage.setItem('user', JSON.stringify(DEMO_USER));

  url.searchParams.delete(DEMO_LOGIN_PARAM);
  history.replaceState(history.state, '', url.pathname + url.search + url.hash);
  return true;
}
