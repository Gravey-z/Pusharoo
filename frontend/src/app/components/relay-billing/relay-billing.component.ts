import { Component, EventEmitter, Input, OnChanges, Output } from '@angular/core';
import { DatePipe, DecimalPipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import {
  Project,
  RelayPaymentIntent,
  RelayPayment,
  RelayPurchasedPeriod,
  RelayUsage,
  WebhookSubscription
} from '../../models/pusharoo.models';
import { RelayBillingStateService } from '../../services/relay-billing-state.service';

@Component({
  selector: 'app-relay-billing',
  imports: [DatePipe, DecimalPipe, FormsModule],
  providers: [RelayBillingStateService],
  templateUrl: './relay-billing.component.html',
  styleUrl: './relay-billing.component.scss'
})
export class RelayBillingComponent implements OnChanges {
  @Input() project: Project | null = null;
  @Input() connected = false;
  @Output() usageChanged = new EventEmitter<Record<string, RelayUsage>>();
  packageId = 'starter';
  operation: 'purchase_period' | 'upgrade_periods' = 'purchase_period';
  selectedPeriodIds: string[] = [];
  reviewed: Record<string, boolean> = {};
  recoveryHashes: Record<string, string> = {};
  noTransfer: Record<string, boolean> = {};
  selectionDrafts: Record<string, string[]> = {};

  constructor(readonly billing: RelayBillingStateService) {
    this.billing.onRefresh = usage => this.usageChanged.emit(usage);
  }

  ngOnChanges(): void {
    this.reviewed = {};
    this.recoveryHashes = {};
    this.noTransfer = {};
    this.selectedPeriodIds = [];
    this.selectionDrafts = {};
    this.billing.start(this.connected ? this.project : null);
  }

  get periods(): RelayPurchasedPeriod[] {
    return this.billing.history?.periods ?? [];
  }

  get enabledSubscriptions(): WebhookSubscription[] {
    return this.billing.subscriptions
      .filter(x => x.isEnabled && x.network === 'neo3:mainnet')
      .sort(
        (a, b) =>
          Date.parse(a.createdAt) - Date.parse(b.createdAt) ||
          (a.id < b.id ? -1 : a.id > b.id ? 1 : 0)
      );
  }

  get downgradePeriods(): RelayPurchasedPeriod[] {
    return this.periods.filter(period => {
      const predecessor = this.periods.find(x => x.endsAt === period.startsAt);
      return (
        Date.parse(period.startsAt) > this.billing.now &&
        period.effectiveTerms.packageId === 'starter' &&
        (predecessor?.effectiveTerms.maxActiveSubscriptions ??
          this.billing.usage['neo3:mainnet']?.maxActiveSubscriptions ??
          0) > period.effectiveTerms.maxActiveSubscriptions
      );
    });
  }

  get quoteDisabled(): boolean {
    const selected = this.billing.catalog?.packages.find(
      x => x.terms.packageId === (this.operation === 'upgrade_periods' ? 'pro' : this.packageId)
    );
    return (
      !this.billing.mainnetWallet ||
      !this.billing.ready ||
      this.billing.busy ||
      this.billing.loading ||
      !selected?.purchaseEnabled ||
      !this.billing.catalog?.billingReady ||
      this.billing.openIntent ||
      (this.operation === 'upgrade_periods' && !this.selectedPeriodIds.length)
    );
  }

  get estimatedUpgradeCost(): number {
    const packages = this.billing.catalog?.packages ?? [];
    const pro = packages.find(x => x.terms.packageId === 'pro')?.terms.gasDatoshis ?? 0;
    const starter = packages.find(x => x.terms.packageId === 'starter')?.terms.gasDatoshis ?? 0;
    return (pro - starter) * this.selectedPeriodIds.length;
  }

  async quote(): Promise<void> {
    const ids = this.selectedPeriodIds.filter(id =>
      this.periods.some(x => x.id === id && this.canUpgrade(x))
    );
    await this.billing.quote(
      this.operation === 'upgrade_periods' ? 'pro' : this.packageId,
      this.operation,
      this.operation === 'upgrade_periods' ? ids : []
    );
  }

  canUpgrade(period: RelayPurchasedPeriod): boolean {
    return (
      period.effectiveTerms.packageId === 'starter' &&
      Date.parse(period.endsAt) > this.billing.now &&
      !['legacy_carryover', 'legacy_custom'].includes(period.source)
    );
  }

  periodStatus(period: RelayPurchasedPeriod): string {
    if (Date.parse(period.endsAt) <= this.billing.now) return 'Completed';
    return Date.parse(period.startsAt) > this.billing.now ? 'Prepaid' : 'Current';
  }

  togglePeriod(id: string, selected: boolean): void {
    this.selectedPeriodIds = selected
      ? [...new Set([...this.selectedPeriodIds, id])]
      : this.selectedPeriodIds.filter(x => x !== id);
  }

  selections(period: RelayPurchasedPeriod): string[] {
    return (
      this.selectionDrafts[period.id] ??
      this.savedSelection(period.id) ??
      this.enabledSubscriptions
        .slice(0, period.effectiveTerms.maxActiveSubscriptions)
        .map(x => x.id)
    );
  }

  hasSavedSelection(periodId: string): boolean {
    return this.savedSelection(periodId) !== undefined;
  }

  toggleSubscription(period: RelayPurchasedPeriod, id: string, selected: boolean): void {
    const enabled = new Set(this.enabledSubscriptions.map(x => x.id));
    const ids = this.selections(period).filter(x => enabled.has(x));
    this.selectionDrafts[period.id] = selected
      ? [...new Set([...ids, id])]
      : ids.filter(x => x !== id);
  }

  async saveSelection(period: RelayPurchasedPeriod): Promise<void> {
    const enabled = new Set(this.enabledSubscriptions.map(x => x.id));
    await this.billing.saveSelection(
      period.id,
      this.selections(period).filter(x => enabled.has(x))
    );
  }

  quoteStarterPeriods(intent: RelayPaymentIntent): RelayPurchasedPeriod[] {
    const upgradedIds = new Set((intent.terms?.targets ?? []).map(x => x.periodId));
    if (intent.terms?.bonus) upgradedIds.add(intent.terms.bonus.target.periodId);
    return this.periods.filter(
      x =>
        Date.parse(x.startsAt) > this.billing.now &&
        x.effectiveTerms.packageId === 'starter' &&
        !upgradedIds.has(x.id)
    );
  }

  label(status: string): string {
    const labels: Record<string, string> = {
      pending: 'Awaiting payment',
      awaiting_finality: 'Awaiting Mainnet finality',
      applying: 'Applying access',
      reconciliation_required: 'Needs payment review',
      confirmed: 'Confirmed',
      resolved: 'Resolved',
      rejected: 'Rejected',
      expired: 'Expired',
      active: 'Active',
      grace: 'Grace period',
      accepted: 'Accepted',
      ineligible: 'Not applied',
      completed: 'Completed',
      not_applied: 'Not applied'
    };
    return labels[status] ?? status.replaceAll('_', ' ');
  }

  expired(intent: RelayPaymentIntent): boolean {
    return Date.parse(intent.expiresAt) <= this.billing.now;
  }

  otherUnapplied(payment: RelayPayment): number {
    const failedLines = (payment.allocations ?? [])
      .filter(x => x.status === 'ineligible')
      .reduce((sum, x) => sum + x.priceDatoshis, 0);
    return Math.max(0, (payment.unappliedGasDatoshis ?? 0) - failedLines);
  }

  gas(datoshis: number): string {
    return (datoshis / 100000000).toLocaleString('en-GB', { maximumFractionDigits: 8 });
  }

  private savedSelection(periodId: string): string[] | undefined {
    return this.billing.usage['neo3:mainnet']?.transitions?.selections.find(
      x => x.periodId === periodId
    )?.subscriptionIds;
  }
}
