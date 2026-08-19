import { Component, OnInit } from '@angular/core';
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
  WebhookManagementOperation,
  WalletActionSignature,
  WebhookSubscription,
  RelayUsage
} from '../../models/pusharoo.models';
import { DeploymentHistoryService } from '../../services/deployment-history.service';
import { ProjectOwnershipService } from '../../services/project-ownership.service';
import { PusharooApiService } from '../../services/pusharoo-api.service';
import { ApiErrorFormatterService } from '../../services/api-error-formatter.service';
import { WalletService } from '../../services/wallet.service';
import { PageShellComponent } from '../page-shell/page-shell.component';
import { ProjectReleaseNavComponent } from '../../components/project-release-nav/project-release-nav.component';

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
  deploymentOptions: DeploymentOption[] = [];
  eventOptions: string[] = [];
  relayNetwork = '';
  webhookNetwork = 'neo3:testnet';
  relayStatuses: Record<string, 'ok' | 'degraded' | 'offline'> = {};
  readonly relayUsageByNetwork: Record<string, RelayUsage> = {};
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
    return this.overview ? `${this.overview.project.name}: Event Webhooks` : 'Event Webhooks';
  }

  get selectedDeployment(): DeploymentOption | null {
    return this.deploymentOptions.find((deployment) => deployment.contractHash === this.contractHash) ?? null;
  }

  get canManageProject(): boolean {
    return this.ownership.canManage(this.overview?.project, this.wallet.account()?.address ?? '');
  }

  constructor(
    private readonly route: ActivatedRoute,
    private readonly api: PusharooApiService,
    private readonly errors: ApiErrorFormatterService,
    private readonly deploymentHistory: DeploymentHistoryService,
    private readonly ownership: ProjectOwnershipService,
    private readonly wallet: WalletService
  ) {
    this.projectId = this.route.snapshot.paramMap.get('projectId') ?? '';
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
      const operation: WebhookManagementOperation = this.editingSubscriptionId
        ? 'subscriptions.update'
        : 'subscriptions.create';
      const network = this.selectedDeployment.network;
      if (this.editingSubscriptionId) {
        await this.executeWebhookRequest(network, operation, {
          subscriptionId: this.editingSubscriptionId,
          subscription: subscriptionRequest
        }, (signature) => this.api.updateWebhookSubscription(
          this.projectId, network, this.editingSubscriptionId, subscriptionRequest, signature
        ));
      } else {
        await this.executeWebhookRequest(network, operation, { subscription: subscriptionRequest }, (signature) =>
          this.api.createWebhookSubscription(this.projectId, network, subscriptionRequest, signature)
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
      await this.executeWebhookRequest(subscription.network, 'subscriptions.delete', { subscriptionId: subscription.id },
        (signature) => this.api.deleteWebhookSubscription(this.projectId, subscription.network, subscription.id, signature));
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
      this.deliveryHistory = await this.executeWebhookRequest(subscription.network, 'deliveries.read', { subscriptionId: subscription.id },
        (signature) => this.api.getWebhookDeliveries(this.projectId, subscription.network, subscription.id, signature));
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
    await this.runDeliveryAction(subscription, 'deliveries.test', undefined, 'Sending test event...',
      (signature) => this.api.sendWebhookTest(this.projectId, subscription.network, subscription.id, signature));
  }

  async redeliver(subscription: WebhookSubscription, delivery: WebhookDelivery): Promise<void> {
    await this.runDeliveryAction(subscription, 'deliveries.redeliver', delivery.id, 'Redelivering event...',
      (signature) => this.api.redeliverWebhook(this.projectId, subscription.network, subscription.id, delivery.id, signature));
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

  private async load(): Promise<void> {
    try {
      this.overview = await firstValueFrom(this.api.getProjectOverview(this.projectId));
      this.deploymentOptions = this.toDeploymentOptions(this.overview?.deployments ?? []);
      this.contractHash = this.deploymentOptions[0]?.contractHash ?? '';
      this.selectDeployment();
      await Promise.all(['neo3:testnet', 'neo3:mainnet'].map(async (network) => {
        try { this.relayStatuses[network] = (await firstValueFrom(this.api.getEventRelayStatus(network))).status === 'ok' ? 'ok' : 'degraded'; }
        catch { this.relayStatuses[network] = 'offline'; }
      }));

      // Listing subscriptions is wallet-authorized. Do not prompt for a signature
      // when this project has nothing deployed on the network the relay monitors.
      if (!this.deploymentOptions.length) {
        return;
      }

      await this.loadSubscriptions();
      await this.loadUsage(this.relayNetwork);
    } catch (error) {
      this.errorMessage = this.errors.format(error, 'Could not load webhook subscriptions.');
    }
  }

  private async loadSubscriptions(network = this.relayNetwork): Promise<void> {
    const subscriptions = await this.executeWebhookRequest(network, 'subscriptions.read', {}, (signature) =>
      this.api.getWebhookSubscriptions(this.projectId, network, signature));
    this.subscriptionsByNetwork[network] = subscriptions;
    if (network === this.relayNetwork) {
      this.subscriptions = subscriptions;
    }
  }

  private async loadUsage(network: string): Promise<void> {
    this.relayUsageByNetwork[network] = await this.executeWebhookRequest(network, 'subscriptions.read', {}, signature => this.api.getRelayUsage(this.projectId, network, signature));
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
      await this.executeWebhookRequest(subscription.network, 'subscriptions.update', {
        subscriptionId: subscription.id,
        subscription: request
      }, (signature) => this.api.updateWebhookSubscription(
        this.projectId, subscription.network, subscription.id, request, signature
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
    operation: WebhookManagementOperation,
    deliveryId: string | undefined,
    status: string,
    action: (signature?: WalletActionSignature) => Observable<WebhookDelivery>
  ): Promise<void> {
    this.errorMessage = '';
    this.formStatus = status;
    this.sendingFor = `${subscription.id}:${deliveryId ?? 'test'}`;
    try {
      const signedId = deliveryId ? `${subscription.id}:${deliveryId}` : subscription.id;
      await this.executeWebhookRequest(subscription.network, operation, { subscriptionId: signedId }, action);
      this.formStatus = deliveryId ? 'Failed event redelivered.' : 'Test event sent.';
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
    this.subscriptions = this.subscriptionsByNetwork[network] ?? [];
    if (!this.subscriptionsByNetwork[network] && this.deploymentOptions.some((item) => item.network === network)) {
      await this.loadSubscriptions(network);
      await this.loadUsage(network);
    }
  }

  private useRelay(network: string): void {
    this.relayNetwork = network;
    this.webhookNetwork = network;
  }

  private async executeWebhookRequest<T>(
    network: string,
    operation: WebhookManagementOperation,
    content: { subscriptionId?: string; subscription?: CreateWebhookSubscriptionRequest },
    request: (signature?: WalletActionSignature) => Observable<T>
  ): Promise<T> {
    if (this.api.hasWebhookSession(this.projectId, network)) {
      try {
        return await firstValueFrom(request());
      } catch (error) {
        if (!this.api.isWebhookSessionExpired(error)) {
          throw error;
        }

        this.api.clearWebhookSession(this.projectId, network);
      }
    }

    const requestHash = await this.api.getWebhookManagementRequestHash(this.projectId, operation, content);
    const signature = await this.wallet.signWebhookAdministration(this.projectId, operation, requestHash);
    return await firstValueFrom(request(signature));
  }

  private toDeploymentOptions(deployments: Deployment[]): DeploymentOption[] {
    return this.deploymentHistory
      .latestConfirmedByNetwork(deployments)
      .map((deployment) => {
        const artifact = this.overview?.artifacts.find((item) => item.id === deployment.artifactId);
        return artifact
          ? {
              label: `${deployment.network} - ${deployment.version}`,
              contractHash: deployment.contractHash ?? '',
              network: deployment.network,
              artifact
            }
          : null;
      })
      .filter((deployment): deployment is DeploymentOption => deployment !== null);
  }
}
