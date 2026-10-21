import { Component, OnDestroy, OnInit, inject } from '@angular/core';
import { DatePipe } from '@angular/common';
import { RelayBillingComponent } from '../../components/relay-billing/relay-billing.component';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { Subject, firstValueFrom, takeUntil, timeout } from 'rxjs';
import { Observable } from 'rxjs';
import {
  Artifact,
  EventRelayStatus,
  CreateWebhookSubscriptionRequest,
  Deployment,
  ProjectOverviewViewModel,
  WebhookDelivery,
  WebhookSubscription,
  RelayUsage,
  RelayReplayJob
} from '../../models/pusharoo.models';
import { DeploymentHistoryService } from '../../services/deployment-history.service';
import { ProjectOwnershipService } from '../../services/project-ownership.service';
import { EventRelayApiService } from '../../services/event-relay-api.service';
import { PusharooApiService } from '../../services/pusharoo-api.service';
import { ApiErrorFormatterService } from '../../services/api-error-formatter.service';
import { WalletService } from '../../services/wallet.service';
import { WalletAuthService } from '../../services/wallet-auth.service';
import { PageShellComponent } from '../page-shell/page-shell.component';
import { RelayHealthPanelComponent } from '../../components/relay-health-panel/relay-health-panel.component';
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
  imports: [
    RelayHealthPanelComponent,
    RelayBillingComponent,
    DatePipe,
    FormsModule,
    PageShellComponent,
    ProjectReleaseNavComponent,
    RouterLink
  ],
  templateUrl: './event-webhooks.component.html',
  styleUrl: './event-webhooks.component.scss'
})
export class EventWebhooksComponent implements OnInit, OnDestroy {
  recoveryOpen = false;
  recoveryContract = '';
  recoveryEvents: Record<string, boolean> = Object.create(null);
  recoveryFrom: number | null = null;
  recoveryTo: number | null = null;
  readonly recoveryJobsByNetwork: Record<string, RelayReplayJob[]> = {};
  readonly recoveryBusyByNetwork: Record<string, boolean> = {};
  readonly recoveryMessages: Record<string, string> = {};
  readonly recoveryErrors: Record<string, string> = {};

  get recoveryJobs(): RelayReplayJob[] {
    return this.recoveryJobsByNetwork[this.webhookNetwork] ?? [];
  }
  get recoveryBusy(): boolean {
    return this.recoveryBusyByNetwork[this.webhookNetwork] ?? false;
  }
  get recoveryEventOptions(): string[] {
    const manifestNames =
      this.deploymentOptions
        .find(x => x.contractHash === this.recoveryContract)
        ?.artifact.manifest.abi.events.map(x => x.name) ?? [];
    const subscriptions = this.subscriptions.filter(
      x =>
        x.isEnabled && x.network === this.webhookNetwork && x.contractHash === this.recoveryContract
    );
    return [
      ...new Set([
        ...manifestNames,
        ...subscriptions.flatMap(x => (x.eventName ? [x.eventName] : []))
      ])
    ]
      .filter(name => subscriptions.some(x => !x.eventName || x.eventName === name))
      .sort();
  }
  get recoveryRecipients(): WebhookSubscription[] {
    const names = this.recoveryEventOptions.filter(name => this.recoveryEvents[name]);
    return this.subscriptions.filter(
      x =>
        x.isEnabled &&
        x.network === this.webhookNetwork &&
        x.contractHash === this.recoveryContract &&
        (!x.eventName ? names.length > 0 : names.includes(x.eventName))
    );
  }

