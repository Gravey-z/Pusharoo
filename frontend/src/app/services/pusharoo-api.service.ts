import { HttpClient, HttpContext } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { defer, forkJoin, map, Observable, shareReplay, switchMap, tap } from 'rxjs';
import {
  Artifact,
  ArtifactComparison,
  AddProjectAuthorizedDeployerRequest,
  ChangedMethod,
  CreateDeploymentRequest,
  DeleteProjectRequest,
  RecoverDeploymentRequest,
  StartDeploymentAttemptRequest,
  Deployment,
  DeploymentReview,
  DeploymentReviewRequest,
  NeoMethod,
  NeoParameter,
  NeoPermission,
  Project,
  ProjectCardViewModel,
  ProjectListItem,
  ProjectCreationSignature,
  ProjectAuthorizedDeployer,
  ProjectOverviewViewModel,
  WalletActionSignature,
  RemoveProjectAuthorizedDeployerRequest,
  UpdateProjectAuthorizedDeployerRequest
} from '../models/pusharoo.models';
import { RuntimeConfigService } from './runtime-config.service';
import { REQUIRE_WALLET_SESSION } from './wallet-auth.interceptor';
import { PROJECT_DATA_CACHE_TTL_MS } from './project-workspace-context.service';

interface CachedGetRequest {
  expiresAt: number;
  response: Observable<unknown>;
}

@Injectable({ providedIn: 'root' })
export class PusharooApiService {
  private readonly getCache = new Map<string, CachedGetRequest>();
  private get apiBaseUrl(): string { return this.runtimeConfig.value.apiBaseUrl.replace(/\/$/, ''); }

  constructor(private readonly http: HttpClient, private readonly runtimeConfig: RuntimeConfigService) {}

  getProjectCards(): Observable<ProjectListItem[]> {
    return this.cachedGet('projects:cards', () => this.http.get<ProjectListItem[]>(`${this.apiBaseUrl}/projects/cards`));
  }

  getProjectOverview(projectId: string): Observable<ProjectOverviewViewModel> {
    return this.cachedGet(`project:${projectId}:overview`, () => this.http.get<Project>(`${this.apiBaseUrl}/projects/${projectId}`)
      .pipe(switchMap((project) => this.getProjectCard(project))));
  }

  getArtifact(artifactId: string): Observable<Artifact> {
    return this.cachedGet(`artifact:${artifactId}`, () => this.http.get<Artifact>(`${this.apiBaseUrl}/artifacts/${artifactId}`));
  }

