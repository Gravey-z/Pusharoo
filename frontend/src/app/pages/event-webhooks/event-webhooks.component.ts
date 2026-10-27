import { Component, OnDestroy, OnInit, inject } from '@angular/core';
import { DatePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { Subject, firstValueFrom, takeUntil, timeout, Observable, forkJoin } from 'rxjs';
import { workspaceRoute } from '../../services/workspace-route';
import {
  WebhookContractTargetsService,
  WebhookDeploymentOption
} from '../../services/webhook-contract-targets.service';
import {
  ProjectContract,
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

@Component({
  selector: 'app-event-webhooks',
  imports: [
    RelayHealthPanelComponent,
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
        .find(x => this.contractTargets.sameHash(x.contractHash, this.recoveryContract))
        ?.artifact?.manifest.abi.events.map(x => x.name) ?? [];
    const subscriptions = this.subscriptions.filter(
      x =>
        x.isEnabled &&
        x.network === this.webhookNetwork &&
        this.contractTargets.sameHash(x.contractHash, this.recoveryContract)
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
        this.contractTargets.sameHash(x.contractHash, this.recoveryContract) &&
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
    const generation = this.contextGeneration;
    const network = this.webhookNetwork;
    if (this.recoveryBusyByNetwork[network]) return;
    this.recoveryBusyByNetwork[network] = true;
    try {
      this.recoveryJobsByNetwork[network] = await this.executeWebhookRequest(() =>
        this.api.getProjectReplayJobs(this.projectId, network)
      );
      this.recoveryErrors[network] = '';
    } catch (error) {
      if (!this.isCurrent(generation)) return;
      this.recoveryErrors[network] = this.errors.format(error, 'Could not load recovery progress.');
    } finally {
      if (!this.isCurrent(generation)) return;
      this.recoveryBusyByNetwork[network] = false;
    }
  }
  async startRecovery(): Promise<void> {
    const generation = this.contextGeneration;
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
      if (!this.isCurrent(generation)) return;
      this.recoveryErrors[network] = this.errors.format(error, 'Could not start recovery.');
    } finally {
      if (!this.isCurrent(generation)) return;
      this.recoveryBusyByNetwork[network] = false;
    }
  }
  async controlRecovery(job: RelayReplayJob, action: 'pause' | 'resume' | 'cancel'): Promise<void> {
    const generation = this.contextGeneration;
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
      if (!this.isCurrent(generation)) return;
      this.recoveryErrors[network] = this.errors.format(error, 'Could not update recovery.');
    } finally {
      if (!this.isCurrent(generation)) return;
      this.recoveryBusyByNetwork[network] = false;
    }
  }
  recoveryEventLabel(job: RelayReplayJob): string {
    return job.selectedEventNames?.join(', ') || job.eventName || 'All events';
  }
  overview: ProjectOverviewViewModel | null = null;
  contracts: ProjectContract[] = [];
  contractFilter = '';
  selectedDeploymentKey = '';
  private readonly contractTargets = inject(WebhookContractTargetsService);

  get filteredSubscriptions(): WebhookSubscription[] {
    if (!this.contractFilter) return this.subscriptions;
    return this.subscriptions.filter(subscription => {
      const contract = this.subscriptionContract(subscription);
      return this.contractFilter === 'unlinked' ? !contract : contract?.id === this.contractFilter;
    });
  }

  subscriptionContract(subscription: WebhookSubscription): ProjectContract | null {
    return this.contractTargets.association(
      subscription,
      this.overview?.deployments ?? [],
      this.contracts
    );
  }

  subscriptions: WebhookSubscription[] = [];
  readonly subscriptionsByNetwork: Record<string, WebhookSubscription[]> = {};
  private allDeploymentOptions: WebhookDeploymentOption[] = [];
  eventOptions: string[] = [];
  relayNetwork = 'neo3:testnet';
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
  private editingSubscription: WebhookSubscription | null = null;
  private readonly deliveryHistoryByNetwork: Record<string, WebhookDelivery[]> = {};
  private readonly deliveryHistoryForByNetwork: Record<string, string> = {};
  private readonly loadingDeliveryHistoryForByNetwork: Record<string, string> = {};
  private readonly sendingForByNetwork: Record<string, string> = {};
  private readonly formStatusByNetwork: Record<string, string> = {};
  private readonly errorMessageByNetwork: Record<string, string> = {};
  private readonly workspace = inject(ProjectWorkspaceContextService, { optional: true });
  private readonly destroy$ = new Subject<void>();
  private destroyed = false;
  private contextGeneration = 0;
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

  get selectedDeployment(): WebhookDeploymentOption | null {
    return (
      this.deploymentOptions.find(deployment => deployment.key === this.selectedDeploymentKey) ??
      null
    );
  }

  get deploymentOptions(): WebhookDeploymentOption[] {
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
  ) {}

  ngOnInit(): void {
    workspaceRoute(this.route)
      .pipe(takeUntil(this.destroy$))
      .subscribe(scope => {
        this.contextGeneration++;
        this.projectId = scope.projectId;
        this.overview = null;
        this.contracts = [];
        this.allDeploymentOptions = [];
        this.subscriptions = [];
        this.contractFilter = '';
        this.selectedDeploymentKey = '';
        this.contractHash = '';
        this.eventName = '';
        this.eventOptions = [];
        this.resetForm();
        this.recoveryOpen = false;
        this.recoveryContract = '';
        this.recoveryEvents = Object.create(null);
        this.recoveryFrom = this.recoveryTo = null;
        for (const state of [
          this.subscriptionsByNetwork,
          this.relayUsageByNetwork,
          this.savingByNetwork,
          this.deliveryHistoryByNetwork,
          this.deliveryHistoryForByNetwork,
          this.loadingDeliveryHistoryForByNetwork,
          this.sendingForByNetwork,
          this.formStatusByNetwork,
          this.errorMessageByNetwork,
          this.deliveryRefreshErrors,
          this.recoveryJobsByNetwork,
          this.recoveryBusyByNetwork,
          this.recoveryMessages,
          this.recoveryErrors
        ]) {
          for (const key of Object.keys(state)) delete state[key];
        }
        void this.load();
      });
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
    const generation = this.contextGeneration;
    if (
      this.destroyed ||
      !this.isRelayConnected ||
      !this.relayNetwork ||
      !this.subscriptionsByNetwork[this.relayNetwork]?.length ||
      (typeof document !== 'undefined' && document.hidden)
    )
      return;
    const network = this.relayNetwork;
    const historyFor = this.deliveryHistoryForByNetwork[network];
    try {
      const [subscriptions, history, usage] = await Promise.all([
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
          : Promise.resolve(null),
        firstValueFrom(
          this.api
            .getRelayUsage(this.projectId, network)
            .pipe(timeout(10000), takeUntil(this.destroy$))
        )
      ]);
      if (!this.isCurrent(generation) || !this.isRelayConnected) return;
      this.subscriptionsByNetwork[network] = subscriptions;
      if (network === this.relayNetwork) this.subscriptions = subscriptions;
      this.relayUsageByNetwork[network] = usage;
      if (history && this.deliveryHistoryForByNetwork[network] === historyFor) {
        this.deliveryHistoryByNetwork[network] = history;
      }
      this.deliveryRefreshErrors[network] = '';
    } catch {
      if (!this.isCurrent(generation)) return;
      if (!this.destroyed) {
        this.deliveryRefreshErrors[network] =
          'Could not refresh delivery status. Retrying automatically.';
      }
    }
  }

  async save(): Promise<void> {
    const generation = this.contextGeneration;
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

    if (!this.selectedDeployment && !this.editingSubscription) {
      this.errorMessage = 'Choose a deployment monitored by Pusharoo Relay.';
      return;
    }

    if (!this.webhookUrl.trim()) {
      this.errorMessage = 'Enter the endpoint URL.';
      return;
    }

    const network = this.editingSubscription?.network ?? this.selectedDeployment!.network;
    const editingId = this.editingSubscriptionId;
    this.savingByNetwork[network] = true;

    try {
      const subscriptionRequest = {
        name: this.name.trim(),
        contractHash: this.editingSubscription?.contractHash ?? this.contractHash,
        network: this.editingSubscription?.network ?? this.selectedDeployment!.network,
        eventName: this.eventName || null,
        webhookUrl: this.webhookUrl.trim(),
        payloadVersion: 2,
        projectId: this.projectId,
        secret: this.secret.trim() || null,
        headers: this.editingSubscription?.headers ?? {},
        isEnabled: this.editingSubscription?.isEnabled ?? true
      };
      if (this.editingSubscriptionId) {
        await this.executeWebhookRequest(() =>
          this.api.updateWebhookSubscription(
            this.projectId,
            network,
            editingId,
            subscriptionRequest
          )
        );
      } else {
        await this.executeWebhookRequest(() =>
          this.api.createWebhookSubscription(this.projectId, network, subscriptionRequest)
        );
      }

      this.formStatusByNetwork[network] = editingId ? 'Webhook updated.' : 'Webhook created.';
      if (network === this.webhookNetwork) this.resetForm();
      await this.loadSubscriptions(network);
    } catch (error) {
      if (!this.isCurrent(generation)) return;
      this.errorMessageByNetwork[network] = this.errors.format(
        error,
        'Could not create the webhook subscription.'
      );
    } finally {
      if (!this.isCurrent(generation)) return;
      this.savingByNetwork[network] = false;
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
    const candidates = this.deploymentOptions.filter(item =>
      this.contractTargets.sameHash(item.contractHash, subscription.contractHash)
    );
    this.selectedDeploymentKey = candidates.length === 1 ? candidates[0].key : '';
    this.editingSubscription = subscription;
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
    const generation = this.contextGeneration;
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
      if (!this.isCurrent(generation)) return;
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
    const generation = this.contextGeneration;
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
      if (!this.isCurrent(generation)) return;
      this.deliveryHistoryByNetwork[network] = [];
      this.deliveryHistoryForByNetwork[network] = '';
      this.errorMessageByNetwork[network] = this.errors.format(
        error,
        'Could not load webhook delivery history.'
      );
    } finally {
      if (!this.isCurrent(generation)) return;
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
    const generation = this.contextGeneration;
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
      await this.loadSubscriptions(this.webhookNetwork);
    } catch (error) {
      if (!this.isCurrent(generation)) return;
      this.errorMessage = this.errors.format(error, 'Could not open the Relay workspace.');
    } finally {
      if (!this.isCurrent(generation)) return;
      this.connectingRelay = false;
    }
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
    this.useRelay(this.webhookNetwork);
    const generation = this.contextGeneration;
    void this.loadRelayStatuses();
    const sessionReady = this.auth.restore();
    try {
      const { overview, summary } = await firstValueFrom(
        forkJoin({
          overview: this.projectApi.getProjectOverview(this.projectId),
          summary: this.projectApi.getProjectSummary(this.projectId)
        })
      );
      if (!this.isCurrent(generation)) return;
      this.overview = overview;
      this.contracts = summary.contracts.map(item => item.contract);
      if (this.workspace) this.workspace.overview = overview;
      this.allDeploymentOptions = this.toDeploymentOptions(this.overview?.deployments ?? []);
      this.selectedDeploymentKey = this.deploymentOptions[0]?.key ?? '';
      this.contractHash = this.deploymentOptions[0]?.contractHash ?? '';
      this.selectDeployment();
      await sessionReady;
      if (!this.isCurrent(generation)) return;

      if (!this.isWalletConnected || !this.auth.canUseSession() || !this.canManageProject) {
        return;
      }

      const networks = [...new Set([this.relayNetwork, 'neo3:mainnet'])];
      await Promise.all([
        this.loadSubscriptions(),
        ...networks.map(network => this.loadUsage(network))
      ]);
    } catch (error) {
      if (!this.isCurrent(generation)) return;
      this.errorMessage = this.errors.format(error, 'Could not load webhook subscriptions.');
    }
  }

  async loadRelayStatuses(): Promise<void> {
    const generation = this.contextGeneration;
    await Promise.all(
      ['neo3:testnet', 'neo3:mainnet'].map(async network => {
        try {
          const detail = await firstValueFrom(this.api.getEventRelayStatus(network));
          if (!this.isCurrent(generation)) return;
          this.relayDetails[network] = detail;
          this.relayStatuses[network] = detail.status === 'ok' ? 'ok' : 'degraded';
        } catch (error: unknown) {
          if (!this.isCurrent(generation)) return;
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
    const generation = this.contextGeneration;
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
      if (!this.isCurrent(generation)) return;
      this.errorMessage = this.errors.format(error, 'Could not update the webhook.');
    }
  }

  private resetForm(): void {
    this.editingSubscriptionId = '';
    this.editingSubscription = null;
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
    const generation = this.contextGeneration;
    this.errorMessage = '';
    this.formStatus = status;
    this.sendingFor = `${subscription.id}:${deliveryId ?? 'test'}`;
    try {
      await this.executeWebhookRequest(action);
      this.formStatus = deliveryId ? 'Failed event redelivered.' : 'Test event queued.';
      await this.loadSubscriptions(subscription.network);
      await this.showDeliveryHistory(subscription);
    } catch (error) {
      if (!this.isCurrent(generation)) return;
      this.errorMessage = this.errors.format(
        error,
        deliveryId ? 'Could not redeliver the event.' : 'Could not send a test event.'
      );
    } finally {
      if (!this.isCurrent(generation)) return;
      this.sendingFor = '';
    }
  }

  selectDeployment(): void {
    if (!this.editingSubscription) this.contractHash = this.selectedDeployment?.contractHash ?? '';
    if (this.selectedDeployment) {
      this.useRelay(this.selectedDeployment.network);
    }
    this.eventOptions =
      this.selectedDeployment?.artifact?.manifest.abi.events.map(event => event.name) ?? [];
    this.eventName = this.eventOptions[0] ?? '';
  }

  async selectWebhookNetwork(network: string): Promise<void> {
    this.recoveryOpen = false;
    this.recoveryContract = '';
    this.recoveryEvents = Object.create(null);
    this.recoveryFrom = this.recoveryTo = null;
    this.webhookNetwork = network;
    this.useRelay(network);
    this.selectedDeploymentKey = this.deploymentOptions[0]?.key ?? '';
    this.contractHash = this.deploymentOptions[0]?.contractHash ?? '';
    this.eventOptions = [];
    this.eventName = '';
    this.editingSubscriptionId = '';
    this.editingSubscription = null;
    this.secret = '';
    if (this.contractHash) {
      this.selectDeployment();
    }
    this.subscriptions = this.subscriptionsByNetwork[network] ?? [];
    if (!this.isRelayConnected) {
      return;
    }
    if (!this.subscriptionsByNetwork[network]) {
      await this.loadSubscriptions(network);
      await this.loadUsage(network);
    }
  }

  private useRelay(network: string): void {
    this.relayNetwork = network;
    this.webhookNetwork = network;
  }

  private isCurrent(generation: number): boolean {
    return !this.destroyed && generation === this.contextGeneration;
  }

  private async executeWebhookRequest<T>(request: () => Observable<T>): Promise<T> {
    const generation = this.contextGeneration;
    await this.auth.ensureAuthenticated();
    if (!this.isCurrent(generation)) throw new Error('The selected project changed.');
    const response = await firstValueFrom(request().pipe(takeUntil(this.destroy$)));
    if (!this.isCurrent(generation)) throw new Error('The selected project changed.');
    return response;
  }

  private toDeploymentOptions(deployments: Deployment[]): WebhookDeploymentOption[] {
    return this.overview
      ? this.contractTargets.options({ ...this.overview, deployments }, this.contracts)
      : [];
  }
}
