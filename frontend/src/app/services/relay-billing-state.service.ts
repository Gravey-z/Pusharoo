import { Injectable, OnDestroy } from '@angular/core';
import { firstValueFrom, timeout } from 'rxjs';
import {
  CreateRelayPaymentIntentRequest,
  Project,
  RelayPackageCatalog,
  RelayPaymentHistory,
  RelayPaymentIntent,
  RelayUsage,
  WebhookSubscription
} from '../models/pusharoo.models';
import { ApiErrorFormatterService } from './api-error-formatter.service';
import { EventRelayApiService } from './event-relay-api.service';
import { ProjectOwnershipService } from './project-ownership.service';
import { WalletAuthService } from './wallet-auth.service';
import { WalletService } from './wallet.service';

interface LocalRelaySubmission {
  intent: RelayPaymentIntent;
  payerAddress: string;
  transactionId?: string;
}

@Injectable()
export class RelayBillingStateService implements OnDestroy {
  project: Project | null = null;
  catalog: RelayPackageCatalog | null = null;
  history: RelayPaymentHistory | null = null;
  usage: Record<string, RelayUsage> = {};
  subscriptions: WebhookSubscription[] = [];
  localSubmission: LocalRelaySubmission | null = null;
  loading = false;
  busy = false;
  ready = false;
  error = '';
  message = '';
  now = Date.now();
  onRefresh: (usage: Record<string, RelayUsage>) => void = () => {};
  private generation = 0;
  private timer?: ReturnType<typeof setTimeout>;
  private quoteRequest: CreateRelayPaymentIntentRequest | null = null;

  constructor(
    private readonly api: EventRelayApiService,
    private readonly auth: WalletAuthService,
    private readonly wallet: WalletService,
    private readonly ownership: ProjectOwnershipService,
    private readonly errors: ApiErrorFormatterService
  ) {}

  get canManage(): boolean {
    return this.ownership.canManage(this.project, this.wallet.account()?.address ?? '');
  }

  get connected(): boolean {
    return this.canManage && this.auth.canUseSession();
  }

  get mainnetWallet(): boolean {
    return this.connected && this.wallet.session()?.network === 'neo3:mainnet';
  }

  get pendingIntents(): RelayPaymentIntent[] {
    const intents = [...(this.history?.pendingIntents ?? [])];
    for (const recent of this.history?.recentIntents ?? []) {
      if (['expired', 'rejected'].includes(recent.status) && !intents.some(x => x.id === recent.id))
        intents.push(recent);
    }
    const local = this.localSubmission;
    if (!local || intents.some(x => x.id === local.intent.id)) return intents;
    return [...intents, local.intent];
  }

  get openIntent(): boolean {
    return this.pendingIntents.some(
      intent =>
        (this.transactionId(intent) &&
          !['confirmed', 'resolved', 'rejected'].includes(intent.status)) ||
        ['applying', 'reconciliation_required'].includes(intent.status) ||
        (intent.status === 'pending' && Date.parse(intent.expiresAt) > this.now) ||
        this.localSubmission?.intent.id === intent.id
    );
  }

  start(project: Project | null): void {
    this.stop();
    this.project = project;
    this.catalog = null;
    this.history = null;
    this.usage = {};
    this.subscriptions = [];
    this.error = '';
    this.message = '';
    this.ready = false;
    this.busy = false;
    this.loading = false;
    this.quoteRequest = null;
    this.localSubmission = this.readSubmission();
    const generation = this.generation;
    if (this.connected)
      void this.refresh().finally(() => {
        if (generation === this.generation) this.schedule();
      });
  }

  ngOnDestroy(): void {
    this.stop();
  }

