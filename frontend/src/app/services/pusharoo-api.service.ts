import { HttpClient, HttpErrorResponse, HttpHeaders } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { forkJoin, map, Observable, of, switchMap } from 'rxjs';
import {
  Artifact,
  ArtifactComparison,
  ChangedMethod,
  CreateDeploymentRequest,
  DeleteProjectRequest,
  RecoverDeploymentRequest,
  StartDeploymentAttemptRequest,
  CreateWebhookSubscriptionRequest,
  Deployment,
  EventRelayStatus,
  NeoMethod,
  NeoParameter,
  NeoPermission,
  Project,
  ProjectCardViewModel,
  ProjectCreationSignature,
  ProjectOverviewViewModel,
  WalletActionSignature,
  WebhookDelivery,
  WebhookManagementOperation,
  WebhookSubscription,
  RelayUsage,
  RelayPaymentIntent,
  RelayPayment,
  RelayPaymentHistory
} from '../models/pusharoo.models';
import { RuntimeConfigService } from './runtime-config.service';

@Injectable({ providedIn: 'root' })
export class PusharooApiService {
  private readonly webhookSessions = new Map<string, string>();
  private get apiBaseUrl(): string { return this.runtimeConfig.value.apiBaseUrl.replace(/\/$/, ''); }

  constructor(private readonly http: HttpClient, private readonly runtimeConfig: RuntimeConfigService) {}

  hasWebhookSession(projectId: string, network: string): boolean {
    return this.webhookSessions.has(this.webhookSessionKey(projectId, network));
  }

  clearWebhookSession(projectId: string, network: string): void {
    this.webhookSessions.delete(this.webhookSessionKey(projectId, network));
  }

  isWebhookSessionExpired(error: unknown): boolean {
    return error instanceof HttpErrorResponse && error.status === 401;
  }

  getProjectCards(): Observable<ProjectCardViewModel[]> {
    return this.http.get<Project[]>(`${this.apiBaseUrl}/projects`).pipe(
      switchMap((projects) => {
        if (projects.length === 0) {
          return of([]);
        }

        return forkJoin(projects.map((project) => this.getProjectCard(project)));
      })
    );
  }

  getProjectOverview(projectId: string): Observable<ProjectOverviewViewModel> {
    return this.http.get<Project>(`${this.apiBaseUrl}/projects/${projectId}`).pipe(
      switchMap((project) => this.getProjectCard(project))
    );
  }

  getArtifact(artifactId: string): Observable<Artifact> {
    return this.http.get<Artifact>(`${this.apiBaseUrl}/artifacts/${artifactId}`);
  }

  getArtifactNefHex(artifactId: string): Observable<string> {
    return this.http
      .get(`${this.apiBaseUrl}/artifacts/${artifactId}/nef`, { responseType: 'arraybuffer' })
      .pipe(map((buffer) => this.arrayBufferToHex(buffer)));
  }

  createProject(
    name: string,
    description: string,
    signature: ProjectCreationSignature
  ): Observable<Project> {
    return this.http.post<Project>(`${this.apiBaseUrl}/projects`, {
      name,
      description: description.trim() || null,
      signature
    });
  }

  deleteProject(projectId: string, request: DeleteProjectRequest): Observable<void> {
    return this.http.delete<void>(`${this.apiBaseUrl}/projects/${projectId}`, { body: request });
  }

  uploadArtifact(
    projectId: string,
    version: string,
    notes: string,
    signature: WalletActionSignature,
    nefFile: File,
    manifestFile: File
  ): Observable<Artifact> {
    const formData = new FormData();
    formData.append('version', version);
    formData.append('notes', notes);
    formData.append('signature', JSON.stringify(signature));
    formData.append('files', nefFile, nefFile.name);
    formData.append('files', manifestFile, manifestFile.name);

    return this.http.post<Artifact>(
      `${this.apiBaseUrl}/projects/${projectId}/artifacts`,
      formData
    );
  }

