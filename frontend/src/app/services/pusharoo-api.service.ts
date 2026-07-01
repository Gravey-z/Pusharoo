import { HttpClient } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { catchError, forkJoin, map, Observable, of, switchMap } from 'rxjs';
import {
  Artifact,
  ArtifactComparison,
  ChangedMethod,
  CreateDeploymentRequest,
  CreateWebhookSubscriptionRequest,
  Deployment,
  NeoMethod,
  NeoParameter,
  NeoPermission,
  Project,
  ProjectCardViewModel,
  ProjectOverviewViewModel,
  WebhookDelivery,
  WebhookSubscription
} from '../models/pusharoo.models';
import { demoArtifacts, demoProjectCards } from './demo-data';

@Injectable({ providedIn: 'root' })
export class PusharooApiService {
  private readonly apiBaseUrl = 'http://localhost:5000/api';
  private readonly eventRelayBaseUrl = 'http://localhost:5001/api';

  constructor(private readonly http: HttpClient) {}

  getProjectCards(): Observable<ProjectCardViewModel[]> {
    return this.http.get<Project[]>(`${this.apiBaseUrl}/projects`).pipe(
      switchMap((projects) => {
        if (projects.length === 0) {
          return of(demoProjectCards);
        }

        return forkJoin(projects.map((project) => this.getProjectCard(project)));
      }),
      catchError(() => of(demoProjectCards))
    );
  }

  getProjectOverview(projectId: string): Observable<ProjectOverviewViewModel | null> {
    const demoProjectCard = demoProjectCards.find((card) => card.project.id === projectId);
    if (demoProjectCard) {
      return of(demoProjectCard);
    }

    return this.http.get<Project>(`${this.apiBaseUrl}/projects/${projectId}`).pipe(
      switchMap((project) => this.getProjectCard(project)),
      catchError(() => of(null))
    );
  }

  getArtifact(artifactId: string): Observable<Artifact | null> {
    const demoArtifact = demoArtifacts.find((artifact) => artifact.id === artifactId);
    if (demoArtifact) {
      return of(demoArtifact);
    }

    return this.http
      .get<Artifact>(`${this.apiBaseUrl}/artifacts/${artifactId}`)
      .pipe(catchError(() => of(null)));
  }

  getArtifactNefHex(artifactId: string): Observable<string> {
    return this.http
      .get(`${this.apiBaseUrl}/artifacts/${artifactId}/nef`, { responseType: 'arraybuffer' })
      .pipe(map((buffer) => this.arrayBufferToHex(buffer)));
  }

  createProject(name: string, description: string): Observable<Project> {
    return this.http.post<Project>(`${this.apiBaseUrl}/projects`, {
      name,
      description: description.trim() || null
    });
  }

  uploadArtifact(
    projectId: string,
    version: string,
    notes: string,
    nefFile: File,
    manifestFile: File
  ): Observable<Artifact> {
    const formData = new FormData();
    formData.append('version', version);
    formData.append('notes', notes);
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
  ): Observable<ArtifactComparison | null> {
    const demoArtifactsForProject = demoArtifacts.filter(
      (artifact) => artifact.projectId === projectId
    );

    if (demoArtifactsForProject.length > 0) {
      const fromArtifact = this.findArtifactByVersion(demoArtifactsForProject, fromVersion);
      const toArtifact = this.findArtifactByVersion(demoArtifactsForProject, toVersion);

      return of(
        fromArtifact && toArtifact
          ? this.compareLocalArtifacts(fromArtifact, toArtifact)
          : null
      );
    }

    return this.http
      .get<ArtifactComparison>(
        `${this.apiBaseUrl}/projects/${projectId}/artifacts/compare`,
        { params: { from: fromVersion, to: toVersion } }
      )
      .pipe(catchError(() => of(null)));
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

  getDeployments(projectId: string): Observable<Deployment[]> {
    return this.http
      .get<Deployment[]>(`${this.apiBaseUrl}/projects/${projectId}/deployments`)
      .pipe(catchError(() => of([])));
  }

  getWebhookSubscriptions(projectId?: string): Observable<WebhookSubscription[]> {
    return this.http.get<WebhookSubscription[]>(`${this.eventRelayBaseUrl}/subscriptions`).pipe(
      map((subscriptions) =>
        projectId
          ? subscriptions.filter((subscription) => subscription.projectId === projectId)
          : subscriptions
      ),
      catchError(() => of([]))
    );
  }

  createWebhookSubscription(
    request: CreateWebhookSubscriptionRequest
  ): Observable<WebhookSubscription> {
    return this.http.post<WebhookSubscription>(
      `${this.eventRelayBaseUrl}/subscriptions`,
      request
    );
  }

  getWebhookDeliveries(subscriptionId: string): Observable<WebhookDelivery[]> {
    return this.http
      .get<WebhookDelivery[]>(`${this.eventRelayBaseUrl}/subscriptions/${subscriptionId}/deliveries`)
      .pipe(catchError(() => of([])));
  }

  private getArtifacts(projectId: string): Observable<Artifact[]> {
    return this.http
      .get<Artifact[]>(`${this.apiBaseUrl}/projects/${projectId}/artifacts`)
      .pipe(catchError(() => of([])));
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

    return {
      project,
      artifacts: sortedArtifacts,
      latestArtifact: sortedArtifacts[0] ?? null,
      deployments: sortedDeployments,
      latestDeployment: sortedDeployments[0] ?? null,
      deployed: sortedDeployments.length > 0
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