  async refresh(): Promise<void> {
    if (!this.connected || !this.project || this.loading || this.busy) return;
    this.loading = true;
    const generation = this.generation;
    const projectId = this.project.id;
    try {
      this.localSubmission = this.readSubmission() ?? this.localSubmission;
      const local = this.localSubmission;
      if (local?.transactionId) {
        try {
          await firstValueFrom(
            this.api
              .confirmRelayPayment(projectId, local.intent.id, local.transactionId)
              .pipe(timeout(20000))
          );
        } catch (error) {
          if (generation === this.generation)
            this.error = this.errors.format(
              error,
              'The submitted payment could not be checked. Retry confirmation; do not send another transfer.'
            );
        }
      }

      const [catalog, history, mainnet, testnet, mainnetSubscriptions, testnetSubscriptions] =
        await Promise.all([
          firstValueFrom(this.api.getRelayPackages(projectId).pipe(timeout(20000))),
          firstValueFrom(this.api.getRelayPaymentHistory(projectId).pipe(timeout(20000))),
          firstValueFrom(this.api.getRelayUsage(projectId, 'neo3:mainnet').pipe(timeout(20000))),
          firstValueFrom(this.api.getRelayUsage(projectId, 'neo3:testnet').pipe(timeout(20000))),
          firstValueFrom(
            this.api.getWebhookSubscriptions(projectId, 'neo3:mainnet').pipe(timeout(20000))
          ),
          firstValueFrom(
            this.api.getWebhookSubscriptions(projectId, 'neo3:testnet').pipe(timeout(20000))
          )
        ]);
      if (generation !== this.generation || !this.connected) return;

      this.catalog = catalog;
      const expiredQuotes = (this.history?.pendingIntents ?? [])
        .filter(
          intent =>
            !intent.submittedTransactionId &&
            Date.parse(intent.expiresAt) <= Date.now() &&
            !history.pendingIntents.some(x => x.id === intent.id) &&
            !history.payments.some(x => x.intentId === intent.id)
        )
        .map(intent => ({ ...intent, status: 'expired' }));
      history.pendingIntents = [...history.pendingIntents, ...expiredQuotes];
      this.history = history;
      this.usage = { 'neo3:mainnet': mainnet, 'neo3:testnet': testnet };
      this.subscriptions = [...mainnetSubscriptions, ...testnetSubscriptions];
      this.now = Date.now();
      this.ready = true;
      this.reconcileSubmission();
      this.onRefresh(this.usage);
    } catch (error) {
      if (generation === this.generation) {
        this.ready = false;
        this.error = this.errors.format(
          error,
          'Billing could not be refreshed. Refresh before creating or paying a quote.'
        );
      }
    } finally {
      if (generation === this.generation) this.loading = false;
    }
  }

  async quote(
    packageId: string,
    operation: 'purchase_period' | 'upgrade_periods',
    periodIds: string[] = []
  ): Promise<void> {
    if (this.busy || this.loading) return;
    this.error = '';
    if (!this.mainnetWallet || !this.ready || !this.catalog?.billingReady || this.openIntent) {
      this.error =
        'Connect the owner wallet on N3:Mainnet, refresh billing, and finish any open payment before requesting a quote.';
      return;
    }
    const selection = { packageId, operation, periodIds: [...periodIds].sort() };
    const previous = this.quoteRequest;
    if (
      !previous ||
      JSON.stringify({ ...previous, requestKey: '' }) !==
        JSON.stringify({ ...selection, requestKey: '' })
    ) {
      this.quoteRequest = { ...selection, requestKey: crypto.randomUUID() };
    }
    await this.perform(async () => {
      const generation = this.generation;
      const intent = await firstValueFrom(
        this.api.createRelayPaymentIntent(this.project!.id, this.quoteRequest!).pipe(timeout(20000))
      );
      if (generation !== this.generation || !this.connected) return;
      this.history = { payments: [], entitlements: [], ...this.history, pendingIntents: [intent] };
      this.quoteRequest = null;
      this.message =
        'Quote ready. Review the stored terms below before opening your wallet. An unpaid quote reserves billing until its deadline.';
    }, 'Could not create the quote. Retry the same selection or refresh to recover an existing quote.');
  }

  canPay(intent: RelayPaymentIntent): boolean {
    return (
      this.mainnetWallet &&
      this.ready &&
      !this.busy &&
      !this.loading &&
      intent.projectId === this.project?.id &&
      intent.network === 'neo3:mainnet' &&
      intent.status === 'pending' &&
      Date.parse(intent.expiresAt) > Date.now() &&
      !this.transactionId(intent) &&
      this.localSubmission?.intent.id !== intent.id &&
      Number.isSafeInteger(intent.requiredGasDatoshis) &&
      intent.requiredGasDatoshis > 0 &&
      /^0x[\da-f]{40}$/i.test(intent.recipientScriptHash) &&
      (!intent.terms || intent.terms.requiredGasDatoshis === intent.requiredGasDatoshis)
    );
  }