  compareArtifacts(
    projectId: string,
    fromVersion: string,
    toVersion: string
  ): Observable<ArtifactComparison> {
    return this.http.get<ArtifactComparison>(
      `${this.apiBaseUrl}/projects/${projectId}/artifacts/compare`,
      { params: { from: fromVersion, to: toVersion } }
    );
  }

  createDeployment(
    projectId: string,
    request: CreateDeploymentRequest
  ): Observable<Deployment> {
    return this.http.post<Deployment>(
      `${this.apiBaseUrl}/projects/${projectId}/deployments`,
      request
    );
  }

  recoverDeployment(
    projectId: string,
    request: RecoverDeploymentRequest
  ): Observable<Deployment> {
    return this.http.post<Deployment>(
      `${this.apiBaseUrl}/projects/${projectId}/deployments/recover`,
      request
    );
  }

  startDeploymentAttempt(projectId: string, request: StartDeploymentAttemptRequest): Observable<Deployment> {
    return this.http.post<Deployment>(`${this.apiBaseUrl}/projects/${projectId}/deployments/attempts`, request);
  }

  markDeploymentSubmitted(projectId: string, deploymentId: string, transactionId: string, deployedBy: string): Observable<Deployment> {
    return this.http.post<Deployment>(
      `${this.apiBaseUrl}/projects/${projectId}/deployments/${deploymentId}/submitted`,
      { transactionId, deployedBy }
    );
  }

  confirmDeploymentAttempt(projectId: string, deploymentId: string, deployedBy: string): Observable<Deployment> {
    return this.http.post<Deployment>(
      `${this.apiBaseUrl}/projects/${projectId}/deployments/${deploymentId}/confirm`,
      { deployedBy }
    );
  }

  markDeploymentFailed(
    projectId: string,
    deploymentId: string,
    deployedBy: string,
    stage: 'preparing' | 'wallet' | 'confirmation' | 'record',
    reason: string
  ): Observable<Deployment> {
    return this.http.post<Deployment>(
      `${this.apiBaseUrl}/projects/${projectId}/deployments/${deploymentId}/failed`,
      { deployedBy, stage, reason }
    );
  }

  getDeployments(projectId: string): Observable<Deployment[]> {
    return this.http.get<Deployment[]>(`${this.apiBaseUrl}/projects/${projectId}/deployments`);
  }

  getWebhookSubscriptions(
    projectId: string,
    network: string,
    signature?: WalletActionSignature
  ): Observable<WebhookSubscription[]> {
    return this.http.post<WebhookSubscription[]>(
      `${this.eventRelayBaseUrl(network)}/projects/${projectId}/subscriptions/query`,
      { signature },
      { headers: this.webhookSessionHeaders(projectId, network), observe: 'response' }
    ).pipe(map((response) => this.readWebhookResponse(projectId, network, response)));
  }

  getEventRelayStatus(network: string): Observable<EventRelayStatus> {
    const healthUrl = this.eventRelayHealthUrl(network);
    return this.http.get<EventRelayStatus>(healthUrl);
  }

  getRelayUsage(projectId: string, network: string, signature?: WalletActionSignature): Observable<RelayUsage> {
    return this.http.post<RelayUsage>(`${this.eventRelayBaseUrl(network)}/projects/${projectId}/subscriptions/usage`, { signature }, { headers: this.webhookSessionHeaders(projectId, network), observe: 'response' }).pipe(map(response => this.readWebhookResponse(projectId, network, response)));
  }

  createRelayPaymentIntent(projectId: string, signature: WalletActionSignature): Observable<RelayPaymentIntent> {
    const network = 'neo3:mainnet';
    return this.http.post<RelayPaymentIntent>(
      `${this.eventRelayBaseUrl(network)}/projects/${projectId}/relay/payments/intents`,
      { signature }, { headers: this.webhookSessionHeaders(projectId, network), observe: 'response' }
    ).pipe(map(response => this.readWebhookResponse(projectId, network, response)));
  }

