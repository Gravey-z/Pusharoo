import { Component, OnDestroy, effect } from '@angular/core';
import { PageShellComponent } from '../page-shell/page-shell.component';
import { FaucetClaim, PendingFaucetClaim, FaucetStatus } from '../../models/faucet.models';
import { FaucetApiService } from '../../services/faucet-api.service';
import { NeoRpcService, ContractInvokeResult } from '../../services/neo-rpc.service';
import { RuntimeConfigService } from '../../services/runtime-config.service';
import { WalletService } from '../../services/wallet.service';
import { WalletAuthService } from '../../services/wallet-auth.service';
import { HttpErrorResponse } from '@angular/common/http';

interface WalletContext {
  address: string | null;
  scriptHash: string | null;
  selectedNetwork: string;
  sessionNetwork: string | null;
  key: string;
}

interface RpcStackValue {
  type?: string;
  value?: unknown;
}

@Component({
  selector: 'app-faucet',
  imports: [PageShellComponent],
  templateUrl: './faucet.component.html',
  styleUrl: './faucet.component.scss'
})
export class FaucetComponent implements OnDestroy {
  status: FaucetStatus | null = null;
  statusLoading = true;
  statusError = '';
  operationError = '';
  activityMessage = '';
  successMessage = '';
  busy = '';
  pendingClaim: PendingFaucetClaim | null = null;
  receivedAmount = '';
  private contextKey = '';
  private contextVersion = 0;
  private readonly pendingStorageKey = 'pusharoo.faucet.pending.v1';
  private destroyed = false;

  constructor(
    readonly wallet: WalletService,
    private readonly api: FaucetApiService,
    private readonly auth: WalletAuthService,
    private readonly neoRpc: NeoRpcService,
    private readonly runtimeConfig: RuntimeConfigService
  ) {
    effect(() => {
      const context = this.readWalletContext();
      if (context.key === this.contextKey) return;
      this.contextKey = context.key;
      const version = ++this.contextVersion;
      this.operationError = '';
      this.successMessage = '';
      this.receivedAmount = '';
      this.pendingClaim = null;
      void this.onWalletContextChanged(context, version);
    });
  }

  ngOnDestroy(): void {
    this.destroyed = true;
    this.contextVersion += 1;
  }

  get onMainnet(): boolean {
    const session = this.wallet.session();
    return this.wallet.selectedNetwork() === 'neo3:mainnet' || session?.network === 'neo3:mainnet';
  }

  get testnetContractHash(): string {
    return this.runtimeConfig.value.faucet.testnetContractHash.trim();
  }

  get claimAvailable(): boolean {
    return this.isTestnetWallet && this.status?.sponsoredAvailable === true && this.status.sponsoredEligible === true;
  }

  get isTestnetWallet(): boolean {
    return this.wallet.selectedNetwork() === 'neo3:testnet' && this.wallet.session()?.network === 'neo3:testnet' && Boolean(this.wallet.account());
  }

  get walletConnected(): boolean {
    return Boolean(this.wallet.account() && this.wallet.session());
  }

  get walletStatusText(): string {
    if (this.onMainnet) return 'The faucet only works on N3:Testnet.';
    if (!this.walletConnected) return 'Connect an N3:Testnet wallet to check claim eligibility.';
    if (this.wallet.selectedNetwork() !== 'neo3:testnet' || this.wallet.session()?.network !== 'neo3:testnet') {
      return 'Select N3:Testnet in Pusharoo and your wallet.';
    }
    if (this.statusLoading) return 'Checking the faucet on testnet…';
    if (this.statusError) return this.statusError;
    if (this.claimAvailable) return 'You can claim test GAS now.';
    if (this.status?.sponsoredAvailable === false) return this.status.sponsoredReason || 'Sponsored claiming is unavailable.';
    if (this.status?.sponsoredEligible === false)
      return this.reasonText(this.status.sponsoredIneligibleReason);
    return 'Wallet claim status is not available.';
  }

  get dailyResetDate(): string {
    return this.formatTime(this.status?.dailyResetAt ?? null);
  }

  get nextClaimDate(): string {
    return this.formatTime(this.status?.nextClaimAt ?? null);
  }

  async refresh(): Promise<void> {
    const context = this.readWalletContext();
    await this.refreshStatus(context, this.contextVersion);
  }

  async switchToTestnet(): Promise<void> {
    if (this.wallet.session()) await this.wallet.disconnect();
    this.wallet.selectNetwork('neo3:testnet');
  }

