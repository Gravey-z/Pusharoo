import { HttpClient, HttpErrorResponse, HttpHeaders } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { map, Observable } from 'rxjs';
import {
  CreateWebhookSubscriptionRequest,
  EventRelayStatus,
  RelayPayment,
  RelayPaymentHistory,
  RelayPaymentIntent,
  RelayUsage,
  WalletActionSignature,
  WebhookDelivery,
  WebhookManagementOperation,
  WebhookSubscription
} from '../models/pusharoo.models';
import { RuntimeConfigService } from './runtime-config.service';

@Injectable({ providedIn: 'root' })
export class EventRelayApiService {
  private readonly sessions = new Map<string, string>();

  constructor(private readonly http: HttpClient, private readonly runtimeConfig: RuntimeConfigService) {}

  hasWebhookSession(projectId: string, network: string): boolean {
    return this.sessions.has(this.sessionKey(projectId, network));
  }

  clearWebhookSession(projectId: string, network: string): void {
    this.sessions.delete(this.sessionKey(projectId, network));
  }

  isWebhookSessionExpired(error: unknown): boolean {
    return error instanceof HttpErrorResponse && error.status === 401;
  }

  getWebhookSubscriptions(
    projectId: string,
    network: string,
    signature?: WalletActionSignature
  ): Observable<WebhookSubscription[]> {
    return this.http.post<WebhookSubscription[]>(
      `${this.baseUrl(network)}/projects/${projectId}/subscriptions/query`,
      { signature },
      { headers: this.sessionHeaders(projectId, network), observe: 'response' }
    ).pipe(map((response) => this.readResponse(projectId, network, response)));
  }

  getEventRelayStatus(network: string): Observable<EventRelayStatus> {
    return this.http.get<EventRelayStatus>(this.healthUrl(network));
  }

  getRelayUsage(projectId: string, network: string, signature?: WalletActionSignature): Observable<RelayUsage> {
    return this.http.post<RelayUsage>(
      `${this.baseUrl(network)}/projects/${projectId}/subscriptions/usage`,
      { signature },
      { headers: this.sessionHeaders(projectId, network), observe: 'response' }
    ).pipe(map((response) => this.readResponse(projectId, network, response)));
  }

  createRelayPaymentIntent(projectId: string, signature: WalletActionSignature): Observable<RelayPaymentIntent> {
    const network = 'neo3:mainnet';
    return this.http.post<RelayPaymentIntent>(
      `${this.baseUrl(network)}/projects/${projectId}/relay/payments/intents`,
      { signature },
      { headers: this.sessionHeaders(projectId, network), observe: 'response' }
    ).pipe(map((response) => this.readResponse(projectId, network, response)));
  }

  confirmRelayPayment(
    projectId: string,
    intentId: string,
    transactionId: string,
    signature?: WalletActionSignature
  ): Observable<RelayPayment> {
    const network = 'neo3:mainnet';
    return this.http.post<RelayPayment>(
      `${this.baseUrl(network)}/projects/${projectId}/relay/payments/confirm`,
      { intentId, transactionId, signature },
      { headers: this.sessionHeaders(projectId, network), observe: 'response' }
    ).pipe(map((response) => this.readResponse(projectId, network, response)));
  }

  getRelayPaymentHistory(projectId: string, signature?: WalletActionSignature): Observable<RelayPaymentHistory> {
    const network = 'neo3:mainnet';
    return this.http.post<RelayPaymentHistory>(
      `${this.baseUrl(network)}/projects/${projectId}/relay/payments/history/query`,
      { signature },
      { headers: this.sessionHeaders(projectId, network), observe: 'response' }
    ).pipe(map((response) => this.readResponse(projectId, network, response)));
  }

  createWebhookSubscription(
    projectId: string,
    network: string,
    request: CreateWebhookSubscriptionRequest,
    signature?: WalletActionSignature
  ): Observable<WebhookSubscription> {
    const { projectId: ignoredProjectId, ...subscription } = request;
    return this.http.post<WebhookSubscription>(
      `${this.baseUrl(network)}/projects/${projectId}/subscriptions`,
      { ...subscription, signature },
      { headers: this.sessionHeaders(projectId, network), observe: 'response' }
    ).pipe(map((response) => this.readResponse(projectId, network, response)));
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
      `${this.baseUrl(network)}/projects/${projectId}/subscriptions/${subscriptionId}`,
      { ...subscription, signature },
      { headers: this.sessionHeaders(projectId, network), observe: 'response' }
    ).pipe(map((response) => this.readResponse(projectId, network, response)));
  }

  deleteWebhookSubscription(
    projectId: string,
    network: string,
    subscriptionId: string,
    signature?: WalletActionSignature
  ): Observable<void> {
    return this.http.delete<void>(
      `${this.baseUrl(network)}/projects/${projectId}/subscriptions/${subscriptionId}`,
      { body: { signature }, headers: this.sessionHeaders(projectId, network), observe: 'response' }
    ).pipe(map((response) => this.readResponse(projectId, network, response)));
  }

  getWebhookDeliveries(
    projectId: string,
    network: string,
    subscriptionId: string,
    signature?: WalletActionSignature
  ): Observable<WebhookDelivery[]> {
    return this.http.post<WebhookDelivery[]>(
      `${this.baseUrl(network)}/projects/${projectId}/subscriptions/${subscriptionId}/deliveries/query`,
      { signature },
      { headers: this.sessionHeaders(projectId, network), observe: 'response' }
    ).pipe(map((response) => this.readResponse(projectId, network, response) ?? []));
  }

  sendWebhookTest(projectId: string, network: string, subscriptionId: string, signature?: WalletActionSignature): Observable<WebhookDelivery> {
    return this.http.post<WebhookDelivery>(
      `${this.baseUrl(network)}/projects/${projectId}/subscriptions/${subscriptionId}/test`,
      { signature },
      { headers: this.sessionHeaders(projectId, network), observe: 'response' }
    ).pipe(map((response) => this.readResponse(projectId, network, response)));
  }

  redeliverWebhook(
    projectId: string,
    network: string,
    subscriptionId: string,
    deliveryId: string,
    signature?: WalletActionSignature
  ): Observable<WebhookDelivery> {
    return this.http.post<WebhookDelivery>(
      `${this.baseUrl(network)}/projects/${projectId}/subscriptions/${subscriptionId}/deliveries/${deliveryId}/redeliver`,
      { signature },
      { headers: this.sessionHeaders(projectId, network), observe: 'response' }
    ).pipe(map((response) => this.readResponse(projectId, network, response)));
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

  private baseUrl(network: string): string {
    return (this.runtimeConfig.value.eventRelays?.[network]?.baseUrl
      ?? this.runtimeConfig.value.eventRelayBaseUrl).replace(/\/$/, '');
  }

  private healthUrl(network: string): string {
    return this.runtimeConfig.value.eventRelays?.[network]?.healthUrl
      ?? this.runtimeConfig.value.eventRelayHealthUrl;
  }

  private sessionHeaders(projectId: string, network: string): HttpHeaders {
    const session = this.sessions.get(this.sessionKey(projectId, network));
    return session ? new HttpHeaders({ 'X-Pusharoo-Webhook-Session': session }) : new HttpHeaders();
  }

  private sessionKey(projectId: string, network: string): string {
    return `${network}\n${projectId.trim()}`;
  }

  private readResponse<T>(projectId: string, network: string, response: { headers: HttpHeaders; body: T | null }): T {
    const session = response.headers.get('X-Pusharoo-Webhook-Session');
    if (session) {
      this.sessions.set(this.sessionKey(projectId, network), session);
    }

    return response.body as T;
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