  confirmRelayPayment(projectId: string, intentId: string, transactionId: string, signature?: WalletActionSignature): Observable<RelayPayment> {
    const network = 'neo3:mainnet';
    return this.http.post<RelayPayment>(
      `${this.eventRelayBaseUrl(network)}/projects/${projectId}/relay/payments/confirm`,
      { intentId, transactionId, signature }, { headers: this.webhookSessionHeaders(projectId, network), observe: 'response' }
    ).pipe(map(response => this.readWebhookResponse(projectId, network, response)));
  }

  getRelayPaymentHistory(projectId: string, signature?: WalletActionSignature): Observable<RelayPaymentHistory> {
    const network = 'neo3:mainnet';
    return this.http.post<RelayPaymentHistory>(
      `${this.eventRelayBaseUrl(network)}/projects/${projectId}/relay/payments/history/query`,
      { signature }, { headers: this.webhookSessionHeaders(projectId, network), observe: 'response' }
    ).pipe(map(response => this.readWebhookResponse(projectId, network, response)));
  }

  createWebhookSubscription(
    projectId: string,
    network: string,
    request: CreateWebhookSubscriptionRequest,
    signature?: WalletActionSignature
  ): Observable<WebhookSubscription> {
    const { projectId: ignoredProjectId, ...subscription } = request;

    return this.http.post<WebhookSubscription>(
      `${this.eventRelayBaseUrl(network)}/projects/${projectId}/subscriptions`,
      { ...subscription, signature },
      { headers: this.webhookSessionHeaders(projectId, network), observe: 'response' }
    ).pipe(map((response) => this.readWebhookResponse(projectId, network, response)));
  }

  updateWebhookSubscription(
    projectId: string,
    network: string,
    subscriptionId: string,
    request: CreateWebhookSubscriptionRequest,
    signature?: WalletActionSignature
  ): Observable<WebhookSubscription> {
    const { projectId: ignoredProjectId, ...subscription } = request;

    return this.http.put<WebhookSubscription>(
      `${this.eventRelayBaseUrl(network)}/projects/${projectId}/subscriptions/${subscriptionId}`,
      { ...subscription, signature },
      { headers: this.webhookSessionHeaders(projectId, network), observe: 'response' }
    ).pipe(map((response) => this.readWebhookResponse(projectId, network, response)));
  }

  deleteWebhookSubscription(
    projectId: string,
    network: string,
    subscriptionId: string,
    signature?: WalletActionSignature
  ): Observable<void> {
    return this.http.delete<void>(
      `${this.eventRelayBaseUrl(network)}/projects/${projectId}/subscriptions/${subscriptionId}`,
      { body: { signature }, headers: this.webhookSessionHeaders(projectId, network), observe: 'response' }
    ).pipe(map((response) => this.readWebhookResponse(projectId, network, response)));
  }

  getWebhookDeliveries(
    projectId: string,
    network: string,
    subscriptionId: string,
    signature?: WalletActionSignature
  ): Observable<WebhookDelivery[]> {
    return this.http.post<WebhookDelivery[]>(
      `${this.eventRelayBaseUrl(network)}/projects/${projectId}/subscriptions/${subscriptionId}/deliveries/query`,
      { signature },
      { headers: this.webhookSessionHeaders(projectId, network), observe: 'response' }
    ).pipe(map((response) => this.readWebhookResponse(projectId, network, response) ?? []));
  }

  sendWebhookTest(projectId: string, network: string, subscriptionId: string, signature?: WalletActionSignature): Observable<WebhookDelivery> {
    return this.http.post<WebhookDelivery>(
      `${this.eventRelayBaseUrl(network)}/projects/${projectId}/subscriptions/${subscriptionId}/test`,
      { signature },
      { headers: this.webhookSessionHeaders(projectId, network), observe: 'response' }
    ).pipe(map((response) => this.readWebhookResponse(projectId, network, response)));
  }