  async claimSponsored(): Promise<void> {
    const context = this.readWalletContext();
    if (!this.requireTestnetContext(context)) return;

    this.busy = 'sponsored';
    this.operationError = '';
    this.successMessage = '';
    this.activityMessage = 'Refreshing eligibility before requesting a sponsored claim.';
    try {
      await this.refreshStatus(context, this.contextVersion, true);
      this.assertSameContext(context);
      if (!this.status?.sponsoredAvailable) throw new Error(this.status?.sponsoredReason || 'Sponsored claims are unavailable.');
      if (!this.status.sponsoredEligible) throw new Error(this.reasonText(this.status?.sponsoredIneligibleReason));

      this.activityMessage = 'Signing in to Pusharoo if needed. Pusharoo pays the transaction fee.';
      await this.auth.ensureAuthenticated();
      this.assertSameContext(context);
      const requestId = crypto.randomUUID().replace(/-/g, '');
      const pending: PendingFaucetClaim = {
        accountAddress: context.address!,
        scriptHash: context.scriptHash!,
        network: 'neo3:testnet',
        route: 'sponsored',
        requestId,
        submittedAt: new Date().toISOString()
      };
      this.savePending(pending);
      this.pendingClaim = pending;
      this.activityMessage = 'Submitting your sponsored claim to Pusharoo.';
      let claim: FaucetClaim;
      try {
        claim = await this.api.submitClaim({ requestId });
      } catch (submitError) {
        try {
          claim = await this.api.getClaim(requestId);
        } catch (lookupError) {
          if (lookupError instanceof HttpErrorResponse && lookupError.status === 404
            && submitError instanceof HttpErrorResponse && submitError.status > 0) {
            this.removePending(pending);
            if (this.isCurrentWallet(pending)) this.pendingClaim = null;
            throw submitError;
          }
          if (this.isCurrentWallet(pending)) {
            this.activityMessage = 'The claim response is uncertain. Pusharoo will keep checking this request; refresh the page to resume.';
          }
          void this.trackSponsoredClaim(pending);
          return;
        }
      }
      const updated = { ...pending, requestId: claim.requestId, transactionHash: claim.transactionHash ?? undefined };
      if (updated.requestId !== pending.requestId) this.removePending(pending);
      this.savePending(updated);
      if (this.isCurrentWallet(pending)) {
        this.pendingClaim = updated;
        this.activityMessage = this.sponsoredStateText(claim.state);
      }
      void this.trackSponsoredClaim(updated);
    } catch (error) {
      this.operationError = this.errorText(error, 'Could not submit the sponsored claim.');
      this.activityMessage = '';
    } finally {
      this.busy = '';
    }
  }

  reasonText(reason: string | null | undefined): string {
    switch (reason) {
      case 'firstSponsoredClaim': return 'Use the faucet button to claim with this wallet.';
      case 'cooldown': return `This wallet can claim again at ${this.nextClaimDate}.`;
      case 'dailyCapReached': return `The faucet's daily allocation is used up. It resets at ${this.dailyResetDate}.`;
      case 'insufficientBalance': return 'The faucet does not have enough GAS to pay a claim right now.';
      case 'paused': return 'Claims are temporarily paused.';
      case 'invalidRecipient': return 'This wallet cannot receive faucet claims.';
      case 'contractRecipientUnsupported': return 'Contract wallets cannot claim from this faucet.';
      case 'eligible': return 'This wallet can claim now.';
      default: return reason || 'Claim availability could not be determined.';
    }
  }

  formatDatoshi(value: string | null | undefined): string {
    if (!value || !/^-?\d+$/.test(value)) return '—';
    const amount = BigInt(value);
    const negative = amount < 0n;
    const absolute = negative ? -amount : amount;
    const whole = absolute / 100_000_000n;
    const fraction = (absolute % 100_000_000n).toString().padStart(8, '0').replace(/0+$/, '');
    return `${negative ? '-' : ''}${whole}${fraction ? `.${fraction}` : ''}`;
  }

  formatTime(value: string | null): string {
    if (!value || !/^\d+$/.test(value)) return '—';
    const timestamp = Number(value);
    if (!Number.isFinite(timestamp)) return '—';
    return new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short' }).format(new Date(timestamp));
  }

  explorerUrl(transactionHash: string | null | undefined): string | null {
    return transactionHash ? `https://dora.coz.io/transaction/neo3/testnet/${encodeURIComponent(transactionHash)}` : null;
  }

