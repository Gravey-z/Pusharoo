import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { computed, effect, Injectable, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { WalletLoginChallenge, WalletLoginSession } from '../models/pusharoo.models';
import { ApiErrorFormatterService } from './api-error-formatter.service';
import { RuntimeConfigService } from './runtime-config.service';
import { WalletService } from './wallet.service';

export type WalletAuthState = 'checking' | 'anonymous' | 'signing-in' | 'authenticated' | 'expired' | 'logout-pending';

@Injectable({ providedIn: 'root' })
export class WalletAuthService {
  private readonly pendingKey = 'pusharoo.walletAuthLogoutPending';
  private readonly eventKey = 'pusharoo.walletAuthEvent';
  private readonly tabId = crypto.randomUUID();
  private generation = 0;
  private lastDisconnectEpoch: number;
  private lastAccountKey: string | null;
  private loginPromise: Promise<void> | null = null;
  private restorePromise: Promise<void> | null = null;
  private logoutPromise: Promise<void> | null = null;
  private expiryTimer: number | null = null;

  readonly state = signal<WalletAuthState>('checking');
  readonly serverSession = signal<WalletLoginSession | null>(null);
  readonly errorMessage = signal('');
  readonly canUseSession = computed(() => {
    const session = this.serverSession();
    return this.state() === 'authenticated' && this.matchesConnectedWallet(session)
      && Date.parse(session!.expiresAtUtc ?? '') > Date.now();
  });

  constructor(
    private readonly http: HttpClient,
    private readonly runtimeConfig: RuntimeConfigService,
    private readonly errors: ApiErrorFormatterService,
    private readonly wallet: WalletService
  ) {
    this.lastDisconnectEpoch = wallet.disconnectEpoch();
    this.lastAccountKey = this.accountKey();
    effect(() => {
      const disconnectEpoch = this.wallet.disconnectEpoch();
      const account = this.wallet.account();
      const session = this.serverSession();
      const accountKey = this.accountKey();
      const accountChanged = accountKey !== this.lastAccountKey;
      this.lastAccountKey = accountKey;
      if (disconnectEpoch !== this.lastDisconnectEpoch) {
        this.lastDisconnectEpoch = disconnectEpoch;
        void this.logout().catch(() => {});
      } else if (accountChanged && account && session?.authenticated && !this.matchesConnectedWallet(session)) {
        void this.logout().catch(() => {});
      }
    });
    window.addEventListener('storage', (event) => this.onStorageChange(event));
  }

  restore(): Promise<void> {
    if (this.restorePromise) return this.restorePromise;
    const promise = this.restoreNow();
    this.restorePromise = promise;
    void promise.then(
      () => { if (this.restorePromise === promise) this.restorePromise = null; },
      () => { if (this.restorePromise === promise) this.restorePromise = null; }
    );
    return promise;
  }

  ensureAuthenticated(): Promise<void> {
    if (this.canUseSession()) return Promise.resolve();
    if (this.loginPromise) return this.loginPromise;
    const promise = this.loginNow();
    this.loginPromise = promise;
    void promise.then(
      () => { if (this.loginPromise === promise) this.loginPromise = null; },
      () => { if (this.loginPromise === promise) this.loginPromise = null; }
    );
    return promise;
  }

  logout(): Promise<void> {
    this.generation++;
    if (this.logoutPromise) return this.logoutPromise;
    this.clearExpiryTimer();
    this.serverSession.set(null);
    this.state.set('logout-pending');
    this.setLogoutPending(true);
    this.broadcast('logout-pending');
    return this.finishLogout();
  }

  invalidateAfterUnauthorized(): void {
    this.generation++;
    this.clearExpiryTimer();
    this.serverSession.set(null);
    this.state.set('expired');
  }

  async refreshAntiforgery(): Promise<void> {
    const generation = this.generation;
    const session = await this.fetchSession();
    if (generation !== this.generation || this.isLogoutPending()) return;
    if (!session.authenticated) this.invalidateAfterUnauthorized();
    else this.acceptSession(session);
  }

  private async restoreNow(): Promise<void> {
    const generation = this.generation;
    this.state.set(this.isLogoutPending() ? 'logout-pending' : 'checking');
    this.errorMessage.set('');
    try {
      if (this.isLogoutPending()) {
        await this.finishLogout();
        return;
      }
      const session = await this.fetchSession();
      if (generation !== this.generation) return;
      this.acceptSession(session);
    } catch (error) {
      if (generation !== this.generation) return;
      this.serverSession.set(null);
      this.state.set(this.isLogoutPending() ? 'logout-pending' : 'anonymous');
      this.errorMessage.set(this.errors.format(error, 'Could not check the Pusharoo login session.'));
    }
  }

  private async loginNow(): Promise<void> {
    if (this.restorePromise) await this.restorePromise;
    if (this.isLogoutPending()) {
      const pendingGeneration = this.generation;
      await this.finishLogout();
      if (pendingGeneration !== this.generation) throw new Error('Wallet sign-in was canceled. Try again.');
    }
    const account = this.wallet.account();
    const walletSession = this.wallet.session();
    if (!account || !walletSession) throw new Error('Connect a wallet before signing in to Pusharoo.');
    if (this.canUseSession()) return;

    const initialGeneration = this.generation;
    this.errorMessage.set('');
    try {
      const existing = await this.fetchSession();
      this.assertWalletCurrent(account.address, account.scriptHash, walletSession.network, initialGeneration);
      if (existing.authenticated) {
        if (this.matchesConnectedWallet(existing)) {
          this.acceptSession(existing);
          return;
        }
        const expectedGeneration = this.generation + 1;
        await this.logout();
        if (this.generation !== expectedGeneration) throw new Error('Wallet sign-in was canceled. Try again.');
      }

      const generation = this.generation;
      this.state.set('signing-in');
      const challenge = await this.postWithCsrfRefresh<WalletLoginChallenge>('challenges', {
        address: account.address,
        network: walletSession.network,
        provider: walletSession.provider
      });
      this.assertWalletCurrent(account.address, account.scriptHash, walletSession.network, generation);
      const signature = await this.wallet.signWalletLogin(challenge);
      this.assertWalletCurrent(account.address, account.scriptHash, walletSession.network, generation);
      const session = await this.postWithCsrfRefresh<WalletLoginSession>('login', {
        challengeId: challenge.challengeId,
        signature
      });
      if (generation !== this.generation || !this.matchesConnectedWallet(session)) {
        if (this.logoutPromise) await this.logoutPromise.catch(() => {});
        await this.logout();
        throw new Error('The connected wallet changed during sign-in. Sign in again with the current wallet.');
      }
      this.acceptSession(session);
      this.broadcast('login');
    } catch (error) {
      if (this.state() === 'signing-in') this.state.set('anonymous');
      this.errorMessage.set(this.errors.format(error, 'Could not sign in to Pusharoo.'));
      throw error;
    }
  }

  private async completeLogout(): Promise<void> {
    try {
      await this.fetchSession();
      await this.postWithCsrfRefresh<WalletLoginSession>('logout', {});
      this.setLogoutPending(false);
      this.serverSession.set(null);
      this.state.set('anonymous');
      this.errorMessage.set('');
      this.broadcast('logout');
    } catch (error) {
      this.state.set('logout-pending');
      this.errorMessage.set(this.errors.format(error, 'Could not finish signing out. Try again when Pusharoo is available.'));
      throw error;
    }
  }

  private finishLogout(): Promise<void> {
    if (this.logoutPromise) return this.logoutPromise;
    const promise = this.completeLogout();
    this.logoutPromise = promise;
    void promise.then(
      () => { if (this.logoutPromise === promise) this.logoutPromise = null; },
      () => { if (this.logoutPromise === promise) this.logoutPromise = null; }
    );
    return promise;
  }

  private async fetchSession(): Promise<WalletLoginSession> {
    return firstValueFrom(this.http.get<WalletLoginSession>(`${this.apiBaseUrl()}/auth/session`));
  }

  private async postWithCsrfRefresh<T>(path: string, body: unknown): Promise<T> {
    const url = `${this.apiBaseUrl()}/auth/${path}`;
    try {
      return await firstValueFrom(this.http.post<T>(url, body));
    } catch (error) {
      if (!this.isAntiforgeryError(error)) throw error;
      await this.fetchSession();
      return firstValueFrom(this.http.post<T>(url, body));
    }
  }

  private acceptSession(session: WalletLoginSession): void {
    this.clearExpiryTimer();
    if (!session.authenticated || !session.address || !session.scriptHash || !session.expiresAtUtc) {
      this.serverSession.set(null);
      this.state.set('anonymous');
      return;
    }
    const remaining = Date.parse(session.expiresAtUtc) - Date.now();
    if (!Number.isFinite(remaining) || remaining <= 0) {
      this.serverSession.set(null);
      this.state.set('expired');
      return;
    }
    this.serverSession.set(session);
    this.state.set('authenticated');
    this.expiryTimer = window.setTimeout(() => this.invalidateAfterUnauthorized(), Math.min(remaining, 2_147_483_647));
  }

  private assertWalletCurrent(address: string, scriptHash: string, network: string, generation: number): void {
    if (generation !== this.generation || this.wallet.account()?.address !== address
      || this.wallet.account()?.scriptHash?.toLowerCase() !== scriptHash.toLowerCase()
      || this.wallet.session()?.network !== network) {
      throw new Error('The wallet changed during sign-in. Try again with the connected account.');
    }
  }

  private matchesConnectedWallet(session: WalletLoginSession | null): boolean {
    const account = this.wallet.account();
    return Boolean(session?.authenticated && account && session.address === account.address
      && session.scriptHash?.toLowerCase() === account.scriptHash?.toLowerCase());
  }

  private accountKey(): string | null {
    const account = this.wallet.account();
    return account ? `${account.address}|${account.scriptHash.toLowerCase()}` : null;
  }

  private apiBaseUrl(): string {
    const url = new URL(this.runtimeConfig.value.apiBaseUrl, window.location.origin);
    if (url.origin !== window.location.origin || (url.pathname !== '/api' && !url.pathname.startsWith('/api/'))) {
      throw new Error('Pusharoo wallet login requires the same-origin /api route.');
    }
    return url.pathname.replace(/\/$/, '');
  }

  private isAntiforgeryError(error: unknown): boolean {
    return error instanceof HttpErrorResponse && error.status === 403
      && error.error?.code === 'antiforgery_failed';
  }

  private clearExpiryTimer(): void {
    if (this.expiryTimer !== null) window.clearTimeout(this.expiryTimer);
    this.expiryTimer = null;
  }

  private isLogoutPending(): boolean {
    try { return localStorage.getItem(this.pendingKey) === '1'; } catch { return this.state() === 'logout-pending'; }
  }

  private setLogoutPending(pending: boolean): void {
    try {
      if (pending) localStorage.setItem(this.pendingKey, '1');
      else localStorage.removeItem(this.pendingKey);
    } catch { /* The in-memory state still blocks protected actions. */ }
  }

  private broadcast(action: 'login' | 'logout' | 'logout-pending'): void {
    try { localStorage.setItem(this.eventKey, JSON.stringify({ action, tabId: this.tabId, at: Date.now() })); } catch { /* Storage may be unavailable. */ }
  }

  private onStorageChange(event: StorageEvent): void {
    if (event.key === this.pendingKey && event.newValue === '1') {
      this.generation++;
      this.serverSession.set(null);
      this.state.set('logout-pending');
      return;
    }
    if (event.key !== this.eventKey || !event.newValue) return;
    try {
      const message = JSON.parse(event.newValue) as { action: string; tabId: string };
      if (message.tabId === this.tabId) return;
      if (message.action === 'login') void this.restore();
      if (message.action === 'logout' || message.action === 'logout-pending') {
        this.generation++;
        this.clearExpiryTimer();
        this.serverSession.set(null);
        this.state.set(message.action === 'logout' ? 'anonymous' : 'logout-pending');
      }
    } catch { /* Ignore malformed events from other tabs. */ }
  }
}
