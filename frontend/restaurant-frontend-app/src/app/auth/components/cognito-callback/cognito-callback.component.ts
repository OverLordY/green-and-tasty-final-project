import { Component, OnInit, inject, signal } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { CognitoLoginService } from '../../services/cognito-login.service';

@Component({ selector: 'app-cognito-callback', imports: [RouterLink],
  template: `<main><h1>Вхід до Green &amp; Tasty</h1>
    @if (error()) { <p role="alert">{{ error() }}</p><a routerLink="/main">Повернутися на головну</a> }
    @else { <p role="status">Завершуємо вхід…</p> }</main>`,
  styles: `main { margin: 60px auto; max-width: 600px; padding: 20px; } [role=alert] { color: #a62924; }` })
export class CognitoCallbackComponent implements OnInit {
  private readonly login = inject(CognitoLoginService);
  private readonly router = inject(Router);
  readonly error = signal('');
  async ngOnInit(): Promise<void> {
    const query = new URLSearchParams(window.location.search);
    window.history.replaceState(null, '', '/auth/callback');
    try { await this.router.navigateByUrl(await this.login.complete(query)); }
    catch { this.error.set('Не вдалося завершити вхід. Спробуйте увійти ще раз.'); }
  }
}
