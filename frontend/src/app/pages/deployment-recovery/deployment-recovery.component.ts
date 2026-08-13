import { Component, OnInit, computed } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { Artifact, ProjectOverviewViewModel } from '../../models/pusharoo.models';
import { ProjectOwnershipService } from '../../services/project-ownership.service';
import { PusharooApiService } from '../../services/pusharoo-api.service';
import { ApiErrorFormatterService } from '../../services/api-error-formatter.service';
import { WalletService } from '../../services/wallet.service';
import { PageShellComponent } from '../page-shell/page-shell.component';
import { ProjectReleaseNavComponent } from '../../components/project-release-nav/project-release-nav.component';

@Component({
  selector: 'app-deployment-recovery',
  imports: [FormsModule, PageShellComponent, ProjectReleaseNavComponent, RouterLink],
  templateUrl: './deployment-recovery.component.html',
  styleUrl: './deployment-recovery.component.scss'
})
export class DeploymentRecoveryComponent implements OnInit {
  overview: ProjectOverviewViewModel | null = null;
  artifacts: Artifact[] = [];
  artifactId = '';
  transactionId = '';
  notes = '';
  errorMessage = '';
  statusMessage = '';
  isRecovering = false;
  readonly projectId: string;
  readonly walletAddress = computed(() => this.wallet.account()?.address ?? '');
  readonly walletNetwork = computed(() => this.wallet.session()?.network ?? '');

  get pageTitle(): string {
    return this.overview ? `${this.overview.project.name}: Recover Deployment` : 'Recover Deployment';
  }

  get canManageProject(): boolean {
    return this.ownership.canManage(this.overview?.project, this.walletAddress());
  }

  constructor(
    private readonly route: ActivatedRoute,
    private readonly router: Router,
    private readonly api: PusharooApiService,
    private readonly errors: ApiErrorFormatterService,
    private readonly ownership: ProjectOwnershipService,
    readonly wallet: WalletService
  ) {
    this.projectId = this.route.snapshot.paramMap.get('projectId') ?? '';
  }

  ngOnInit(): void {
    this.api.getProjectOverview(this.projectId).subscribe((overview) => {
      this.overview = overview;
      this.artifacts = overview?.artifacts ?? [];
      this.artifactId = this.artifacts[0]?.id ?? '';
    });
  }

  async recover(): Promise<void> {
    this.errorMessage = '';
    this.statusMessage = '';

    if (!this.artifactId) {
      this.errorMessage = 'Choose the artifact version used by the transaction.';
      return;
    }

    if (!this.transactionId.trim()) {
      this.errorMessage = 'Enter the transaction ID to recover.';
      return;
    }

    const session = this.wallet.session();
    if (!session || !this.walletAddress()) {
      this.errorMessage = 'Connect the project owner wallet before recovering a deployment.';
      return;
    }

    const ownershipError = this.ownership.managementError(this.overview?.project, this.walletAddress());
    if (ownershipError) {
      this.errorMessage = ownershipError;
      return;
    }

    this.isRecovering = true;
    this.statusMessage = 'Verifying the transaction on Neo...';

    try {
      await firstValueFrom(this.api.recoverDeployment(this.projectId, {
        artifactId: this.artifactId,
        network: session.network,
        transactionId: this.transactionId.trim(),
        deployedBy: this.walletAddress(),
        notes: this.notes.trim() || null
      }));

      await this.router.navigate(['/projects', this.projectId, 'deployments']);
    } catch (error) {
      this.errorMessage = this.errors.format(error, 'Could not recover the deployment.');
    } finally {
      this.isRecovering = false;
      this.statusMessage = '';
    }
  }

}
