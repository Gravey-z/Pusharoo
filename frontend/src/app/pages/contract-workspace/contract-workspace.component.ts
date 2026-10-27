import { Component, DestroyRef, inject, OnInit } from '@angular/core';
import { FormsModule } from '@angular/forms';
import {
  ActivatedRoute,
  Router,
  RouterLink,
  RouterLinkActive,
  RouterOutlet
} from '@angular/router';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { firstValueFrom } from 'rxjs';
import { ProjectWorkspaceSummary, ProjectContract } from '../../models/pusharoo.models';
import { PusharooApiService } from '../../services/pusharoo-api.service';
import { ProjectOwnershipService } from '../../services/project-ownership.service';
import { WalletService } from '../../services/wallet.service';
import { WalletAuthService } from '../../services/wallet-auth.service';
import { ApiErrorFormatterService } from '../../services/api-error-formatter.service';
import { contractLink, workspaceRoute } from '../../services/workspace-route';

@Component({
  selector: 'app-contract-workspace',
  imports: [FormsModule, RouterLink, RouterLinkActive, RouterOutlet],
  templateUrl: './contract-workspace.component.html',
  styleUrl: './contract-workspace.component.scss'
})
export class ContractWorkspaceComponent implements OnInit {
  projectId = '';
  contractId: string | null = null;
  summary: ProjectWorkspaceSummary | null = null;
  contract: ProjectContract | null = null;
  rename = '';
  editing = false;
  saving = false;
  error = '';
  readonly tabs = [
    '',
    'artifacts',
    'upload',
    'compare',
    'deployments',
    'deployments/new',
    'deployments/recovery',
    'console'
  ];
  readonly labels = [
    'Overview',
    'Artifacts',
    'Upload',
    'Compare',
    'Deployments',
    'Deploy',
    'Recover',
    'Console'
  ];
  private generation = 0;
  private readonly destroyRef = inject(DestroyRef);

  constructor(
    private readonly route: ActivatedRoute,
    private readonly router: Router,
    private readonly api: PusharooApiService,
    private readonly ownership: ProjectOwnershipService,
    private readonly wallet: WalletService,
    private readonly auth: WalletAuthService,
    private readonly errors: ApiErrorFormatterService
  ) {
    this.destroyRef.onDestroy(() => this.generation++);
  }

  ngOnInit(): void {
    workspaceRoute(this.route)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(scope => {
        this.projectId = scope.projectId;
        this.contractId = scope.contractId;
        this.contract = null;
        this.summary = null;
        this.editing = false;
        this.saving = false;
        this.error = '';
        const generation = ++this.generation;
        this.api.getProjectSummary(this.projectId).subscribe({
          next: summary => {
            if (generation !== this.generation) return;
            this.summary = summary;
            this.contract =
              summary.contracts.find(item => item.contract.id === this.contractId)?.contract ??
              null;
            if (!this.contract) this.error = 'Contract was not found in this project.';
          },
          error: error => {
            if (generation === this.generation)
              this.error = this.errors.format(error, 'Could not load contract navigation.');
          }
        });
      });
  }

  get canManage(): boolean {
    return this.ownership.canManage(this.summary?.project, this.wallet.account()?.address ?? '');
  }
  link(tab: string): string[] {
    return contractLink(this.projectId, this.contractId, ...tab.split('/').filter(Boolean));
  }

  async selectContract(contractId: string): Promise<void> {
    const segments = this.route.firstChild?.snapshot.url.map(item => item.path) ?? [];
    await this.router.navigate(contractLink(this.projectId, contractId, ...segments));
  }

  async saveName(): Promise<void> {
    if (!this.canManage || !this.contractId || this.saving || !this.rename.trim()) return;
    const generation = this.generation;
    const projectId = this.projectId;
    const contractId = this.contractId;
    const name = this.rename.trim();
    this.saving = true;
    this.error = '';
    try {
      await this.auth.ensureAuthenticated();
      if (generation !== this.generation) return;
      const contract = await firstValueFrom(this.api.renameContract(projectId, contractId, name));
      if (generation !== this.generation) return;
      this.contract = contract;
      this.editing = false;
      if (this.summary)
        this.summary.contracts = this.summary.contracts.map(item =>
          item.contract.id === contractId ? { ...item, contract } : item
        );
    } catch (error) {
      if (generation === this.generation)
        this.error = this.errors.format(error, 'Could not rename contract.');
    } finally {
      if (generation === this.generation) this.saving = false;
    }
  }
}