  async pay(intent: RelayPaymentIntent): Promise<void> {
    if (!this.canPay(intent)) return;
    const generation = this.generation;
    await this.perform(async () => {
      if (!navigator.locks)
        throw new Error(
          'This browser cannot coordinate wallet payments safely. Use a browser with Web Locks support.'
        );
      await navigator.locks.request(
        `pusharoo.relay.${intent.projectId}.${intent.id}`,
        { ifAvailable: true },
        async lock => {
          if (!lock)
            throw new Error(
              'This payment is already open in another tab. Check that wallet attempt before continuing.'
            );
          if (
            generation !== this.generation ||
            !this.mainnetWallet ||
            Date.parse(intent.expiresAt) <= Date.now()
          )
            throw new Error('The wallet or quote changed. Refresh billing before paying.');
          const prior = this.readSubmission();
          if (prior) {
            this.localSubmission = prior;
            throw new Error(
              'A saved wallet attempt already exists. Recover it before trying another transfer.'
            );
          }
          const account = this.wallet.account()!;
          const submission: LocalRelaySubmission = { intent, payerAddress: account.address };
          this.storeSubmission(submission);
          const transactionId = await this.wallet.invokeContract(
            'neo3:mainnet',
            '0xd2a4cff31913016155e38e474a2c06d08be276cf',
            'transfer',
            [
              { type: 'Hash160', value: account.scriptHash },
              { type: 'Hash160', value: intent.recipientScriptHash },
              { type: 'Integer', value: String(intent.requiredGasDatoshis) },
              { type: 'Any', value: null }
            ],
            'GAS'
          );
          if (typeof transactionId !== 'string' || !/^(0x)?[\da-f]{64}$/i.test(transactionId))
            throw new Error(
              'The wallet did not return a valid transaction hash. Check its transaction history and recover the existing transfer before trying again.'
            );
          submission.transactionId = transactionId;
          if (generation === this.generation) this.localSubmission = submission;
          try {
            localStorage.setItem(this.submissionKey(submission), JSON.stringify(submission));
          } catch {
            if (generation === this.generation)
              this.error = `Transaction ${transactionId} was submitted but could not be saved in this browser. Keep its hash for recovery; do not transfer again.`;
          }
          if (
            this.auth.canUseSession() &&
            this.wallet.account()?.address === submission.payerAddress
          )
            await this.confirm(intent, transactionId, generation);
        }
      );
    }, 'The wallet or confirmation request did not finish. Check your wallet and recover the transaction below before trying another transfer.');
    if (generation === this.generation) await this.refresh();
  }

  transactionId(intent: RelayPaymentIntent): string {
    return (
      intent.submittedTransactionId ||
      intent.confirmedTransactionId ||
      (this.localSubmission?.intent.id === intent.id ? this.localSubmission.transactionId : '') ||
      ''
    );
  }

  async check(
    intent: RelayPaymentIntent,
    transactionId = this.transactionId(intent)
  ): Promise<void> {
    if (this.busy || !this.connected || intent.projectId !== this.project?.id) return;
    if (!/^(0x)?[\da-f]{64}$/i.test(transactionId.trim())) {
      this.error = 'Enter the transaction hash returned by your Mainnet wallet.';
      return;
    }
    await this.perform(async () => {
      const submission: LocalRelaySubmission = {
        intent,
        payerAddress: this.wallet.account()!.address,
        transactionId: transactionId.trim()
      };
      this.localSubmission = submission;
      try {
        this.storeSubmission(submission);
      } catch {
        this.error = `Keep transaction ${transactionId.trim()} for recovery: this browser could not save it. Checking this existing transfer will not request another payment.`;
      }
      await this.confirm(intent, transactionId.trim());
    }, 'Confirmation could not be checked. Keep this transaction hash and retry; do not send another transfer.');
    await this.refresh();
  }