  selectRecoveryContract(): void {
    this.recoveryEvents = Object.create(null);
  }
  selectAllRecoveryEvents(): void {
    this.recoveryEvents = Object.fromEntries(this.recoveryEventOptions.map(name => [name, true]));
  }
  async toggleRecovery(): Promise<void> {
    this.recoveryOpen = !this.recoveryOpen;
    if (this.recoveryOpen) {
      if (!this.recoveryContract)
        this.recoveryContract = this.deploymentOptions[0]?.contractHash ?? '';
      await this.refreshRecovery();
    }
  }
  async refreshRecovery(): Promise<void> {
    const network = this.webhookNetwork;
    if (this.recoveryBusyByNetwork[network]) return;
    this.recoveryBusyByNetwork[network] = true;
    try {
      this.recoveryJobsByNetwork[network] = await this.executeWebhookRequest(() =>
        this.api.getProjectReplayJobs(this.projectId, network)
      );
      this.recoveryErrors[network] = '';
    } catch (error) {
      this.recoveryErrors[network] = this.errors.format(error, 'Could not load recovery progress.');
    } finally {
      this.recoveryBusyByNetwork[network] = false;
    }
  }
  async startRecovery(): Promise<void> {
    const network = this.webhookNetwork;
    if (this.recoveryBusyByNetwork[network]) return;
    this.recoveryErrors[network] = '';
    this.recoveryMessages[network] = '';
    const names = this.recoveryEventOptions.filter(name => this.recoveryEvents[name]);
    const from = this.recoveryFrom,
      to = this.recoveryTo;
    if (
      !this.recoveryContract ||
      !names.length ||
      !Number.isSafeInteger(from) ||
      !Number.isSafeInteger(to) ||
      from! < 0 ||
      to! < from!
    ) {
      this.recoveryErrors[network] =
        'Choose a contract, at least one event type, and an inclusive range of confirmed blocks.';
      return;
    }
    this.recoveryBusyByNetwork[network] = true;
    const contract = this.recoveryContract;
    try {
      const job = await this.executeWebhookRequest(() =>
        this.api.createProjectReplayJob(this.projectId, network, contract, names, from!, to!)
      );
      this.recoveryJobsByNetwork[network] = [
        job,
        ...(this.recoveryJobsByNetwork[network] ?? []).filter(x => x.id !== job.id)
      ];
      this.recoveryMessages[network] =
        'Recovery queued. The block range is scanned once for the selected events.';
    } catch (error) {
      this.recoveryErrors[network] = this.errors.format(error, 'Could not start recovery.');
    } finally {
      this.recoveryBusyByNetwork[network] = false;
    }
  }
  async controlRecovery(job: RelayReplayJob, action: 'pause' | 'resume' | 'cancel'): Promise<void> {
    const network = job.network;
    if (this.recoveryBusyByNetwork[network]) return;
    this.recoveryBusyByNetwork[network] = true;
    try {
      const updated = await this.executeWebhookRequest(() =>
        this.api.projectReplayAction(this.projectId, network, job.id, action)
      );
      this.recoveryJobsByNetwork[network] = (this.recoveryJobsByNetwork[network] ?? []).map(x =>
        x.id === job.id ? updated : x
      );
      this.recoveryErrors[network] = '';
    } catch (error) {
      this.recoveryErrors[network] = this.errors.format(error, 'Could not update recovery.');
    } finally {
      this.recoveryBusyByNetwork[network] = false;
    }
  }
  recoveryEventLabel(job: RelayReplayJob): string {
    return job.selectedEventNames?.join(', ') || job.eventName || 'All events';
  }
  overview: ProjectOverviewViewModel | null = null;
  subscriptions: WebhookSubscription[] = [];
  readonly subscriptionsByNetwork: Record<string, WebhookSubscription[]> = {};
  private allDeploymentOptions: DeploymentOption[] = [];
  eventOptions: string[] = [];
  relayNetwork = '';
  webhookNetwork = 'neo3:testnet';
  relayDetails: Partial<Record<string, EventRelayStatus>> = {};
  relayStatuses: Record<string, 'ok' | 'degraded' | 'offline'> = {};
  readonly relayUsageByNetwork: Record<string, RelayUsage> = {};
  connectingRelay = false;
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
  private readonly destroy$ = new Subject<void>();
  private destroyed = false;
  private deliveryRefreshTimer?: ReturnType<typeof setTimeout>;
  private readonly deliveryRefreshErrors: Record<string, string> = {};