  redeliverWebhook(projectId: string, network: string, subscriptionId: string, deliveryId: string, signature?: WalletActionSignature): Observable<WebhookDelivery> {
    return this.http.post<WebhookDelivery>(
      `${this.eventRelayBaseUrl(network)}/projects/${projectId}/subscriptions/${subscriptionId}/deliveries/${deliveryId}/redeliver`,
      { signature },
      { headers: this.webhookSessionHeaders(projectId, network), observe: 'response' }
    ).pipe(map((response) => this.readWebhookResponse(projectId, network, response)));
  }

  async getWebhookManagementRequestHash(
    projectId: string,
    operation: WebhookManagementOperation,
    content: {
      subscriptionId?: string;
      subscription?: CreateWebhookSubscriptionRequest;
    } = {}
  ): Promise<string> {
    const subscription = content.subscription;
    const headers = Object.entries(subscription?.headers ?? {})
      .map(([key, value]) => `${key.trim().toLowerCase()}:${value.trim()}`)
      .sort()
      .join('\n');
    const secretHash = await this.sha256Hex(subscription?.secret?.trim() ?? '');
    const headersHash = await this.sha256Hex(headers);
    const payload = [
      `Project ID: ${projectId.trim()}`,
      `Operation: ${operation}`,
      `Subscription ID: ${content.subscriptionId?.trim() ?? ''}`,
      `Name: ${subscription?.name.trim() ?? ''}`,
      `Contract hash: ${subscription?.contractHash.trim().toLowerCase() ?? ''}`,
      `Network: ${subscription?.network.trim() ?? ''}`,
      `Event name: ${subscription?.eventName?.trim() ?? ''}`,
      `Webhook URL: ${subscription?.webhookUrl.trim() ?? ''}`,
      `Enabled: ${subscription ? String(subscription.isEnabled).toLowerCase() : ''}`,
      `Secret SHA-256: ${secretHash}`,
      `Headers SHA-256: ${headersHash}`
    ].join('\n');

    return this.sha256Hex(payload);
  }

  async getPaymentIntentRequestHash(projectId: string): Promise<string> {
    return this.sha256Hex([`Project ID: ${projectId.trim()}`, 'Operation: payments.create'].join('\n'));
  }

  async getPaymentConfirmationRequestHash(projectId: string, intentId: string, transactionId: string): Promise<string> {
    return this.sha256Hex([
      `Project ID: ${projectId.trim()}`,
      'Operation: payments.confirm',
      `Payment intent ID: ${intentId.trim()}`,
      `Transaction ID: ${transactionId.trim().toLowerCase()}`
    ].join('\n'));
  }

  private getArtifacts(projectId: string): Observable<Artifact[]> {
    return this.http.get<Artifact[]>(`${this.apiBaseUrl}/projects/${projectId}/artifacts`);
  }

  private eventRelayBaseUrl(network: string): string {
    return (this.runtimeConfig.value.eventRelays?.[network]?.baseUrl
      ?? this.runtimeConfig.value.eventRelayBaseUrl).replace(/\/$/, '');
  }

  private eventRelayHealthUrl(network: string): string {
    return this.runtimeConfig.value.eventRelays?.[network]?.healthUrl
      ?? this.runtimeConfig.value.eventRelayHealthUrl;
  }

  private webhookSessionHeaders(projectId: string, network: string): HttpHeaders {
    const session = this.webhookSessions.get(this.webhookSessionKey(projectId, network));
    return session ? new HttpHeaders({ 'X-Pusharoo-Webhook-Session': session }) : new HttpHeaders();
  }

  private webhookSessionKey(projectId: string, network: string): string {
    return `${network}\n${projectId.trim()}`;
  }

