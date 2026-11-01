import { Injectable, InjectionToken, inject } from '@angular/core';
import { API_BASE_URL } from '../../shared/config/api-base-url.token';
import { AuthenticationService } from './authentication.service';
import { UserModel } from '../models/user.model';

export interface CognitoLoginConfig { domain: string; clientId: string; }
export const COGNITO_CONFIG = new InjectionToken<CognitoLoginConfig>('COGNITO_CONFIG', { factory: () => ({ domain: '', clientId: '' }) });
interface LoginRequest { state: string; verifier: string; createdAt: number; returnPath: string; }
const REQUEST_KEY = 'cognito-login-request';

@Injectable({ providedIn: 'root' })
export class CognitoLoginService {
  private readonly config = inject(COGNITO_CONFIG);
  private readonly baseUrl = inject(API_BASE_URL);
  private readonly auth = inject(AuthenticationService);
  readonly configured = /^https:\/\/[a-z0-9.-]+\.auth\.[a-z0-9-]+\.amazoncognito\.com$/i.test(this.config.domain) && /^[a-z0-9]+$/i.test(this.config.clientId);
  signOutUrl(): string {
    if (!this.configured) throw new Error('Cognito login is not configured.');
    const url = new URL(this.config.domain + '/logout');
    url.search = new URLSearchParams({ client_id: this.config.clientId, logout_uri: window.location.origin }).toString();
    return url.toString();
  }
  signOut(): void { window.location.assign(this.signOutUrl()); }
  private random(): string { return this.encode(crypto.getRandomValues(new Uint8Array(32))); }
  private encode(bytes: Uint8Array): string { return btoa(String.fromCharCode(...bytes)).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, ''); }
  private redirectUri(): string { return window.location.origin + '/auth/callback'; }
  async prepare(returnPath = '/main', mode: 'login' | 'signup' = 'login'): Promise<string> {
    if (!this.configured) throw new Error('Cognito login is not configured.');
    const state = this.random(); const verifier = this.random();
    const challenge = this.encode(new Uint8Array(await crypto.subtle.digest('SHA-256', new TextEncoder().encode(verifier))));
    const safePath = returnPath.startsWith('/') && !returnPath.startsWith('//') && !returnPath.includes('\\') ? returnPath : '/main';
    const request: LoginRequest = { state, verifier, createdAt: Date.now(), returnPath: safePath };
    sessionStorage.setItem(REQUEST_KEY, JSON.stringify(request));
    const url = new URL(this.config.domain + (mode === 'signup' ? '/signup' : '/oauth2/authorize'));
    url.search = new URLSearchParams({ client_id: this.config.clientId, response_type: 'code', scope: 'openid email profile',
      redirect_uri: this.redirectUri(), state, code_challenge_method: 'S256', code_challenge: challenge }).toString();
    return url.toString();
  }
  async begin(returnPath = '/main', mode: 'login' | 'signup' = 'login'): Promise<void> {
    window.location.assign(await this.prepare(returnPath, mode));
  }
  async complete(query: URLSearchParams): Promise<string> {
    const raw = sessionStorage.getItem(REQUEST_KEY); sessionStorage.removeItem(REQUEST_KEY);
    if (!this.configured || !raw || !query.get('code') || !query.get('state')) throw new Error('Invalid login request.');
    const request = JSON.parse(raw) as LoginRequest;
    if (request.state !== query.get('state') || !request.verifier || !Number.isFinite(request.createdAt)
      || Date.now() - request.createdAt > 10 * 60 * 1000 || Date.now() < request.createdAt) throw new Error('Invalid or expired login request.');
    const response = await fetch(this.config.domain + '/oauth2/token', { method: 'POST', headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
      body: new URLSearchParams({ grant_type: 'authorization_code', client_id: this.config.clientId, code: query.get('code')!,
        redirect_uri: this.redirectUri(), code_verifier: request.verifier }) });
    if (!response.ok) throw new Error('Login failed.');
    const body = await response.json() as { id_token?: string; access_token?: string };
    if (!body.id_token || !body.access_token) throw new Error('Missing tokens.');
    const profile = await fetch(this.baseUrl + '/me', { headers: { Authorization: 'Bearer ' + body.id_token }, cache: 'no-store' });
    if (!profile.ok) throw new Error('Could not verify user.');
    const user = await profile.json() as UserModel;
    this.auth.acceptValidatedSession({ idToken: body.id_token, accessToken: body.access_token }, user);
    return request.returnPath?.startsWith('/') && !request.returnPath.startsWith('//') && !request.returnPath.includes('\\') ? request.returnPath : '/main';
  }
}