  get deliveryRefreshError(): string {
    return this.deliveryRefreshErrors[this.webhookNetwork] ?? '';
  }

  get isSaving(): boolean {
    return this.savingByNetwork[this.webhookNetwork] ?? false;
  }
  set isSaving(value: boolean) {
    this.savingByNetwork[this.webhookNetwork] = value;
  }

  get deliveryHistory(): WebhookDelivery[] {
    return this.deliveryHistoryByNetwork[this.webhookNetwork] ?? [];
  }
  set deliveryHistory(value: WebhookDelivery[]) {
    this.deliveryHistoryByNetwork[this.webhookNetwork] = value;
  }

  get deliveryHistoryFor(): string {
    return this.deliveryHistoryForByNetwork[this.webhookNetwork] ?? '';
  }
  set deliveryHistoryFor(value: string) {
    this.deliveryHistoryForByNetwork[this.webhookNetwork] = value;
  }

  get loadingDeliveryHistoryFor(): string {
    return this.loadingDeliveryHistoryForByNetwork[this.webhookNetwork] ?? '';
  }
  set loadingDeliveryHistoryFor(value: string) {
    this.loadingDeliveryHistoryForByNetwork[this.webhookNetwork] = value;
  }

  get sendingFor(): string {
    return this.sendingForByNetwork[this.webhookNetwork] ?? '';
  }
  set sendingFor(value: string) {
    this.sendingForByNetwork[this.webhookNetwork] = value;
  }

  get formStatus(): string {
    return this.formStatusByNetwork[this.webhookNetwork] ?? '';
  }
  set formStatus(value: string) {
    this.formStatusByNetwork[this.webhookNetwork] = value;
  }

  get errorMessage(): string {
    return this.errorMessageByNetwork[this.webhookNetwork] ?? '';
  }
  set errorMessage(value: string) {
    this.errorMessageByNetwork[this.webhookNetwork] = value;
  }

  get pageTitle(): string {
    return 'Event Webhooks';
  }

  get selectedDeployment(): DeploymentOption | null {
    return (
      this.deploymentOptions.find(deployment => deployment.contractHash === this.contractHash) ??
      null
    );
  }

  get deploymentOptions(): DeploymentOption[] {
    return this.allDeploymentOptions.filter(
      deployment => deployment.network === this.webhookNetwork
    );
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
    this.scheduleDeliveryRefresh();
  }

  ngOnDestroy(): void {
    this.destroyed = true;
    clearTimeout(this.deliveryRefreshTimer);
    this.destroy$.next();
    this.destroy$.complete();
  }

  private scheduleDeliveryRefresh(): void {
    if (this.destroyed) return;
    this.deliveryRefreshTimer = setTimeout(() => {
      void this.refreshDeliveryStatus().finally(() => this.scheduleDeliveryRefresh());
    }, 5000);
  }

