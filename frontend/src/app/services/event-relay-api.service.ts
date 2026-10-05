import { HttpClient, HttpContext } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import {
  CreateWebhookSubscriptionRequest,
  EventRelayStatus,
  RelayPayment,
  RelayPaymentHistory,
  RelayPaymentIntent,
  RelayUsage,
  WebhookDelivery,
  WebhookSubscription
} from '../models/pusharoo.models';
import { RuntimeConfigService } from './runtime-config.service';
import { REQUIRE_WALLET_SESSION } from './wallet-auth.interceptor';

@Injectable({ providedIn: 'root' })
export class EventRelayApiService {
  constructor(private readonly http: HttpClient, private readonly runtimeConfig: RuntimeConfigService) {}

  getEventRelayStatus(network: string): Observable<EventRelayStatus> {
    return this.http.get<EventRelayStatus>(this.runtimeConfig.value.eventRelays?.[network]?.healthUrl
      ?? this.runtimeConfig.value.eventRelayHealthUrl);
  }

  getWebhookSubscriptions(projectId: string, network: string): Observable<WebhookSubscription[]> {
    return this.http.post<WebhookSubscription[]>(`${this.baseUrl(projectId, network)}/subscriptions/query`, null, this.options());
  }

  getRelayUsage(projectId: string, network: string): Observable<RelayUsage> {
    return this.http.post<RelayUsage>(`${this.baseUrl(projectId, network)}/subscriptions/usage`, null, this.options());
  }

  createRelayPaymentIntent(projectId: string): Observable<RelayPaymentIntent> {
    return this.http.post<RelayPaymentIntent>(`${this.baseUrl(projectId, 'neo3:mainnet')}/payments/intents`, null, this.options());
  }

  confirmRelayPayment(projectId: string, intentId: string, transactionId: string): Observable<RelayPayment> {
    return this.http.post<RelayPayment>(`${this.baseUrl(projectId, 'neo3:mainnet')}/payments/confirm`,
      { intentId, transactionId }, this.options());
  }

  getRelayPaymentHistory(projectId: string): Observable<RelayPaymentHistory> {
    return this.http.post<RelayPaymentHistory>(`${this.baseUrl(projectId, 'neo3:mainnet')}/payments/history/query`,
      null, this.options());
  }

  createWebhookSubscription(projectId: string, network: string,
    request: CreateWebhookSubscriptionRequest): Observable<WebhookSubscription> {
    const { projectId: ignoredProjectId, ...subscription } = request;
    return this.http.post<WebhookSubscription>(`${this.baseUrl(projectId, network)}/subscriptions`, subscription, this.options());
  }

  updateWebhookSubscription(projectId: string, network: string, subscriptionId: string,
    request: CreateWebhookSubscriptionRequest): Observable<WebhookSubscription> {
    const { projectId: ignoredProjectId, ...subscription } = request;
    return this.http.put<WebhookSubscription>(
      `${this.baseUrl(projectId, network)}/subscriptions/${encodeURIComponent(subscriptionId)}`,
      subscription, this.options());
  }

  deleteWebhookSubscription(projectId: string, network: string, subscriptionId: string): Observable<void> {
    return this.http.delete<void>(`${this.baseUrl(projectId, network)}/subscriptions/${encodeURIComponent(subscriptionId)}`,
      this.options());
  }

  getWebhookDeliveries(projectId: string, network: string, subscriptionId: string): Observable<WebhookDelivery[]> {
    return this.http.post<WebhookDelivery[]>(
      `${this.baseUrl(projectId, network)}/subscriptions/${encodeURIComponent(subscriptionId)}/deliveries/query`,
      null, this.options());
  }

  sendWebhookTest(projectId: string, network: string, subscriptionId: string): Observable<WebhookDelivery> {
    return this.http.post<WebhookDelivery>(
      `${this.baseUrl(projectId, network)}/subscriptions/${encodeURIComponent(subscriptionId)}/test`,
      null, this.options());
  }

  redeliverWebhook(projectId: string, network: string, subscriptionId: string, deliveryId: string): Observable<WebhookDelivery> {
    return this.http.post<WebhookDelivery>(
      `${this.baseUrl(projectId, network)}/subscriptions/${encodeURIComponent(subscriptionId)}/deliveries/${encodeURIComponent(deliveryId)}/redeliver`,
      null, this.options());
  }

  private baseUrl(projectId: string, network: string): string {
    const segment = network === 'neo3:testnet' ? 'testnet' : network === 'neo3:mainnet' ? 'mainnet' : null;
    if (!segment) throw new Error('Unsupported Relay network.');
    return `${this.runtimeConfig.value.apiBaseUrl.replace(/\/$/, '')}/projects/${encodeURIComponent(projectId)}/relay/${segment}`;
  }

  private options(): { context: HttpContext } {
    return { context: new HttpContext().set(REQUIRE_WALLET_SESSION, true) };
  }
}
