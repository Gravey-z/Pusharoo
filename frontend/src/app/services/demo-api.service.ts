import { Injectable } from '@angular/core';
import {
  Artifact,
  CreateDeploymentRequest,
  CreateWebhookSubscriptionRequest,
  Deployment,
  Project,
  ProjectListItem,
  ProjectOverviewViewModel,
  RecoverDeploymentRequest,
  RelayPayment,
  RelayPaymentHistory,
  RelayPaymentIntent,
  RelayUsage,
  StartDeploymentAttemptRequest,
  WebhookDelivery,
  WebhookSubscription
} from '../models/pusharoo.models';
import {
  DEMO_WALLET_ADDRESS,
  demoArtifacts,
  demoDeployments,
  demoProjects,
  demoRelayPaymentHistory,
  demoRelayUsage,
  demoWebhookDeliveries,
  demoWebhookSubscriptions
} from './demo-data';

@Injectable({ providedIn: 'root' })
export class DemoApiService {
  private readonly projects = this.copy(demoProjects);
  private readonly artifacts = this.copy(demoArtifacts);
  private readonly deployments = this.copy(demoDeployments);
  private readonly subscriptions = this.copy(demoWebhookSubscriptions);
  private readonly deliveries = this.copy(demoWebhookDeliveries);
  private readonly usage = this.copy(demoRelayUsage);
  private readonly paymentHistory = this.copy(demoRelayPaymentHistory);

  getProjectCards(): ProjectListItem[] {
    return this.projects
      .map((project) => {
        const artifacts = this.artifactsFor(project.id);
        const deployments = this.deploymentsFor(project.id);
        const latestArtifact = artifacts[0] ?? null;
        const confirmed = deployments.filter((deployment) => deployment.status === 'confirmed' && deployment.contractHash);

        return {
          project: this.copy(project),
          latestArtifact: latestArtifact
            ? { version: latestArtifact.version, createdAt: latestArtifact.createdAt }
            : null,
          deploymentNetworks: [...new Set(confirmed.map((deployment) => deployment.network))],
          deployed: confirmed.length > 0
        };
      })
      .sort((left, right) => Date.parse(right.project.createdAt) - Date.parse(left.project.createdAt));
  }

  getProjectOverview(projectId: string): ProjectOverviewViewModel {
    const project = this.requireProject(projectId);
    const artifacts = this.artifactsFor(projectId);
    const deployments = this.deploymentsFor(projectId);
    const confirmed = deployments.filter((deployment) => deployment.status === 'confirmed' && deployment.contractHash);

    return this.copy({
      project,
      artifacts,
      latestArtifact: artifacts[0] ?? null,
      deployments,
      latestDeployment: confirmed[0] ?? null,
      deployed: confirmed.length > 0
    });
  }

  getArtifact(artifactId: string): Artifact {
    const artifact = this.artifacts.find((item) => item.id === artifactId);
    if (!artifact) {
      throw new Error('The demo artifact was not found. Reload to reset the demo.');
    }

    return this.copy(artifact);
  }

  getArtifactNefHex(artifactId: string): string {
    this.getArtifact(artifactId);
    return `4e454633${'00'.repeat(92)}`;
  }

  createProject(name: string, description: string): Project {
    const now = new Date().toISOString();
    const project: Project = {
      id: this.id('project'),
      name: name.trim(),
      description: description.trim() || null,
      createdByWalletAddress: DEMO_WALLET_ADDRESS,
      creatorNetwork: 'neo3:testnet',
      createdAt: now
    };
    this.projects.unshift(project);
    return this.copy(project);
  }

  deleteProject(projectId: string): void {
    this.removeWhere(this.projects, (item) => item.id === projectId);
    this.removeWhere(this.artifacts, (item) => item.projectId === projectId);
    this.removeWhere(this.deployments, (item) => item.projectId === projectId);
    const subscriptionIds = this.subscriptions
      .filter((item) => item.projectId === projectId)
      .map((item) => item.id);
    this.removeWhere(this.subscriptions, (item) => item.projectId === projectId);
    this.removeWhere(this.deliveries, (item) => subscriptionIds.includes(item.subscriptionId));
  }