  private async refreshDeliveryStatus(): Promise<void> {
    if (
      this.destroyed ||
      !this.isRelayConnected ||
      !this.relayNetwork ||
      (typeof document !== 'undefined' && document.hidden)
    )
      return;
    const network = this.relayNetwork;
    const historyFor = this.deliveryHistoryForByNetwork[network];
    try {
      const [subscriptions, history] = await Promise.all([
        firstValueFrom(
          this.api
            .getWebhookSubscriptions(this.projectId, network)
            .pipe(timeout(10000), takeUntil(this.destroy$))
        ),
        historyFor
          ? firstValueFrom(
              this.api
                .getWebhookDeliveries(this.projectId, network, historyFor)
                .pipe(timeout(10000), takeUntil(this.destroy$))
            )
          : Promise.resolve(null)
      ]);
      if (this.destroyed || !this.isRelayConnected) return;
      this.subscriptionsByNetwork[network] = subscriptions;
      if (network === this.relayNetwork) this.subscriptions = subscriptions;
      if (history && this.deliveryHistoryForByNetwork[network] === historyFor) {
        this.deliveryHistoryByNetwork[network] = history;
      }
      this.deliveryRefreshErrors[network] = '';
    } catch {
      if (!this.destroyed) {
        this.deliveryRefreshErrors[network] =
          'Could not refresh delivery status. Retrying automatically.';
      }
    }
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
        payloadVersion: 2,
        projectId: this.projectId,
        secret: this.secret.trim() || null,
        headers: {},
        isEnabled: true
      };
      const network = this.selectedDeployment.network;
      if (this.editingSubscriptionId) {
        await this.executeWebhookRequest(() =>
          this.api.updateWebhookSubscription(
            this.projectId,
            network,
            this.editingSubscriptionId,
            subscriptionRequest
          )
        );
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
    return (
      this.subscriptions.find(subscription => subscription.id === subscriptionId)?.latestDelivery ??
      null
    );
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
    this.formStatus =
      'Editing webhook. Leave the signing secret empty to keep the existing secret.';
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
        this.api.deleteWebhookSubscription(this.projectId, subscription.network, subscription.id)
      );
      this.formStatus = 'Webhook deleted.';
      if (this.editingSubscriptionId === subscription.id) {
        this.resetForm();
      }
      await this.loadSubscriptions(subscription.network);
    } catch (error) {
      this.errorMessage = this.errors.format(error, 'Could not delete the webhook.');
    }
  }

  async toggleDeliveryHistory(subscription: WebhookSubscription): Promise<void> {
    if (this.deliveryHistoryForByNetwork[subscription.network] === subscription.id) {
      this.deliveryHistoryForByNetwork[subscription.network] = '';
      return;
    }
    await this.showDeliveryHistory(subscription);
  }

  async showDeliveryHistory(subscription: WebhookSubscription): Promise<void> {
    const network = subscription.network;
    this.errorMessage = '';
    this.loadingDeliveryHistoryForByNetwork[network] = subscription.id;
    try {
      const history = await this.executeWebhookRequest(() =>
        this.api.getWebhookDeliveries(this.projectId, subscription.network, subscription.id)
      );
      this.deliveryHistoryByNetwork[network] = history;
      this.deliveryHistoryForByNetwork[network] = subscription.id;
    } catch (error) {
      this.deliveryHistoryByNetwork[network] = [];
      this.deliveryHistoryForByNetwork[network] = '';
      this.errorMessageByNetwork[network] = this.errors.format(
        error,
        'Could not load webhook delivery history.'
      );
    } finally {
      this.loadingDeliveryHistoryForByNetwork[network] = '';
    }
  }

  async sendTest(subscription: WebhookSubscription): Promise<void> {
    await this.runDeliveryAction(subscription, undefined, 'Sending test event...', () =>
      this.api.sendWebhookTest(this.projectId, subscription.network, subscription.id)
    );
  }

  async redeliver(subscription: WebhookSubscription, delivery: WebhookDelivery): Promise<void> {
    await this.runDeliveryAction(subscription, delivery.id, 'Redelivering event...', () =>
      this.api.redeliverWebhook(this.projectId, subscription.network, subscription.id, delivery.id)
    );
  }

  async connectRelay(): Promise<void> {
    this.errorMessage = '';
    this.formStatus = '';
    if (!this.canManageProject) {
      this.errorMessage = 'Only the project owner can connect to Relay.';
      return;
    }
    this.connectingRelay = true;
    try {
      await this.auth.ensureAuthenticated();
      await this.loadUsage(this.webhookNetwork);
      if (this.deploymentOptions.length) await this.loadSubscriptions(this.webhookNetwork);
    } catch (error) {
      this.errorMessage = this.errors.format(error, 'Could not open the Relay workspace.');
    } finally {
      this.connectingRelay = false;
    }
  }

  updateRelayUsage(usage: Record<string, RelayUsage>): void {
    Object.assign(this.relayUsageByNetwork, usage);
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
    void this.loadRelayStatuses();
    const sessionReady = this.auth.restore();
    try {
      const cachedOverview = this.workspace?.getFreshOverview(this.projectId);
      this.overview =
        cachedOverview?.project.id === this.projectId
          ? cachedOverview
          : await firstValueFrom(this.projectApi.getProjectOverview(this.projectId));
      if (this.workspace && this.overview) {
        this.workspace.overview = this.overview;
      }
      this.allDeploymentOptions = this.toDeploymentOptions(this.overview?.deployments ?? []);
      this.contractHash = this.deploymentOptions[0]?.contractHash ?? '';
      this.selectDeployment();
      await sessionReady;

      if (!this.deploymentOptions.length) {
        return;
      }

      if (!this.isWalletConnected || !this.auth.canUseSession() || !this.canManageProject) {
        return;
      }

      const networks = [...new Set([this.relayNetwork, 'neo3:mainnet'])];
      await Promise.all([
        this.loadSubscriptions(),
        ...networks.map(network => this.loadUsage(network))
      ]);
    } catch (error) {
      this.errorMessage = this.errors.format(error, 'Could not load webhook subscriptions.');
    }
  }

  async loadRelayStatuses(): Promise<void> {
    await Promise.all(
      ['neo3:testnet', 'neo3:mainnet'].map(async network => {
        try {
          const detail = await firstValueFrom(this.api.getEventRelayStatus(network));
          this.relayDetails[network] = detail;
          this.relayStatuses[network] = detail.status === 'ok' ? 'ok' : 'degraded';
        } catch (error: unknown) {
          const failure = error as { status?: number; error?: EventRelayStatus };
          this.relayDetails[network] =
            failure.error?.network === network ? failure.error : undefined;
          this.relayStatuses[network] = failure.status === 503 ? 'degraded' : 'offline';
        }
      })
    );
  }

  private async loadSubscriptions(network = this.relayNetwork): Promise<void> {
    const subscriptions = await this.executeWebhookRequest(() =>
      this.api.getWebhookSubscriptions(this.projectId, network)
    );
    this.subscriptionsByNetwork[network] = subscriptions;
    if (network === this.relayNetwork) {
      this.subscriptions = subscriptions;
    }
  }

  private async loadUsage(network: string): Promise<void> {
    this.relayUsageByNetwork[network] = await this.executeWebhookRequest(() =>
      this.api.getRelayUsage(this.projectId, network)
    );
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
      payloadVersion: 2,
      secret: null,
      headers: subscription.headers,
      isEnabled: subscription.isEnabled,
      ...changes
    };
    try {
      await this.executeWebhookRequest(() =>
        this.api.updateWebhookSubscription(
          this.projectId,
          subscription.network,
          subscription.id,
          request
        )
      );
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
      this.errorMessage = this.errors.format(
        error,
        deliveryId ? 'Could not redeliver the event.' : 'Could not send a test event.'
      );
    } finally {
      this.sendingFor = '';
    }
  }

  selectDeployment(): void {
    if (this.selectedDeployment) {
      this.useRelay(this.selectedDeployment.network);
    }
    this.eventOptions =
      this.selectedDeployment?.artifact.manifest.abi.events.map(event => event.name) ?? [];
    this.eventName = this.eventOptions[0] ?? '';
  }

  async selectWebhookNetwork(network: string): Promise<void> {
    this.recoveryOpen = false;
    this.recoveryContract = '';
    this.recoveryEvents = Object.create(null);
    this.recoveryFrom = this.recoveryTo = null;
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
      .map(deployment => {
        const artifact = this.overview?.artifacts.find(item => item.id === deployment.artifactId);
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
