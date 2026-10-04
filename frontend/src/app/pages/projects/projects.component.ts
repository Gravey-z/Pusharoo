import { Component, OnInit } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { ProjectListItem } from '../../models/pusharoo.models';
import { PusharooApiService } from '../../services/pusharoo-api.service';
import { ApiErrorFormatterService } from '../../services/api-error-formatter.service';
import { WalletService } from '../../services/wallet.service';
import { WalletAuthService } from '../../services/wallet-auth.service';
import { PageShellComponent } from '../page-shell/page-shell.component';

@Component({
  selector: 'app-projects',
  imports: [FormsModule, PageShellComponent, RouterLink],
  templateUrl: './projects.component.html',
  styleUrl: './projects.component.scss'
})
export class ProjectsComponent implements OnInit {
  projects: ProjectListItem[] = [];
  isLoading = true;
  loadError = '';
  isCreating = false;
  isSaving = false;
  newProjectName = '';
  newProjectDescription = '';
  errorMessage = '';
  searchTerm = '';
  page = 1;
  readonly pageSize = 9;
  private createAttempt: { payload: string; key: string } | null = null;

  constructor(
    private readonly api: PusharooApiService,
    private readonly errors: ApiErrorFormatterService,
    private readonly auth: WalletAuthService,
    readonly wallet: WalletService
  ) {}

  ngOnInit(): void {
    this.loadProjects();
  }

  openCreateProject(): void {
    this.isCreating = true;
    this.errorMessage = '';
  }

  cancelCreateProject(): void {
    this.createAttempt = null;
    this.isCreating = false;
    this.newProjectName = '';
    this.newProjectDescription = '';
    this.errorMessage = '';
  }

  async createProject(): Promise<void> {
    const name = this.newProjectName.trim();
    if (!name) {
      this.errorMessage = 'Project name is required.';
      return;
    }

    if (!this.wallet.account()) {
      this.errorMessage = 'Connect a wallet before creating a project.';
      return;
    }

    this.isSaving = true;
    this.errorMessage = '';

    try {
      await this.auth.ensureAuthenticated();
      const network = this.wallet.session()?.network;
      if (!network) throw new Error('Connect a wallet before creating a project.');
      const payload = JSON.stringify([name, this.newProjectDescription.trim(), network, this.wallet.account()?.address]);
      if (this.createAttempt?.payload !== payload) {
        this.createAttempt = { payload, key: crypto.randomUUID() };
      }
      await firstValueFrom(this.api.createProject(name, this.newProjectDescription, network, this.createAttempt.key));
      this.cancelCreateProject();
      this.loadProjects();
    } catch (error) {
      this.errorMessage = this.errors.format(error, 'Could not create project.');
    } finally {
      this.isSaving = false;
    }
  }

  deploymentNetworkSummary(item: ProjectListItem): string {
    return item.deploymentNetworks.length > 0 ? item.deploymentNetworks.join(', ') : 'Not deployed';
  }

  networkLabel(network: string): string {
    return network === 'neo3:mainnet' ? 'N3:Mainnet' : network === 'neo3:testnet' ? 'N3:Testnet' : network;
  }

  visibleProjects(projects: ProjectListItem[]): ProjectListItem[] {
    const query = this.searchTerm.trim().toLowerCase();
    const filtered = projects.filter((item) => !query || [item.project.name, item.project.description ?? '']
      .some((value) => value.toLowerCase().includes(query)));
    const lastIndex = this.page * this.pageSize;

    return filtered.slice(lastIndex - this.pageSize, lastIndex);
  }

  totalPages(projects: ProjectListItem[]): number {
    const query = this.searchTerm.trim().toLowerCase();
    const count = projects.filter((item) => !query || [item.project.name, item.project.description ?? '']
      .some((value) => value.toLowerCase().includes(query))).length;
    return Math.max(1, Math.ceil(count / this.pageSize));
  }

  updateSearch(): void {
    this.page = 1;
  }

  changePage(projects: ProjectListItem[], direction: number): void {
    this.page = Math.min(Math.max(1, this.page + direction), this.totalPages(projects));
  }

  creatorSummary(item: ProjectListItem): string {
    const address = item.project.createdByWalletAddress;

    return address ? `${address.slice(0, 6)}...${address.slice(-4)}` : 'Legacy';
  }

  loadProjects(): void {
    this.isLoading = true;
    this.loadError = '';
    this.api.getProjectCards().subscribe({
      next: (projects) => {
        this.projects = projects;
        this.isLoading = false;
      },
      error: (error) => {
        this.projects = [];
        this.loadError = this.errors.format(error, 'Could not load projects.');
        this.isLoading = false;
      }
    });
  }
}