  async uploadArtifact(
    projectId: string,
    version: string,
    notes: string,
    nefFile: File,
    manifestFile: File
  ): Promise<Artifact> {
    this.requireProject(projectId);
    const manifest = JSON.parse(await manifestFile.text()) as Artifact['manifest'];
    const methods = manifest.abi?.methods ?? [];
    const events = manifest.abi?.events ?? [];
    const permissions = manifest.permissions ?? [];
    const artifact: Artifact = {
      id: this.id('artifact'),
      projectId,
      version: version.trim(),
      notes: notes.trim() || null,
      contractName: manifest.name || nefFile.name.replace(/\.nef$/i, ''),
      nefFileName: nefFile.name,
      nefSize: nefFile.size,
      manifest,
      summary: {
        methodCount: methods.length,
        eventCount: events.length,
        permissionCount: permissions.length,
        supportedStandards: manifest.supportedstandards ?? []
      },
      warnings: [],
      createdAt: new Date().toISOString()
    };
    this.artifacts.unshift(artifact);
    return this.copy(artifact);
  }

  artifactsFor(projectId: string): Artifact[] {
    return this.copy(this.artifacts
      .filter((artifact) => artifact.projectId === projectId)
      .sort((left, right) => Date.parse(right.createdAt) - Date.parse(left.createdAt)));
  }

  deploymentsFor(projectId: string): Deployment[] {
    return this.copy(this.deployments
      .filter((deployment) => deployment.projectId === projectId)
      .sort((left, right) => Date.parse(right.createdAt) - Date.parse(left.createdAt)));
  }

  createDeployment(projectId: string, request: CreateDeploymentRequest): Deployment {
    const artifact = this.getArtifact(request.artifactId);
    const now = new Date().toISOString();
    const deployment: Deployment = {
      id: this.id('deployment'),
      projectId,
      artifactId: artifact.id,
      version: artifact.version,
      network: request.network,
      contractHash: request.contractHash ?? this.hash(20),
      transactionId: request.transactionId ?? this.hash(32),
      deployedBy: request.deployedBy,
      notes: request.notes,
      createdAt: now,
      updatedAt: now,
      operation: this.currentContract(projectId, request.network) ? 'update' : 'deploy',
      status: 'confirmed'
    };
    this.deployments.unshift(deployment);
    return this.copy(deployment);
  }

  startDeploymentAttempt(projectId: string, request: StartDeploymentAttemptRequest): Deployment {
    const artifact = this.getArtifact(request.artifactId);
    const now = new Date().toISOString();
    const attempt: Deployment = {
      id: this.id('attempt'),
      projectId,
      artifactId: artifact.id,
      version: artifact.version,
      network: request.network,
      deployedBy: request.deployedBy,
      notes: request.notes,
      createdAt: now,
      updatedAt: now,
      operation: this.currentContract(projectId, request.network) ? 'update' : 'deploy',
      status: 'awaiting_wallet'
    };
    this.deployments.unshift(attempt);
    return this.copy(attempt);
  }

  markDeploymentSubmitted(deploymentId: string, transactionId: string): Deployment {
    return this.updateDeployment(deploymentId, {
      transactionId,
      status: 'submitted',
      updatedAt: new Date().toISOString()
    });
  }

  confirmDeploymentAttempt(deploymentId: string): Deployment {
    const attempt = this.requireDeployment(deploymentId);
    const existingContract = this.currentContract(attempt.projectId, attempt.network, attempt.id);
    return this.updateDeployment(deploymentId, {
      contractHash: existingContract ?? this.hash(20),
      status: 'confirmed',
      failureStage: null,
      failureReason: null,
      updatedAt: new Date().toISOString()
    });
  }

  markDeploymentFailed(deploymentId: string, stage: string, reason: string): Deployment {
    return this.updateDeployment(deploymentId, {
      status: 'failed',
      failureStage: stage,
      failureReason: reason,
      updatedAt: new Date().toISOString()
    });
  }

  recoverDeployment(projectId: string, request: RecoverDeploymentRequest): Deployment {
    return this.createDeployment(projectId, {
      ...request,
      contractHash: this.currentContract(projectId, request.network) ?? this.hash(20)
    });
  }