  private readWebhookResponse<T>(projectId: string, network: string, response: { headers: HttpHeaders; body: T | null }): T {
    const session = response.headers.get('X-Pusharoo-Webhook-Session');
    if (session) {
      this.webhookSessions.set(this.webhookSessionKey(projectId, network), session);
    }

    return response.body as T;
  }

  private getProjectCard(project: Project): Observable<ProjectCardViewModel> {
    return forkJoin({
      artifacts: this.getArtifacts(project.id),
      deployments: this.getDeployments(project.id)
    }).pipe(
      map(({ artifacts, deployments }) => this.toProjectCard(project, artifacts, deployments))
    );
  }

  private toProjectCard(
    project: Project,
    artifacts: Artifact[],
    deployments: Deployment[] = []
  ): ProjectCardViewModel {
    const sortedArtifacts = [...artifacts].sort(
      (left, right) =>
        new Date(right.createdAt).getTime() - new Date(left.createdAt).getTime()
    );
    const sortedDeployments = [...deployments].sort(
      (left, right) =>
        new Date(right.createdAt).getTime() - new Date(left.createdAt).getTime()
    );

    const confirmedDeployments = sortedDeployments.filter((deployment) =>
      Boolean(deployment.contractHash)
      && (!deployment.status || deployment.status === 'confirmed')
    );

    return {
      project,
      artifacts: sortedArtifacts,
      latestArtifact: sortedArtifacts[0] ?? null,
      deployments: sortedDeployments,
      latestDeployment: confirmedDeployments[0] ?? null,
      deployed: confirmedDeployments.length > 0
    };
  }

  private compareLocalArtifacts(fromArtifact: Artifact, toArtifact: Artifact): ArtifactComparison {
    const fromMethods = this.toMethodMap(fromArtifact.manifest.abi.methods);
    const toMethods = this.toMethodMap(toArtifact.manifest.abi.methods);
    const addedMethods = [...toMethods.keys()]
      .filter((name) => !fromMethods.has(name))
      .sort();
    const removedMethods = [...fromMethods.keys()]
      .filter((name) => !toMethods.has(name))
      .sort();
    const changedMethods = [...fromMethods.keys()]
      .filter((name) => toMethods.has(name))
      .map((name) => this.getChangedMethod(name, fromMethods.get(name), toMethods.get(name)))
      .filter((change): change is ChangedMethod => change !== null)
      .sort((left, right) => left.name.localeCompare(right.name));
    const fromEventNames = new Set(
      fromArtifact.manifest.abi.events.map((event) => event.name)
    );
    const addedEvents = toArtifact.manifest.abi.events
      .map((event) => event.name)
      .filter((name) => !fromEventNames.has(name))
      .sort();
    const permissionChanges = this.getPermissionChanges(
      fromArtifact.manifest.permissions,
      toArtifact.manifest.permissions
    );

    return {
      addedMethods,
      removedMethods,
      changedMethods,
      addedEvents,
      permissionChanges
    };
  }

  private toMethodMap(methods: NeoMethod[]): Map<string, NeoMethod> {
    return new Map(methods.map((method) => [method.name, method]));
  }

  private getChangedMethod(
    name: string,
    fromMethod?: NeoMethod,
    toMethod?: NeoMethod
  ): ChangedMethod | null {
    if (!fromMethod || !toMethod) {
      return null;
    }

    if (this.getMethodSignature(fromMethod) === this.getMethodSignature(toMethod)) {
      return null;
    }

    const changes: string[] = [];
    const fromParameters = this.getParameterSignature(fromMethod.parameters);
    const toParameters = this.getParameterSignature(toMethod.parameters);
    const fromReturnType = this.methodReturnType(fromMethod);
    const toReturnType = this.methodReturnType(toMethod);

    if (fromParameters !== toParameters) {
      changes.push(
        `Parameters changed from ${this.formatParameters(fromMethod.parameters)} to ${this.formatParameters(toMethod.parameters)}`
      );
    }

    if (fromReturnType !== toReturnType) {
      changes.push(`Return type changed from ${fromReturnType} to ${toReturnType}`);
    }

    if (fromMethod.safe !== toMethod.safe) {
      changes.push(`Safe flag changed from ${fromMethod.safe} to ${toMethod.safe}`);
    }

    return { name, changes: changes.length ? changes : ['Method signature changed'] };
  }

