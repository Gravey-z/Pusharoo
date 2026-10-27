import { Component, DestroyRef, inject, OnInit } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, RouterLink, Params } from '@angular/router';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { firstValueFrom } from 'rxjs';
import { ProjectWorkspaceSummary } from '../../models/pusharoo.models';
import { PusharooApiService } from '../../services/pusharoo-api.service';
import { ProjectOwnershipService } from '../../services/project-ownership.service';
import { WalletService } from '../../services/wallet.service';
import { WalletAuthService } from '../../services/wallet-auth.service';
import { ApiErrorFormatterService } from '../../services/api-error-formatter.service';
import { contractLink, workspaceRoute } from '../../services/workspace-route';

@Component({
  selector: 'app-contracts',
  imports: [FormsModule, RouterLink],
  templateUrl: './contracts.component.html',
  styleUrl: './contracts.component.scss'
})
export class ContractsComponent implements OnInit {
  summary: ProjectWorkspaceSummary | null = null;
  destination = '';
  destinationQuery: Params = {};
  search = '';
  page = 1;
  readonly pageSize = 12;
  name = '';
  creating = false;
  saving = false;
  loading = true;
  error = '';
  private projectId = '';
  private generation = 0;
  private createAttempt: { name: string; key: string } | null = null;
  private readonly destroyRef = inject(DestroyRef);

  constructor(
    private readonly route: ActivatedRoute,
    private readonly api: PusharooApiService,
    private readonly ownership: ProjectOwnershipService,
    readonly wallet: WalletService,
    private readonly auth: WalletAuthService,
    private readonly errors: ApiErrorFormatterService
  ) {
    this.destroyRef.onDestroy(() => this.generation++);
  }

  ngOnInit(): void {
    this.route.queryParamMap.pipe(takeUntilDestroyed(this.destroyRef)).subscribe(query => {
      const destination = query.get('contractDestination') ?? '';
      const destinations = [
        'artifacts',
        'upload',
        'compare',
        'deployments',
        'deployments/new',
        'deployments/recovery',
        'console'
      ];
      this.destination = destinations.includes(destination) ? destination : '';
      this.destinationQuery = Object.fromEntries(
        query.keys
          .filter(key => key !== 'contractDestination' && key !== 'contractId')
          .map(key => [key, query.getAll(key)])
      );
    });
    workspaceRoute(this.route)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(scope => {
        this.projectId = scope.projectId;
        this.search = '';
        this.page = 1;
        this.name = '';
        this.creating = false;
        this.saving = false;
        this.createAttempt = null;
        this.load();
      });
  }

  get destinationLabel(): string {
    const labels: Record<string, string> = {
      artifacts: 'artifacts',
      upload: 'upload an artifact',
      compare: 'compare versions',
      deployments: 'deployment history',
      'deployments/new': 'deploy a contract',
      'deployments/recovery': 'recover a deployment',
      console: 'the contract console'
    };
    return labels[this.destination] ?? '';
  }

  contractDestination(contractId: string): string[] {
    return contractLink(this.projectId, contractId, ...this.destination.split('/').filter(Boolean));
  }

  get canManage(): boolean {
    return this.ownership.canManage(this.summary?.project, this.wallet.account()?.address ?? '');
  }

  get filteredContracts() {
    const query = this.search.trim().toLowerCase();
    return (this.summary?.contracts ?? []).filter(item =>
      item.contract.name.toLowerCase().includes(query)
    );
  }

  get pages(): number {
    return Math.max(1, Math.ceil(this.filteredContracts.length / this.pageSize));
  }
  get visibleContracts() {
    return this.filteredContracts.slice((this.page - 1) * this.pageSize, this.page * this.pageSize);
  }

  currentDeployment(item: ProjectWorkspaceSummary['contracts'][number], network: string) {
    return item.currentDeployments.find(deployment => deployment.network === network) ?? null;
  }

  contractName(contractId: string | null | undefined): string {
    return (
      this.summary?.contracts.find(item => item.contract.id === contractId)?.contract.name ??
      'Unlinked legacy contract'
    );
  }

  load(): void {
    const generation = ++this.generation;
    this.loading = true;
    this.error = '';
    this.summary = null;
    this.api.getProjectSummary(this.projectId).subscribe({
      next: summary => {
        if (generation !== this.generation) return;
        this.summary = summary;
        this.loading = false;
      },
      error: error => {
        if (generation !== this.generation) return;
        this.error = this.errors.format(error, 'Could not load contracts.');
        this.loading = false;
      }
    });
  }

  async create(): Promise<void> {
    if (!this.canManage || this.saving || !this.name.trim()) return;
    const projectId = this.projectId;
    const generation = this.generation;
    const name = this.name.trim();
    this.saving = true;
    this.error = '';
    if (this.createAttempt?.name !== name) this.createAttempt = { name, key: crypto.randomUUID() };
    const key = this.createAttempt.key;
    try {
      await this.auth.ensureAuthenticated();
      if (generation !== this.generation) return;
      await firstValueFrom(this.api.createContract(projectId, name, key));
      if (generation !== this.generation) return;
      this.name = '';
      this.creating = false;
      this.createAttempt = null;
      this.saving = false;
      this.load();
    } catch (error) {
      if (generation === this.generation)
        this.error = this.errors.format(error, 'Could not create contract.');
    } finally {
      if (generation === this.generation) this.saving = false;
    }
  }
}
