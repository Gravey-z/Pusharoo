import { HttpContextToken, HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { catchError, throwError } from 'rxjs';
import { RuntimeConfigService } from './runtime-config.service';
import { WalletAuthService } from './wallet-auth.service';
import { WalletService } from './wallet.service';

export const REQUIRE_WALLET_SESSION = new HttpContextToken<boolean>(() => false);

export const walletAuthInterceptor: HttpInterceptorFn = (request, next) => {
  if (!request.context.get(REQUIRE_WALLET_SESSION)) return next(request);

  const runtimeConfig = inject(RuntimeConfigService);
  const auth = inject(WalletAuthService);
  const wallet = inject(WalletService);
  const api = new URL(runtimeConfig.value.apiBaseUrl, window.location.origin);
  const target = new URL(request.url, window.location.origin);
  const apiPath = api.pathname.replace(/\/$/, '');
  if (api.origin !== window.location.origin || target.origin !== window.location.origin
    || (target.pathname !== apiPath && !target.pathname.startsWith(`${apiPath}/`))) {
    return throwError(() => new Error('Wallet login can only authorize Pusharoo API requests.'));
  }
  const account = wallet.account();
  if (!account || !auth.canUseSession()) {
    return throwError(() => new Error('Sign in to Pusharoo with the connected wallet before continuing.'));
  }

  return next(request.clone({ setHeaders: { 'X-Pusharoo-Expected-Wallet': account.address } })).pipe(
    catchError((error: unknown) => {
      if (error instanceof HttpErrorResponse && error.status === 401) auth.invalidateAfterUnauthorized();
      if (error instanceof HttpErrorResponse && error.status === 403
        && error.error?.code === 'antiforgery_failed') {
        void auth.refreshAntiforgery().catch(() => {});
      }
      return throwError(() => error);
    })
  );
};