  private getMethodSignature(method: NeoMethod): string {
    return `${method.name}(${this.getParameterSignature(method.parameters)}):${this.methodReturnType(method)}:safe=${method.safe}`;
  }

  private getParameterSignature(parameters: NeoParameter[]): string {
    return parameters.map((parameter) => `${parameter.name}:${parameter.type}`).join(',');
  }

  private methodReturnType(method: NeoMethod): string {
    return method.returntype ?? method.returnType ?? '';
  }

  private formatParameters(parameters: NeoParameter[]): string {
    if (parameters.length === 0) {
      return '-';
    }

    return parameters.map((parameter) => `${parameter.name}: ${parameter.type}`).join(', ');
  }

  private getPermissionChanges(
    fromPermissions: NeoPermission[],
    toPermissions: NeoPermission[]
  ): string[] {
    const fromPermissionMap = this.toPermissionMap(fromPermissions);
    const toPermissionMap = this.toPermissionMap(toPermissions);
    const changes: string[] = [];

    for (const [permission, value] of toPermissionMap) {
      if (!fromPermissionMap.has(permission)) {
        changes.push(
          this.isWildcardPermission(value)
            ? 'Added wildcard permission'
            : `Added permission ${permission}`
        );
      }
    }

    for (const permission of fromPermissionMap.keys()) {
      if (!toPermissionMap.has(permission)) {
        changes.push(`Removed permission ${permission}`);
      }
    }

    return changes;
  }

  private toPermissionMap(permissions: NeoPermission[]): Map<string, NeoPermission> {
    return new Map(
      permissions.map((permission) => [this.normalizePermission(permission), permission])
    );
  }

  private normalizePermission(permission: NeoPermission): string {
    return `${this.normalizeValue(permission.contract)}::${this.normalizeValue(permission.methods)}`;
  }

  private isWildcardPermission(permission: NeoPermission): boolean {
    return this.hasWildcard(permission.contract) || this.hasWildcard(permission.methods);
  }

  private hasWildcard(value: unknown): boolean {
    if (value === '*') {
      return true;
    }

    if (Array.isArray(value)) {
      return value.some((item) => this.hasWildcard(item));
    }

    return false;
  }

  private normalizeValue(value: unknown): string {
    if (Array.isArray(value)) {
      return `[${value.map((item) => this.normalizeValue(item)).join(',')}]`;
    }

    if (value && typeof value === 'object') {
      return JSON.stringify(value, Object.keys(value).sort());
    }

    return String(value);
  }

  private findArtifactByVersion(artifacts: Artifact[], version: string): Artifact | undefined {
    const normalizedVersion = version.trim().replace(/^v/i, '');

    return artifacts.find(
      (artifact) => artifact.version.replace(/^v/i, '') === normalizedVersion
    );
  }

  private arrayBufferToHex(buffer: ArrayBuffer): string {
    return [...new Uint8Array(buffer)]
      .map((value) => value.toString(16).padStart(2, '0'))
      .join('');
  }

  private async sha256Hex(value: string): Promise<string> {
    const bytes = new TextEncoder().encode(value);
    const input = new ArrayBuffer(bytes.byteLength);
    new Uint8Array(input).set(bytes);
    const hash = await crypto.subtle.digest('SHA-256', input);

    return [...new Uint8Array(hash)]
      .map((item) => item.toString(16).padStart(2, '0'))
      .join('');
  }
}