  getSubscriptions(projectId: string, network: string): WebhookSubscription[] {
    return this.copy(this.subscriptions
      .filter((subscription) => subscription.projectId === projectId && subscription.network === network)
      .sort((left, right) => Date.parse(right.createdAt) - Date.parse(left.createdAt)));
  }

  createSubscription(
    projectId: string,
    network: string,
    request: CreateWebhookSubscriptionRequest
  ): WebhookSubscription {
    const now = new Date().toISOString();
    const subscription: WebhookSubscription = {
      id: this.id('webhook'),
      projectId,
      name: request.name.trim(),
      contractHash: request.contractHash,
      network,
      eventName: request.eventName ?? null,
      webhookUrl: request.webhookUrl.trim(),
      headers: request.headers ?? {},
      isEnabled: request.isEnabled,
      createdAt: now,
      updatedAt: now,
      latestDelivery: null
    };
    this.subscriptions.unshift(subscription);
    return this.copy(subscription);
  }

  updateSubscription(subscriptionId: string, request: CreateWebhookSubscriptionRequest): WebhookSubscription {
    const index = this.subscriptions.findIndex((item) => item.id === subscriptionId);
    if (index < 0) {
      throw new Error('The demo webhook was not found.');
    }
    const current = this.subscriptions[index];
    const updated: WebhookSubscription = {
      ...current,
      name: request.name.trim(),
      contractHash: request.contractHash,
      network: request.network,
      eventName: request.eventName ?? null,
      webhookUrl: request.webhookUrl.trim(),
      headers: request.headers ?? {},
      isEnabled: request.isEnabled,
      updatedAt: new Date().toISOString()
    };
    this.subscriptions[index] = updated;
    return this.copy(updated);
  }

  deleteSubscription(subscriptionId: string): void {
    this.removeWhere(this.subscriptions, (item) => item.id === subscriptionId);
    this.removeWhere(this.deliveries, (item) => item.subscriptionId === subscriptionId);
  }

  getDeliveries(subscriptionId: string): WebhookDelivery[] {
    return this.copy(this.deliveries
      .filter((delivery) => delivery.subscriptionId === subscriptionId)
      .sort((left, right) => Date.parse(right.deliveredAt) - Date.parse(left.deliveredAt)));
  }

  sendTest(subscriptionId: string): WebhookDelivery {
    const subscription = this.requireSubscription(subscriptionId);
    const delivery: WebhookDelivery = {
      id: this.id('delivery'),
      subscriptionId,
      eventId: this.id('test-event'),
      webhookUrl: subscription.webhookUrl,
      statusCode: 202,
      succeeded: true,
      deliveredAt: new Date().toISOString(),
      trigger: 'test'
    };
    this.recordDelivery(subscription, delivery);
    return this.copy(delivery);
  }

  redeliver(subscriptionId: string, deliveryId: string): WebhookDelivery {
    const subscription = this.requireSubscription(subscriptionId);
    const original = this.deliveries.find((delivery) => delivery.id === deliveryId);
    if (!original) {
      throw new Error('The demo delivery was not found.');
    }
    const delivery: WebhookDelivery = {
      ...original,
      id: this.id('delivery'),
      statusCode: 200,
      succeeded: true,
      error: null,
      deliveredAt: new Date().toISOString(),
      trigger: 'manual',
      redeliveryOfDeliveryId: original.id
    };
    this.recordDelivery(subscription, delivery);
    return this.copy(delivery);
  }

  getUsage(projectId: string, network: string): RelayUsage {
    const base = this.usage[network] ?? this.usage['neo3:testnet'];
    const activeSubscriptions = this.subscriptions.filter((subscription) =>
      subscription.projectId === projectId && subscription.network === network && subscription.isEnabled
    ).length;
    return this.copy({ ...base, activeSubscriptions });
  }