  acknowledgeNoTransfer(intent: RelayPaymentIntent): void {
    if (this.busy || !this.connected || this.transactionId(intent)) return;
    if (this.localSubmission?.intent.id !== intent.id) return;
    try {
      this.removeSubmission();
      this.message =
        'Wallet attempt cleared after your confirmation that nothing was broadcast. Review the quote deadline before trying again.';
    } catch {
      this.error = 'The saved attempt could not be cleared. Enable site storage and retry.';
    }
  }

  async saveSelection(periodId: string, subscriptionIds: string[]): Promise<void> {
    if (this.busy || this.loading || !this.connected || !this.ready) return;
    await this.perform(async () => {
      await firstValueFrom(
        this.api
          .setRelayDowngradeSelection(this.project!.id, periodId, subscriptionIds)
          .pipe(timeout(20000))
      );
      this.message =
        'Webhook choices saved for this boundary. Changes apply when that Starter period starts.';
    }, 'Could not save the webhook choices. Refresh and check whether the boundary or enabled webhooks changed.');
    await this.refresh();
  }

  private async confirm(
    intent: RelayPaymentIntent,
    transactionId: string,
    generation = this.generation
  ): Promise<void> {
    const payment = await firstValueFrom(
      this.api.confirmRelayPayment(intent.projectId, intent.id, transactionId).pipe(timeout(20000))
    );
    if (generation !== this.generation || !this.connected) return;
    this.message =
      payment.message ||
      (payment.status === 'confirmed'
        ? 'Payment confirmed. The purchased periods and current access are shown below.'
        : 'Payment recorded. Confirmation and access updates continue automatically; do not send another transfer.');
  }

  private async perform(action: () => Promise<void>, fallback: string): Promise<void> {
    const generation = this.generation;
    this.busy = true;
    this.error = '';
    this.message = '';
    try {
      await this.auth.ensureAuthenticated();
      if (generation !== this.generation || !this.connected) return;
      await action();
    } catch (error) {
      if (generation === this.generation) this.error = this.errors.format(error, fallback);
    } finally {
      if (generation === this.generation) this.busy = false;
    }
  }

  private storageKey(): string {
    return `pusharoo.relayPayment.${this.project?.id}.${this.wallet.account()?.address}`;
  }

  private readSubmission(): LocalRelaySubmission | null {
    if (!this.project || !this.canManage) return null;
    try {
      const value = JSON.parse(
        localStorage.getItem(this.storageKey()) || 'null'
      ) as LocalRelaySubmission | null;
      return value?.intent?.projectId === this.project.id &&
        value.payerAddress === this.wallet.account()?.address
        ? value
        : null;
    } catch {
      return null;
    }
  }

  private storeSubmission(value: LocalRelaySubmission): void {
    try {
      localStorage.setItem(this.submissionKey(value), JSON.stringify(value));
    } catch {
      throw new Error(
        'Payment recovery cannot be saved in this browser. Enable site storage before opening the wallet.'
      );
    }
    this.localSubmission = value;
  }

  private submissionKey(value: LocalRelaySubmission): string {
    return `pusharoo.relayPayment.${value.intent.projectId}.${value.payerAddress}`;
  }

  private removeSubmission(): void {
    localStorage.removeItem(this.storageKey());
    this.localSubmission = null;
  }

  private reconcileSubmission(): void {
    const local = this.localSubmission;
    if (!local) return;
    const intent = this.history?.pendingIntents.find(x => x.id === local.intent.id);
    const recent = this.history?.recentIntents?.find(x => x.id === local.intent.id);
    const payment = this.history?.payments.find(x => x.intentId === local.intent.id);
    if (
      intent?.submittedTransactionId ||
      (recent && ['confirmed', 'resolved', 'rejected'].includes(recent.status)) ||
      (payment && ['confirmed', 'resolved', 'rejected'].includes(payment.status))
    ) {
      try {
        this.removeSubmission();
      } catch {
        this.localSubmission = null;
      }
    }
  }

  private schedule(): void {
    clearTimeout(this.timer);
    const generation = this.generation;
    this.timer = setTimeout(() => {
      this.now = Date.now();
      if (generation !== this.generation) return;
      if (typeof document !== 'undefined' && document.hidden) this.schedule();
      else
        void this.refresh().finally(() => {
          if (generation === this.generation) this.schedule();
        });
    }, 10000);
  }

  private stop(): void {
    this.generation++;
    clearTimeout(this.timer);
  }
}