  private async onWalletContextChanged(context: WalletContext, version: number): Promise<void> {
    this.status = null;
    this.activityMessage = '';
    if (context.sessionNetwork === 'neo3:mainnet' || context.selectedNetwork === 'neo3:mainnet') {
      this.statusLoading = false;
      this.statusError = '';
      return;
    }

    await this.refreshStatus(context, version);
    if (this.destroyed || version !== this.contextVersion) return;
    const pending = this.findPending(context);
    this.pendingClaim = pending;
    if (pending) {
      this.activityMessage = pending.route === 'direct'
        ? 'A direct claim is still being tracked for this wallet.'
        : 'A sponsored claim is still being tracked for this wallet.';
      if (pending.route === 'direct') void this.trackDirectClaim(pending);
      else void this.trackSponsoredClaim(pending);
    }
  }

  private async refreshStatus(context: WalletContext, version: number, showLoading = false): Promise<void> {
    if (context.sessionNetwork === 'neo3:mainnet' || context.selectedNetwork === 'neo3:mainnet') {
      this.statusLoading = false;
      return;
    }
    if (showLoading) this.statusLoading = true;
    this.statusError = '';
    try {
      let apiStatus: FaucetStatus | null = null;
      let apiError = '';
      try {
        apiStatus = await this.api.getStatus(context.address ?? undefined);
      } catch (error) {
        apiError = this.errorText(error, 'The faucet API is unavailable.');
      }
      if (version !== this.contextVersion || this.destroyed) return;

      if (apiStatus?.available) {
        this.status = apiStatus;
        return;
      }

      try {
        if (!this.testnetContractHash) throw new Error(apiStatus?.reason || apiError || 'The testnet faucet contract is not configured.');
        await this.neoRpc.verifyTestnetContract(this.testnetContractHash);
        const globalResult = await this.neoRpc.invokeFunction('neo3:testnet', this.testnetContractHash, 'getStatus', []);
        const global = this.toStack(globalResult);
        const walletResult = context.scriptHash
          ? await this.neoRpc.invokeFunction('neo3:testnet', this.testnetContractHash, 'getClaimStatus', [
            { type: 'Hash160', value: context.scriptHash }
          ])
          : null;
        const claim = walletResult ? this.toStack(walletResult) : [];
        const reason = apiStatus?.sponsoredReason || apiStatus?.reason || apiError || 'Sponsored claims are unavailable.';
        this.status = {
          available: true,
          reason: null,
          sponsoredAvailable: false,
          sponsoredReason: reason,
          claimAmount: this.stackInteger(global[0]),
          dailyCap: this.stackInteger(global[1]),
          paused: this.stackBoolean(global[2]),
          balance: this.stackInteger(global[3]),
          remainingDailyAllowance: this.stackInteger(global[5]),
          registered: claim.length ? this.stackBoolean(claim[0]) === 'true' : null,
          sponsoredEligible: claim.length ? this.stackBoolean(claim[1]) === 'true' : null,
          directEligible: claim.length ? this.stackBoolean(claim[2]) === 'true' : null,
          nextClaimAt: claim.length ? this.stackInteger(claim[3]) : null,
          sponsoredIneligibleReason: claim.length ? this.stackText(claim[4]) : null,
          directIneligibleReason: claim.length ? this.stackText(claim[5]) : null,
          dailyResetAt: this.stackInteger(global[6])
        };
        return;
      } catch (fallbackError) {
        this.status = null;
        this.statusError = this.errorText(fallbackError, apiError || 'Could not read the faucet from testnet.');
      }
    } finally {
      if (version === this.contextVersion && !this.destroyed) this.statusLoading = false;
    }
  }

  private async trackDirectClaim(pending: PendingFaucetClaim): Promise<void> {
    if (!pending.transactionHash) return;
    try {
      const confirmed = await this.neoRpc.waitForFaucetClaim(
        pending.transactionHash, this.testnetContractHash, pending.scriptHash
      );
      this.removePending(pending);
      if (this.isCurrentWallet(pending)) {
        this.pendingClaim = null;
        this.receivedAmount = this.formatDatoshi(confirmed.amountDatoshi);
        this.successMessage = `Claim confirmed. ${this.receivedAmount} GAS was sent to your wallet.`;
        this.activityMessage = '';
        await this.refresh();
      }
    } catch (error) {
      const message = this.errorText(error, 'The direct claim is still awaiting confirmation.');
      if (message.includes('finished with FAULT')) {
        this.removePending(pending);
        if (this.isCurrentWallet(pending)) {
          this.pendingClaim = null;
          this.operationError = message;
          this.activityMessage = '';
          await this.refresh();
        }
        return;
      }
      if (this.isCurrentWallet(pending)) {
        this.pendingClaim = pending;
        this.activityMessage = 'The transaction has not been confirmed yet. Pusharoo will keep tracking it; refresh the page to resume later.';
      }
    }
  }

