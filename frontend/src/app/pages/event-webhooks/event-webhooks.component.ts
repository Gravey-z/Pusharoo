import { Component, OnInit, inject } from '@angular/core';
import { DatePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { Observable } from 'rxjs';
import {
  Artifact,
  CreateWebhookSubscriptionRequest,
  Deployment,
  ProjectOverviewViewModel,
  WebhookDelivery,
  WebhookSubscription,
  RelayUsage,
  RelayPaymentHistory,
  RelayPaymentIntent
} from '../../models/pusharoo.models';
import { DeploymentHistoryService } from '../../services/deployment-history.service';
import { ProjectOwnershipService } from '../../services/project-ownership.service';
import { EventRelayApiService } from '../../services/event-relay-api.service';
import { PusharooApiService } from '../../services/pusharoo-api.service';
import { ApiErrorFormatterService } from '../../services/api-error-formatter.service';
import { WalletService } from '../../services/wallet.service';
import { WalletAuthService } from '../../services/wallet-auth.service';
import { PageShellComponent } from '../page-shell/page-shell.component';
import { ProjectReleaseNavComponent } from '../../components/project-release-nav/project-release-nav.component';
import { ProjectWorkspaceContextService } from '../../services/project-workspace-context.service';

interface DeploymentOption {
  label: string;
  contractHash: string;
  network: string;
  artifact: Artifact;
}

@Component({
  selector: 'app-event-webhooks',
  imports: [DatePipe, FormsModule, PageShellComponent, ProjectReleaseNavComponent, RouterLink],
  templateUrl: './event-webhooks.component.html',
  styleUrl: './event-webhooks.component.scss'
})
export class EventWebhooksComponent implements OnInit {
  overview: ProjectOverviewViewModel | null = null;
  subscriptions: WebhookSubscription[] = [];
  readonly subscriptionsByNetwork: Record<string, WebhookSubscription[]> = {};
  private allDeploymentOptions: DeploymentOption[] = [];
  eventOptions: string[] = [];
  relayNetwork = '';
  webhookNetwork = 'neo3:testnet';
  relayStatuses: Record<string, 'ok' | 'degraded' | 'offline'> = {};
  readonly relayUsageByNetwork: Record<string, RelayUsage> = {};
  paymentHistory: RelayPaymentHistory | null = null;
  connectingRelay = false;
  renewingRelay = false;
  payingIntentId = '';
  resumingIntentId = '';
  projectId = '';
  name = '';
  contractHash = '';
  eventName = '';
  webhookUrl = '';
  secret = '';
  private readonly savingByNetwork: Record<string, boolean> = {};
  editingSubscriptionId = '';
  private readonly deliveryHistoryByNetwork: Record<string, WebhookDelivery[]> = {};
  private readonly deliveryHistoryForByNetwork: Record<string, string> = {};
  private readonly loadingDeliveryHistoryForByNetwork: Record<string, string> = {};
  private readonly sendingForByNetwork: Record<string, string> = {};
  private readonly formStatusByNetwork: Record<string, string> = {};
  private readonly errorMessageByNetwork: Record<string, string> = {};
  private readonly workspace = inject(ProjectWorkspaceContextService, { optional: true });

  get isSaving(): boolean { return this.savingByNetwork[this.webhookNetwork] ?? false; }
  set isSaving(value: boolean) { this.savingByNetwork[this.webhookNetwork] = value; }

  get deliveryHistory(): WebhookDelivery[] { return this.deliveryHistoryByNetwork[this.webhookNetwork] ?? []; }
  set deliveryHistory(value: WebhookDelivery[]) { this.deliveryHistoryByNetwork[this.webhookNetwork] = value; }

  get deliveryHistoryFor(): string { return this.deliveryHistoryForByNetwork[this.webhookNetwork] ?? ''; }
  set deliveryHistoryFor(value: string) { this.deliveryHistoryForByNetwork[this.webhookNetwork] = value; }

  get loadingDeliveryHistoryFor(): string { return this.loadingDeliveryHistoryForByNetwork[this.webhookNetwork] ?? ''; }
  set loadingDeliveryHistoryFor(value: string) { this.loadingDeliveryHistoryForByNetwork[this.webhookNetwork] = value; }

  get sendingFor(): string { return this.sendingForByNetwork[this.webhookNetwork] ?? ''; }
  set sendingFor(value: string) { this.sendingForByNetwork[this.webhookNetwork] = value; }

  get formStatus(): string { return this.formStatusByNetwork[this.webhookNetwork] ?? ''; }
  set formStatus(value: string) { this.formStatusByNetwork[this.webhookNetwork] = value; }

  get errorMessage(): string { return this.errorMessageByNetwork[this.webhookNetwork] ?? ''; }
  set errorMessage(value: string) { this.errorMessageByNetwork[this.webhookNetwork] = value; }

  get pageTitle(): string {
    return 'Event Webhooks';
  }

  get selectedDeployment(): DeploymentOption | null {
    return this.deploymentOptions.find((deployment) => deployment.contractHash === this.contractHash) ?? null;
  }

  get deploymentOptions(): DeploymentOption[] {
    return this.allDeploymentOptions.filter((deployment) => deployment.network === this.webhookNetwork);
  }

  get canManageProject(): boolean {
    return this.ownership.canManage(this.overview?.project, this.wallet.account()?.address ?? '');
  }

  get isWalletConnected(): boolean {
    return this.wallet.account() !== null;
  }

  get isRelayConnected(): boolean {
    return this.auth.canUseSession() && this.canManageProject;
  }

  constructor(
    private readonly route: ActivatedRoute,
    private readonly api: EventRelayApiService,
    private readonly projectApi: PusharooApiService,
    private readonly errors: ApiErrorFormatterService,
    private readonly deploymentHistory: DeploymentHistoryService,
    private readonly ownership: ProjectOwnershipService,
    readonly auth: WalletAuthService,
    private readonly wallet: WalletService
  ) {
    this.projectId = (this.route.parent ?? this.route).snapshot.paramMap.get('projectId') ?? '';
  }

  ngOnInit(): void {
    this.load();
  }

  async save(): Promise<void> {
    this.errorMessage = '';
    this.formStatus = '';

    if (!this.name.trim()) {
      this.errorMessage = 'Name this webhook.';
      return;
    }

    if (!this.contractHash) {
      this.errorMessage = 'Choose a deployed contract.';
      return;
    }

    if (!this.selectedDeployment) {
      this.errorMessage = 'Choose a deployment monitored by Pusharoo Relay.';
      return;
    }

    if (!this.webhookUrl.trim()) {
      this.errorMessage = 'Enter the endpoint URL.';
      return;
    }

    this.isSaving = true;

    try {
      const subscriptionRequest = {
        name: this.name.trim(),
        contractHash: this.contractHash,
        network: this.selectedDeployment.network,
        eventName: this.eventName || null,
        webhookUrl: this.webhookUrl.trim(),
        projectId: this.projectId,
        secret: this.secret.trim() || null,
        headers: {},
        isEnabled: true
      };
      const network = this.selectedDeployment.network;
      if (this.editingSubscriptionId) {
        await this.executeWebhookRequest(() => this.api.updateWebhookSubscription(
          this.projectId, network, this.editingSubscriptionId, subscriptionRequest
        ));
      } else {
        await this.executeWebhookRequest(() =>
          this.api.createWebhookSubscription(this.projectId, network, subscriptionRequest)
        );
      }

      this.formStatus = this.editingSubscriptionId ? 'Webhook updated.' : 'Webhook created.';
      this.resetForm();
      await this.loadSubscriptions(network);
    } catch (error) {
      this.errorMessage = this.errors.format(error, 'Could not create the webhook subscription.');
    } finally {
      this.isSaving = false;
    }
  }

  latestDelivery(subscriptionId: string): WebhookDelivery | null {
    return this.subscriptions.find((subscription) => subscription.id === subscriptionId)?.latestDelivery ?? null;
  }

  edit(subscription: WebhookSubscription): void {
    this.editingSubscriptionId = subscription.id;
    this.name = subscription.name;
    this.contractHash = subscription.contractHash;
    this.eventName = subscription.eventName ?? '';
    this.webhookUrl = subscription.webhookUrl;
    this.secret = '';
    this.selectDeployment();
    this.eventName = subscription.eventName ?? '';
    this.errorMessage = '';
    this.formStatus = 'Editing webhook. Leave the signing secret empty to keep the existing secret.';
  }

  cancelEdit(): void {
    this.resetForm();
    this.errorMessage = '';
    this.formStatus = '';
  }

  async toggle(subscription: WebhookSubscription): Promise<void> {
    await this.updateSubscription(subscription, { isEnabled: !subscription.isEnabled });
  }

  async remove(subscription: WebhookSubscription): Promise<void> {
    if (!confirm(`Delete webhook “${subscription.name}”?`)) {
      return;
    }

    this.errorMessage = '';
    try {
      await this.executeWebhookRequest(() =>
        this.api.deleteWebhookSubscription(this.projectId, subscription.network, subscription.id));
      this.formStatus = 'Webhook deleted.';
      if (this.editingSubscriptionId === subscription.id) {
        this.resetForm();
      }
      await this.loadSubscriptions(subscription.network);
    } catch (error) {
      this.errorMessage = this.errors.format(error, 'Could not delete the webhook.');
    }
  }

  async showDeliveryHistory(subscription: WebhookSubscription): Promise<void> {
    this.errorMessage = '';
    this.loadingDeliveryHistoryFor = subscription.id;
    try {
      this.deliveryHistory = await this.executeWebhookRequest(() =>
        this.api.getWebhookDeliveries(this.projectId, subscription.network, subscription.id));
      this.deliveryHistoryFor = subscription.id;
    } catch (error) {
      this.deliveryHistory = [];
      this.deliveryHistoryFor = '';
      this.errorMessage = this.errors.format(error, 'Could not load webhook delivery history.');
    } finally {
      this.loadingDeliveryHistoryFor = '';
    }
  }

  async sendTest(subscription: WebhookSubscription): Promise<void> {
    await this.runDeliveryAction(subscription, undefined, 'Sending test event...',
      () => this.api.sendWebhookTest(this.projectId, subscription.network, subscription.id));
  }

  async redeliver(subscription: WebhookSubscription, delivery: WebhookDelivery): Promise<void> {
    await this.runDeliveryAction(subscription, delivery.id, 'Redelivering event...',
      () => this.api.redeliverWebhook(this.projectId, subscription.network, subscription.id, delivery.id));
  }

  async renewRelay(): Promise<void> {
    this.errorMessage = '';
    this.formStatus = '';
    if (!this.canManageProject) {
      this.errorMessage = 'Only the project owner can renew Relay access.';
      return;
    }
    const account = this.wallet.account();
    if (!account || this.wallet.session()?.network !== 'neo3:mainnet') {
      this.errorMessage = 'Connect the project owner wallet on N3:Mainnet before renewing.';
      return;
    }
    this.renewingRelay = true;
    try {
      await this.auth.ensureAuthenticated();
      await firstValueFrom(this.api.createRelayPaymentIntent(this.projectId));
      await this.loadPaymentHistory();
      this.formStatus = 'Payment intent created. Review the recipient and amount, then approve the GAS transfer in your wallet.';
    } catch (error) {
      this.errorMessage = this.errors.format(error, 'Could not create a Relay payment intent.');
    } finally {
      this.renewingRelay = false;
    }
  }

  async connectRelay(): Promise<void> {
    this.errorMessage = '';
    this.formStatus = '';
    if (!this.canManageProject) {
      this.errorMessage = 'Only the project owner can connect to N3:Mainnet Relay.';
      return;
    }
    this.connectingRelay = true;
    try {
      await this.auth.ensureAuthenticated();
      await this.loadUsage(this.webhookNetwork);
      if (this.deploymentOptions.length) await this.loadSubscriptions(this.webhookNetwork);
      if (this.webhookNetwork === 'neo3:mainnet') await this.loadPaymentHistory();
    } catch (error) {
      this.errorMessage = this.errors.format(error, 'Could not open the Relay workspace.');
    } finally {
      this.connectingRelay = false;
    }
  }

  async payRelayIntent(intent: RelayPaymentIntent): Promise<void> {
    const account = this.wallet.account();
    if (!account || this.wallet.session()?.network !== 'neo3:mainnet') {
      this.errorMessage = 'Connect the project owner wallet on N3:Mainnet before approving this payment.';
      return;
    }
    this.errorMessage = '';
    this.payingIntentId = intent.id;
    try {
      await this.auth.ensureAuthenticated();
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
      const confirmation = await this.confirmPayment(intent.id, transactionId);
      this.formStatus = confirmation.status === 'confirmed'
        ? `Relay renewed until ${new Date(confirmation.entitlementEndsAt ?? '').toLocaleDateString('en-GB')}.`
        : confirmation.message ?? 'Payment submitted. Relay access will renew once N3:Mainnet finality is reached.';
      await Promise.all([this.loadUsage('neo3:testnet'), this.loadUsage('neo3:mainnet'), this.loadPaymentHistory()]);
    } catch (error) {
      this.errorMessage = this.errors.format(error, 'Could not submit the Relay payment.');
    } finally {
      this.payingIntentId = '';
    }
  }

  async resumeRelayPayment(intent: RelayPaymentIntent): Promise<void> {
    if (!intent.submittedTransactionId) return;
    this.errorMessage = '';
    this.resumingIntentId = intent.id;
    try {
      const confirmation = await this.confirmPayment(intent.id, intent.submittedTransactionId);
      this.formStatus = confirmation.status === 'confirmed'
        ? `Relay renewed until ${new Date(confirmation.entitlementEndsAt ?? '').toLocaleDateString('en-GB')}.`
        : confirmation.message ?? 'Payment is still waiting for N3:Mainnet finality.';
      await Promise.all([this.loadUsage('neo3:testnet'), this.loadUsage('neo3:mainnet'), this.loadPaymentHistory()]);
    } catch (error) {
      this.errorMessage = this.errors.format(error, 'Could not confirm the submitted Relay payment.');
    } finally {
      this.resumingIntentId = '';
    }
  }

  async showPaymentHistory(): Promise<void> {
    this.errorMessage = '';
    try {
      await this.loadPaymentHistory();
    } catch (error) {
      this.errorMessage = this.errors.format(error, 'Could not load Relay payment history.');
    }
  }

  gasAmount(datoshis: number): string {
    return (datoshis / 100000000).toLocaleString(undefined, { maximumFractionDigits: 8 });
  }

  eventLabel(subscription: WebhookSubscription): string {
    return subscription.eventName || 'All events';
  }

  shortHash(value: string | null | undefined): string {
    if (!value) {
      return '-';
    }

    return value.length > 17 ? `${value.slice(0, 10)}...${value.slice(-4)}` : value;
  }

  networkLabel(network: string): string {
    return this.wallet.networkLabel(network);
  }

  private async load(): Promise<void> {
    try {
      const cachedOverview = this.workspace?.getFreshOverview(this.projectId);
      this.overview = cachedOverview?.project.id === this.projectId
        ? cachedOverview
        : await firstValueFrom(this.projectApi.getProjectOverview(this.projectId));
      if (this.workspace && this.overview) {
        this.workspace.overview = this.overview;
      }
      this.allDeploymentOptions = this.toDeploymentOptions(this.overview?.deployments ?? []);
      this.contractHash = this.deploymentOptions[0]?.contractHash ?? '';
      this.selectDeployment();
      await Promise.all(['neo3:testnet', 'neo3:mainnet'].map(async (network) => {
        try { this.relayStatuses[network] = (await firstValueFrom(this.api.getEventRelayStatus(network))).status === 'ok' ? 'ok' : 'degraded'; }
        catch (error: unknown) { this.relayStatuses[network] = (error as { status?: number })?.status === 503 ? 'degraded' : 'offline'; }
      }));

      await this.auth.restore();

      // Keep page loading read-only; sign in only after an explicit owner action.
      if (!this.deploymentOptions.length) {
        return;
      }

      if (!this.isWalletConnected || !this.auth.canUseSession() || !this.canManageProject) {
        return;
      }

      await this.loadSubscriptions();
      await this.loadUsage(this.relayNetwork);
      if (this.auth.canUseSession()) {
        await Promise.all([this.loadUsage('neo3:mainnet'), this.loadPaymentHistory()]);
      }
    } catch (error) {
      this.errorMessage = this.errors.format(error, 'Could not load webhook subscriptions.');
    }
  }

  private async loadSubscriptions(network = this.relayNetwork): Promise<void> {
    const subscriptions = await this.executeWebhookRequest(() =>
      this.api.getWebhookSubscriptions(this.projectId, network));
    this.subscriptionsByNetwork[network] = subscriptions;
    if (network === this.relayNetwork) {
      this.subscriptions = subscriptions;
    }
  }

  private async loadUsage(network: string): Promise<void> {
    this.relayUsageByNetwork[network] = await this.executeWebhookRequest(() => this.api.getRelayUsage(this.projectId, network));
  }

  private async loadPaymentHistory(): Promise<void> {
    this.paymentHistory = await this.executeWebhookRequest(() => this.api.getRelayPaymentHistory(this.projectId));
  }

  private async confirmPayment(intentId: string, transactionId: string) {
    await this.auth.ensureAuthenticated();
    return firstValueFrom(this.api.confirmRelayPayment(this.projectId, intentId, transactionId));
  }

  private async updateSubscription(
    subscription: WebhookSubscription,
    changes: Partial<CreateWebhookSubscriptionRequest>
  ): Promise<void> {
    this.errorMessage = '';
    const request = {
      name: subscription.name,
      contractHash: subscription.contractHash,
      network: subscription.network,
      eventName: subscription.eventName ?? null,
      webhookUrl: subscription.webhookUrl,
      secret: null,
      headers: subscription.headers,
      isEnabled: subscription.isEnabled,
      ...changes
    };
    try {
      await this.executeWebhookRequest(() => this.api.updateWebhookSubscription(
        this.projectId, subscription.network, subscription.id, request
      ));
      this.formStatus = request.isEnabled ? 'Webhook enabled.' : 'Webhook disabled.';
      await this.loadSubscriptions(subscription.network);
    } catch (error) {
      this.errorMessage = this.errors.format(error, 'Could not update the webhook.');
    }
  }

  private resetForm(): void {
    this.editingSubscriptionId = '';
    this.name = '';
    this.webhookUrl = '';
    this.secret = '';
  }

  private async runDeliveryAction(
    subscription: WebhookSubscription,
    deliveryId: string | undefined,
    status: string,
    action: () => Observable<WebhookDelivery>
  ): Promise<void> {
    this.errorMessage = '';
    this.formStatus = status;
    this.sendingFor = `${subscription.id}:${deliveryId ?? 'test'}`;
    try {
      await this.executeWebhookRequest(action);
      this.formStatus = deliveryId ? 'Failed event redelivered.' : 'Test event queued.';
      await this.loadSubscriptions(subscription.network);
      await this.showDeliveryHistory(subscription);
    } catch (error) {
      this.errorMessage = this.errors.format(error, deliveryId ? 'Could not redeliver the event.' : 'Could not send a test event.');
    } finally {
      this.sendingFor = '';
    }
  }

  selectDeployment(): void {
    if (this.selectedDeployment) {
      this.useRelay(this.selectedDeployment.network);
    }
    this.eventOptions = this.selectedDeployment?.artifact.manifest.abi.events
      .map((event) => event.name) ?? [];
    this.eventName = this.eventOptions[0] ?? '';
  }

  async selectWebhookNetwork(network: string): Promise<void> {
    this.webhookNetwork = network;
    this.useRelay(network);
    this.contractHash = this.deploymentOptions[0]?.contractHash ?? '';
    this.eventOptions = [];
    this.eventName = '';
    this.editingSubscriptionId = '';
    this.secret = '';
    if (this.contractHash) {
      this.selectDeployment();
    }
    this.subscriptions = this.subscriptionsByNetwork[network] ?? [];
    if (!this.isRelayConnected) {
      return;
    }
    if (!this.subscriptionsByNetwork[network] && this.deploymentOptions.length) {
      await this.loadSubscriptions(network);
      await this.loadUsage(network);
    }
  }

  private useRelay(network: string): void {
    this.relayNetwork = network;
    this.webhookNetwork = network;
  }

  private async executeWebhookRequest<T>(request: () => Observable<T>): Promise<T> {
    await this.auth.ensureAuthenticated();
    return firstValueFrom(request());
  }

  private toDeploymentOptions(deployments: Deployment[]): DeploymentOption[] {
    return this.deploymentHistory
      .latestConfirmedByNetwork(deployments)
      .map((deployment) => {
        const artifact = this.overview?.artifacts.find((item) => item.id === deployment.artifactId);
        return artifact
          ? {
              label: `${this.networkLabel(deployment.network)} - ${deployment.version}`,
              contractHash: deployment.contractHash ?? '',
              network: deployment.network,
              artifact
            }
          : null;
      })
      .filter((deployment): deployment is DeploymentOption => deployment !== null);
  }
}
