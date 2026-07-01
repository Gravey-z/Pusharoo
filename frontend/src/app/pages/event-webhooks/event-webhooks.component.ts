import { Component, OnInit } from '@angular/core';
import { DatePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { firstValueFrom, forkJoin, of } from 'rxjs';
import {
  Deployment,
  ProjectOverviewViewModel,
  WebhookDelivery,
  WebhookSubscription
} from '../../models/pusharoo.models';
import { DeploymentHistoryService } from '../../services/deployment-history.service';
import { PusharooApiService } from '../../services/pusharoo-api.service';
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
  deliveriesBySubscription: Record<string, WebhookDelivery[]> = {};
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
    private readonly deploymentHistory: DeploymentHistoryService
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
      await firstValueFrom(this.api.createWebhookSubscription({
        name: this.name.trim(),
        contractHash: this.contractHash,
        eventName: this.eventName || null,
        webhookUrl: this.webhookUrl.trim(),
        projectId: this.projectId,
        secret: this.secret.trim() || null,
        headers: {},
        isEnabled: true
      }));

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
    return this.deliveriesBySubscription[subscriptionId]?.[0] ?? null;
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
    this.overview = await firstValueFrom(this.api.getProjectOverview(this.projectId));
    this.deploymentOptions = this.toDeploymentOptions(this.overview?.deployments ?? []);
    this.eventOptions = this.overview?.latestArtifact?.manifest.abi.events.map((event) => event.name) ?? [];
    this.contractHash = this.deploymentOptions[0]?.contractHash ?? '';
    this.eventName = this.eventOptions[0] ?? '';
    await this.loadSubscriptions();
  }

  private async loadSubscriptions(): Promise<void> {
    this.subscriptions = await firstValueFrom(this.api.getWebhookSubscriptions(this.projectId));

    if (!this.subscriptions.length) {
      this.deliveriesBySubscription = {};
      return;
    }

    this.deliveriesBySubscription = await firstValueFrom(
      forkJoin(
        Object.fromEntries(
          this.subscriptions.map((subscription) => [
            subscription.id,
            this.api.getWebhookDeliveries(subscription.id)
          ])
        )
      )
    );
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