  private async trackSponsoredClaim(pending: PendingFaucetClaim): Promise<void> {
    if (!pending.requestId) return;
    let notFoundCount = 0;
    for (let attempt = 0; attempt < 60; attempt += 1) {
      if (this.destroyed) return;
      try {
        const claim = await this.api.getClaim(pending.requestId);
        notFoundCount = 0;
        const updated: PendingFaucetClaim = { ...pending, transactionHash: claim.transactionHash ?? pending.transactionHash };
        this.savePending(updated);
        if (this.isCurrentWallet(pending)) {
          this.pendingClaim = updated;
          this.activityMessage = this.sponsoredStateText(claim.state);
        }
        if (claim.state.toLowerCase() === 'confirmed') {
          let amount = '';
          if (updated.transactionHash) {
            try {
              const confirmed = await this.neoRpc.waitForFaucetClaim(updated.transactionHash, this.testnetContractHash, pending.scriptHash);
              amount = this.formatDatoshi(confirmed.amountDatoshi);
            } catch {
              // The relayer only reports Confirmed after validating the matching on-chain events.
            }
          }
          this.removePending(pending);
          if (this.isCurrentWallet(pending)) {
            this.pendingClaim = null;
            this.receivedAmount = amount;
            this.successMessage = amount
              ? `Claim confirmed. ${amount} GAS was sent to your wallet.`
              : 'Your sponsored claim is confirmed on testnet.';
            this.activityMessage = '';
            await this.refresh();
          }
          return;
        }
        if (claim.state.toLowerCase() === 'failed') {
          this.removePending(pending);
          if (this.isCurrentWallet(pending)) {
            this.pendingClaim = null;
            this.operationError = claim.error || 'The sponsored claim did not complete. The relayer may still have paid a transaction fee.';
            this.activityMessage = '';
            await this.refresh();
          }
          return;
        }
        if (claim.state.toLowerCase() === 'needsreview') return;
      } catch (error) {
        if (error instanceof HttpErrorResponse && error.status === 404 && ++notFoundCount >= 3) {
          this.removePending(pending);
          if (this.isCurrentWallet(pending)) {
            this.pendingClaim = null;
            this.activityMessage = '';
            this.operationError = 'Pusharoo did not receive this claim. You can request it again.';
          }
          return;
        }
        // Keep the request locally so polling can resume after a refresh or API recovery.
      }
      await this.delay(3000);
    }
    if (this.isCurrentWallet(pending)) this.activityMessage = 'The sponsored claim is still pending. It will resume tracking when you return.';
  }

  private sponsoredStateText(state: string): string {
    switch (state.toLowerCase()) {
      case 'queued': return 'Your sponsored claim is queued.';
      case 'processing':
      case 'broadcasting': return 'Pusharoo is preparing the sponsored claim transaction.';
      case 'submitted': return 'Transaction submitted. Waiting for testnet confirmation.';
      case 'needsreview': return 'The claim needs operator review. Keep this page or return later to check its status.';
      case 'confirmed': return 'Sponsored claim confirmed.';
      case 'failed': return 'The sponsored claim failed.';
      default: return `Sponsored claim status: ${state}.`;
    }
  }

  private readWalletContext(): WalletContext {
    const account = this.wallet.account();
    const session = this.wallet.session();
    const selectedNetwork = this.wallet.selectedNetwork();
    const sessionNetwork = session?.network ?? null;
    const scriptHash = account?.scriptHash ?? null;
    const address = account?.address ?? null;
    const key = `${scriptHash?.toLowerCase() ?? 'disconnected'}|${selectedNetwork}|${sessionNetwork ?? 'no-session'}`;
    return { address, scriptHash, selectedNetwork, sessionNetwork, key };
  }

  private requireTestnetContext(context: WalletContext): boolean {
    if (context.selectedNetwork !== 'neo3:testnet' || context.sessionNetwork !== 'neo3:testnet' || !context.address || !context.scriptHash) {
      this.operationError = 'Connect a wallet with both Pusharoo and the wallet session on N3:Testnet.';
      return false;
    }
    return true;
  }