  getArtifactNefHex(artifactId: string): Observable<string> {
    return this.cachedGet(`artifact:${artifactId}:nef`, () => this.http
      .get(`${this.apiBaseUrl}/artifacts/${artifactId}/nef`, { responseType: 'arraybuffer' })
      .pipe(map((buffer) => this.arrayBufferToHex(buffer))));
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
    }).pipe(tap(() => this.clearGetCache()));
  }

  deleteProject(projectId: string, request: DeleteProjectRequest): Observable<void> {
    return this.http.delete<void>(`${this.apiBaseUrl}/projects/${projectId}`, { body: request })
      .pipe(tap(() => this.clearGetCache()));
  }

  getAuthorizedDeployers(projectId: string): Observable<ProjectAuthorizedDeployer[]> {
    return this.cachedGet(`project:${projectId}:authorized-deployers`, () =>
      this.http.get<ProjectAuthorizedDeployer[]>(`${this.apiBaseUrl}/projects/${projectId}/authorized-deployers`));
  }

  addAuthorizedDeployer(projectId: string, request: AddProjectAuthorizedDeployerRequest): Observable<ProjectAuthorizedDeployer> {
    return this.http.post<ProjectAuthorizedDeployer>(`${this.apiBaseUrl}/projects/${projectId}/authorized-deployers`, request)
      .pipe(tap(() => this.invalidateProjectCache(projectId)));
  }

  updateAuthorizedDeployer(
    projectId: string,
    walletAddress: string,
    request: UpdateProjectAuthorizedDeployerRequest
  ): Observable<ProjectAuthorizedDeployer> {
    return this.http.put<ProjectAuthorizedDeployer>(
      `${this.apiBaseUrl}/projects/${projectId}/authorized-deployers/${encodeURIComponent(walletAddress)}`,
      request
    ).pipe(tap(() => this.invalidateProjectCache(projectId)));
  }

  removeAuthorizedDeployer(
    projectId: string,
    walletAddress: string,
    request: RemoveProjectAuthorizedDeployerRequest
  ): Observable<void> {
    return this.http.delete<void>(
      `${this.apiBaseUrl}/projects/${projectId}/authorized-deployers/${encodeURIComponent(walletAddress)}`,
      { body: request }
    ).pipe(tap(() => this.invalidateProjectCache(projectId)));
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
    ).pipe(tap(() => this.invalidateProjectCache(projectId)));
  }

  compareArtifacts(
    projectId: string,
    fromVersion: string,
    toVersion: string
  ): Observable<ArtifactComparison> {
    return this.cachedGet(`project:${projectId}:comparison:${fromVersion}:${toVersion}`, () =>
      this.http.get<ArtifactComparison>(
        `${this.apiBaseUrl}/projects/${projectId}/artifacts/compare`,
        { params: { from: fromVersion, to: toVersion } }
      ));
  }

  createDeployment(
    projectId: string,
    request: CreateDeploymentRequest
  ): Observable<Deployment> {
    return this.http.post<Deployment>(
      `${this.apiBaseUrl}/projects/${projectId}/deployments`,
      request
    ).pipe(tap(() => this.invalidateProjectCache(projectId)));
  }

  recoverDeployment(
    projectId: string,
    request: RecoverDeploymentRequest
  ): Observable<Deployment> {
    return this.http.post<Deployment>(
      `${this.apiBaseUrl}/projects/${projectId}/deployments/recover`,
      request,
      { context: this.walletSessionContext() }
    ).pipe(tap(() => this.invalidateProjectCache(projectId)));
  }

  startDeploymentAttempt(projectId: string, request: StartDeploymentAttemptRequest): Observable<Deployment> {
    return this.http.post<Deployment>(`${this.apiBaseUrl}/projects/${projectId}/deployments/attempts`, request,
      { context: this.walletSessionContext() })
      .pipe(tap(() => this.invalidateProjectCache(projectId)));
  }

  reviewDeployment(
    projectId: string,
    request: DeploymentReviewRequest
  ): Observable<DeploymentReview> {
    return this.http.post<DeploymentReview>(
      `${this.apiBaseUrl}/projects/${projectId}/deployments/review`,
      request,
      { context: this.walletSessionContext() }
    );
  }

  resumeDeploymentAttempt(projectId: string, deploymentId: string): Observable<Deployment> {
    return this.http.post<Deployment>(
      `${this.apiBaseUrl}/projects/${projectId}/deployments/${deploymentId}/resume`, {},
      { context: this.walletSessionContext() }
    );
  }

  markDeploymentSubmitted(projectId: string, deploymentId: string, transactionId: string, attemptCapability: string): Observable<Deployment> {
    return this.http.post<Deployment>(
      `${this.apiBaseUrl}/projects/${projectId}/deployments/${deploymentId}/submitted`,
      { transactionId, attemptCapability },
      { context: this.walletSessionContext() }
    ).pipe(tap(() => this.invalidateProjectCache(projectId)));
  }

  confirmDeploymentAttempt(projectId: string, deploymentId: string, attemptCapability: string): Observable<Deployment> {
    return this.http.post<Deployment>(
      `${this.apiBaseUrl}/projects/${projectId}/deployments/${deploymentId}/confirm`,
      { attemptCapability },
      { context: this.walletSessionContext() }
    ).pipe(tap(() => this.invalidateProjectCache(projectId)));
  }

  markDeploymentFailed(
    projectId: string,
    deploymentId: string,
    attemptCapability: string,
    stage: 'preparing' | 'wallet' | 'confirmation' | 'record',
    reason: string
  ): Observable<Deployment> {
    return this.http.post<Deployment>(
      `${this.apiBaseUrl}/projects/${projectId}/deployments/${deploymentId}/failed`,
      { attemptCapability, stage, reason },
      { context: this.walletSessionContext() }
    ).pipe(tap(() => this.invalidateProjectCache(projectId)));
  }

  getDeployments(projectId: string): Observable<Deployment[]> {
    return this.cachedGet(`project:${projectId}:deployments`, () =>
      this.http.get<Deployment[]>(`${this.apiBaseUrl}/projects/${projectId}/deployments`));
  }

  private walletSessionContext(): HttpContext {
    return new HttpContext().set(REQUIRE_WALLET_SESSION, true);
  }

  private getArtifacts(projectId: string): Observable<Artifact[]> {
    return this.cachedGet(`project:${projectId}:artifacts`, () =>
      this.http.get<Artifact[]>(`${this.apiBaseUrl}/projects/${projectId}/artifacts`));
  }

  private cachedGet<T>(key: string, request: () => Observable<T>): Observable<T> {
    const now = Date.now();
    for (const [cachedKey, entry] of this.getCache) {
      if (entry.expiresAt <= now) {
        this.getCache.delete(cachedKey);
      }
    }

    const cached = this.getCache.get(key);
    if (cached && cached.expiresAt > now) {
      return cached.response as Observable<T>;
    }
    if (cached) {
      this.getCache.delete(key);
    }

    let entry!: CachedGetRequest;
    const response = defer(request).pipe(
      tap({
        next: () => {
          if (this.getCache.get(key) === entry) {
            entry.expiresAt = Date.now() + PROJECT_DATA_CACHE_TTL_MS;
          }
        },
        error: () => {
          if (this.getCache.get(key) === entry) {
            this.getCache.delete(key);
          }
        }
      }),
      shareReplay({ bufferSize: 1, refCount: false })
    );

    entry = { expiresAt: Date.now() + PROJECT_DATA_CACHE_TTL_MS, response };
    if (this.getCache.size >= 100) {
      const oldestKey = this.getCache.keys().next().value;
      if (oldestKey !== undefined) {
        this.getCache.delete(oldestKey);
      }
    }
    this.getCache.set(key, entry);
    return response;
  }

  private invalidateProjectCache(projectId: string): void {
    const prefix = `project:${projectId}:`;
    for (const key of this.getCache.keys()) {
      if (key === 'projects:cards' || key.startsWith(prefix)) {
        this.getCache.delete(key);
      }
    }
  }

  private clearGetCache(): void {
    this.getCache.clear();
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
}