  createPaymentIntent(projectId: string): RelayPaymentIntent {
    const now = new Date();
    const intent: RelayPaymentIntent = {
      id: this.id('payment-intent'),
      projectId,
      network: 'neo3:mainnet',
      recipientAddress: 'NDemoRelayTreasury00000000000000000',
      recipientScriptHash: `0x${'7a'.repeat(20)}`,
      requiredGasDatoshis: 500000000,
      status: 'pending',
      createdAt: now.toISOString(),
      expiresAt: new Date(now.getTime() + 15 * 60_000).toISOString()
    };
    this.paymentHistory.pendingIntents.unshift(intent);
    return this.copy(intent);
  }

  confirmPayment(intentId: string, transactionId: string): RelayPayment {
    const intentIndex = this.paymentHistory.pendingIntents.findIndex((intent) => intent.id === intentId);
    const intent = this.paymentHistory.pendingIntents[intentIndex];
    if (!intent) {
      const existing = this.paymentHistory.payments.find((payment) => payment.intentId === intentId);
      if (existing) {
        return this.copy(existing);
      }
      throw new Error('The demo payment intent was not found.');
    }
    const entitlementEndsAt = new Date(Date.now() + 30 * 24 * 60 * 60_000).toISOString();
    const payment: RelayPayment = {
      transactionId,
      intentId,
      status: 'confirmed',
      entitlementEndsAt
    };
    this.paymentHistory.pendingIntents.splice(intentIndex, 1);
    this.paymentHistory.payments.unshift(payment);
    this.usage['neo3:mainnet'] = {
      ...this.usage['neo3:mainnet'],
      plan: 'paid',
      status: 'active',
      periodEndsAt: entitlementEndsAt
    };
    return this.copy(payment);
  }

  getPaymentHistory(): RelayPaymentHistory {
    return this.copy(this.paymentHistory);
  }

  private currentContract(projectId: string, network: string, excludeId = ''): string | null {
    return this.deployments
      .filter((deployment) => deployment.id !== excludeId
        && deployment.projectId === projectId
        && deployment.network === network
        && deployment.status === 'confirmed'
        && deployment.contractHash)
      .sort((left, right) => Date.parse(right.createdAt) - Date.parse(left.createdAt))[0]
      ?.contractHash ?? null;
  }

  private updateDeployment(deploymentId: string, changes: Partial<Deployment>): Deployment {
    const index = this.deployments.findIndex((deployment) => deployment.id === deploymentId);
    if (index < 0) {
      throw new Error('The demo deployment was not found. Reload to reset the demo.');
    }
    this.deployments[index] = { ...this.deployments[index], ...changes };
    return this.copy(this.deployments[index]);
  }

  private requireProject(projectId: string): Project {
    const project = this.projects.find((item) => item.id === projectId);
    if (!project) {
      throw new Error('The demo project was not found. Reload to reset the demo.');
    }
    return project;
  }

  private requireDeployment(deploymentId: string): Deployment {
    const deployment = this.deployments.find((item) => item.id === deploymentId);
    if (!deployment) {
      throw new Error('The demo deployment was not found.');
    }
    return deployment;
  }

  private requireSubscription(subscriptionId: string): WebhookSubscription {
    const subscription = this.subscriptions.find((item) => item.id === subscriptionId);
    if (!subscription) {
      throw new Error('The demo webhook was not found.');
    }
    return subscription;
  }

  private recordDelivery(subscription: WebhookSubscription, delivery: WebhookDelivery): void {
    this.deliveries.unshift(delivery);
    const index = this.subscriptions.findIndex((item) => item.id === subscription.id);
    this.subscriptions[index] = {
      ...subscription,
      latestDelivery: delivery,
      updatedAt: delivery.deliveredAt
    };
  }

  private id(prefix: string): string {
    return `demo-${prefix}-${crypto.randomUUID()}`;
  }

  private hash(byteLength: number): string {
    const bytes = crypto.getRandomValues(new Uint8Array(byteLength));
    return `0x${[...bytes].map((value) => value.toString(16).padStart(2, '0')).join('')}`;
  }

  private removeWhere<T>(items: T[], predicate: (item: T) => boolean): void {
    for (let index = items.length - 1; index >= 0; index -= 1) {
      if (predicate(items[index])) {
        items.splice(index, 1);
      }
    }
  }

  private copy<T>(value: T): T {
    return structuredClone(value);
  }
}