  private assertSameContext(expected: WalletContext): void {
    const current = this.readWalletContext();
    if (current.key !== expected.key || current.selectedNetwork !== 'neo3:testnet' || current.sessionNetwork !== 'neo3:testnet') {
      throw new Error('Wallet account or network changed. Reconnect on N3:Testnet and start the claim again.');
    }
  }

  private isCurrentWallet(pending: PendingFaucetClaim): boolean {
    const context = this.readWalletContext();
    return context.sessionNetwork === pending.network && this.sameHash(context.scriptHash ?? '', pending.scriptHash);
  }

  private findPending(context: WalletContext): PendingFaucetClaim | null {
    if (!context.scriptHash || context.sessionNetwork !== 'neo3:testnet') return null;
    return this.readPending().find((item) => item.network === 'neo3:testnet' && this.sameHash(item.scriptHash, context.scriptHash!)) ?? null;
  }

  private savePending(pending: PendingFaucetClaim): void {
    const all = this.readPending().filter((item) => {
      if (item.route !== pending.route) return true;
      if (pending.requestId) return item.requestId !== pending.requestId;
      if (pending.transactionHash) return item.transactionHash !== pending.transactionHash;
      return true;
    });
    all.push(pending);
    try { localStorage.setItem(this.pendingStorageKey, JSON.stringify(all.slice(-10))); } catch { }
  }

  private removePending(pending: PendingFaucetClaim): void {
    const all = this.readPending().filter((item) =>
      !(item.network === pending.network && this.sameHash(item.scriptHash, pending.scriptHash)
        && (pending.requestId ? item.requestId === pending.requestId : item.transactionHash === pending.transactionHash))
    );
    try { localStorage.setItem(this.pendingStorageKey, JSON.stringify(all)); } catch { }
  }

  private readPending(): PendingFaucetClaim[] {
    try {
      const value = localStorage.getItem(this.pendingStorageKey);
      if (!value) return [];
      const parsed = JSON.parse(value) as unknown;
      return Array.isArray(parsed) ? parsed as PendingFaucetClaim[] : [];
    } catch {
      return [];
    }
  }

  private toStack(result: ContractInvokeResult): RpcStackValue[] {
    const top = result.stack?.[0];
    if (!top || !Array.isArray(top.value)) throw new Error('The faucet contract returned an invalid status response.');
    return top.value as RpcStackValue[];
  }

  private stackText(item: RpcStackValue | undefined): string {
    return item?.value === null || item?.value === undefined ? '' : String(item.value);
  }

  private stackBoolean(item: RpcStackValue | undefined): string {
    const value = item?.value;
    if (value === true || value === 'true') return 'true';
    if (value === false || value === 'false') return 'false';
    return this.stackText(item);
  }

  private stackInteger(item: RpcStackValue | undefined): string {
    const value = this.stackText(item);
    if (/^-?\d+$/.test(value)) return value;
    if ((item?.type === 'ByteString' || item?.type === 'Buffer') && value) {
      const bytes = [...atob(value)].map((character) => character.charCodeAt(0));
      let result = 0n;
      for (let index = bytes.length - 1; index >= 0; index -= 1) result = (result << 8n) | BigInt(bytes[index]);
      if (bytes.length && (bytes[bytes.length - 1] & 0x80)) result -= 1n << BigInt(bytes.length * 8);
      return result.toString();
    }
    throw new Error('The faucet contract returned an invalid number.');
  }

  private errorText(error: unknown, fallback: string): string {
    if (error && typeof error === 'object' && 'error' in error) {
      const body = (error as { error?: unknown }).error;
      if (body && typeof body === 'object') {
        const apiMessage = (body as { error?: unknown; message?: unknown }).error
          ?? (body as { message?: unknown }).message;
        if (typeof apiMessage === 'string' && apiMessage) return apiMessage;
      }
    }
    if (error instanceof Error && error.message) {
      const message = error.message.toLowerCase();
      if (message.includes('reject') || message.includes('cancel')) return 'Wallet request cancelled.';
      return error.message;
    }
    return fallback;
  }

  private sameHash(left: string, right: string): boolean {
    return left.replace(/^0x/i, '').toLowerCase() === right.replace(/^0x/i, '').toLowerCase();
  }

  private delay(milliseconds: number): Promise<void> {
    return new Promise((resolve) => window.setTimeout(resolve, milliseconds));
  }
}
