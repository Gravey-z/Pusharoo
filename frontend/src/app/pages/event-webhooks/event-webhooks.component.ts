import { Component, OnInit } from '@angular/core';
import { DatePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import {
  Deployment,
  ProjectOverviewViewModel,
  WebhookDelivery,
  WebhookSubscription
} from '../../models/pusharoo.models';
import { DeploymentHistoryService } from '../../services/deployment-history.service';
import { PusharooApiService } from '../../services/pusharoo-api.service';
import { WalletService } from '../../services/wallet.service';
import { PageShellComponent } from '../page-shell/page-shell.component';

interface DeploymentOption {
  label: string;
  contractHash: string;
  network: string;
}

@Component({
  selector: 'app-event-webhooks',
  imports: [DatePipe, FormsModule, PageShellComponent, RouterLink],
  templateUrl: './event-webhooks.component.html',
  styleUrl: './event-webhooks.component.scss'
})
export class EventWebhooksComponent implements OnInit {
  overview: ProjectOverviewViewModel | null = null;
  subscriptions: WebhookSubscription[] = [];
  deploymentOptions: DeploymentOption[] = [];
  eventOptions: string[] = [];
  projectId = '';
  name = '';
  contractHash = '';
  eventName = '';
  webhookUrl = '';
  secret = '';
  isSaving = false;
  formStatus = '';
  errorMessage = '';

  get pageTitle(): string {
    return this.overview ? `${this.overview.project.name}: Event Webhooks` : 'Event Webhooks';
  }

  constructor(
    private readonly route: ActivatedRoute,
    private readonly api: PusharooApiService,
    private readonly deploymentHistory: DeploymentHistoryService,
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

    if (!this.webhookUrl.trim()) {
      this.errorMessage = 'Enter the endpoint URL.';
      return;
    }

    this.isSaving = true;

    try {
      const subscriptionRequest = {
        name: this.name.trim(),
        contractHash: this.contractHash,
        eventName: this.eventName || null,
        webhookUrl: this.webhookUrl.trim(),
        projectId: this.projectId,
        secret: this.secret.trim() || null,
        headers: {},
        isEnabled: true
      };
      const requestHash = await this.api.getWebhookManagementRequestHash(
        this.projectId,
        'subscriptions.create',
        { subscription: subscriptionRequest }
      );
      const signature = await this.wallet.signWebhookAdministration(
        this.projectId,
        'subscriptions.create',
        requestHash
      );
      await firstValueFrom(this.api.createWebhookSubscription(
        this.projectId,
        subscriptionRequest,
        signature
      ));

      this.formStatus = 'Webhook created.';
      this.name = '';
      this.webhookUrl = '';
      this.secret = '';
      await this.loadSubscriptions();
    } catch {
      this.errorMessage = 'Could not create the webhook subscription.';
    } finally {
      this.isSaving = false;
    }
  }

  latestDelivery(subscriptionId: string): WebhookDelivery | null {
    return this.subscriptions.find((subscription) => subscription.id === subscriptionId)?.latestDelivery ?? null;
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
      this.eventOptions = this.overview?.latestArtifact?.manifest.abi.events.map((event) => event.name) ?? [];
      this.contractHash = this.deploymentOptions[0]?.contractHash ?? '';
      this.eventName = this.eventOptions[0] ?? '';
      await this.loadSubscriptions();
    } catch (error) {
      this.errorMessage = this.getErrorMessage(error, 'Could not load webhook subscriptions.');
    }
  }

  private async loadSubscriptions(): Promise<void> {
    const requestHash = await this.api.getWebhookManagementRequestHash(
      this.projectId,
      'subscriptions.read'
    );
    const signature = await this.wallet.signWebhookAdministration(
      this.projectId,
      'subscriptions.read',
      requestHash
    );
    this.subscriptions = await firstValueFrom(
      this.api.getWebhookSubscriptions(this.projectId, signature)
    );
  }

  private getErrorMessage(error: unknown, fallback: string): string {
    return error instanceof Error && error.message ? error.message : fallback;
  }

  private toDeploymentOptions(deployments: Deployment[]): DeploymentOption[] {
    return this.deploymentHistory
      .latestByNetwork(deployments)
      .filter((deployment) => deployment.contractHash)
      .map((deployment) => ({
        label: `${deployment.network} - ${deployment.version}`,
        contractHash: deployment.contractHash ?? '',
        network: deployment.network
      }));
  }
}
