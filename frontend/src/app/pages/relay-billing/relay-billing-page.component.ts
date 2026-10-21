import { Component, OnInit } from '@angular/core';
import { RouterLink } from '@angular/router';
import { Project } from '../../models/pusharoo.models';
import { RelayBillingComponent } from '../../components/relay-billing/relay-billing.component';
import { ApiErrorFormatterService } from '../../services/api-error-formatter.service';
import { ProjectOwnershipService } from '../../services/project-ownership.service';
import { ProjectWorkspaceContextService } from '../../services/project-workspace-context.service';
import { WalletAuthService } from '../../services/wallet-auth.service';
import { WalletService } from '../../services/wallet.service';

@Component({
  selector: 'app-relay-billing-page',
  imports: [RouterLink, RelayBillingComponent],
  templateUrl: './relay-billing-page.component.html',
  styleUrl: './relay-billing-page.component.scss'
})
export class RelayBillingPageComponent implements OnInit {
  connecting = false;
  error = '';

  constructor(
    private readonly workspace: ProjectWorkspaceContextService,
    private readonly ownership: ProjectOwnershipService,
    private readonly errors: ApiErrorFormatterService,
    readonly auth: WalletAuthService,
    readonly wallet: WalletService
  ) {}

  ngOnInit(): void {
    void this.auth.restore();
  }

  get project(): Project | null {
    return this.workspace.overview?.project ?? null;
  }

  get isOwner(): boolean {
    return this.ownership.canManage(this.project, this.wallet.account()?.address ?? '');
  }

  get connected(): boolean {
    return this.isOwner && this.auth.canUseSession();
  }

  async connect(): Promise<void> {
    if (!this.isOwner || this.connecting) return;

    this.connecting = true;
    this.error = '';

    try {
      await this.auth.ensureAuthenticated();
    } catch (error) {
      this.error = this.errors.format(error, 'Could not open Relay billing.');
    } finally {
      this.connecting = false;
    }
  }
}
