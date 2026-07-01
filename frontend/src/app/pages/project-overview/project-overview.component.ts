import { Component, OnInit } from '@angular/core';
import { AsyncPipe } from '@angular/common';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { Observable, map, switchMap } from 'rxjs';
import { Artifact, Deployment, ProjectOverviewViewModel } from '../../models/pusharoo.models';
import { ClipboardService } from '../../services/clipboard.service';
import { DeploymentHistoryService } from '../../services/deployment-history.service';
import { PusharooApiService } from '../../services/pusharoo-api.service';
import { PageShellComponent } from '../page-shell/page-shell.component';

@Component({
  selector: 'app-project-overview',
  imports: [AsyncPipe, PageShellComponent, RouterLink],
  templateUrl: './project-overview.component.html',
  styleUrl: './project-overview.component.scss'
})
export class ProjectOverviewComponent implements OnInit {
  overview$!: Observable<ProjectOverviewViewModel | null>;
  copiedValue = '';

  constructor(
    private readonly route: ActivatedRoute,
    private readonly api: PusharooApiService,
    private readonly clipboard: ClipboardService,
    private readonly deploymentHistory: DeploymentHistoryService
  ) {}

  ngOnInit(): void {
    this.overview$ = this.route.paramMap.pipe(
      map((params) => params.get('projectId') ?? ''),
      switchMap((projectId) => this.api.getProjectOverview(projectId))
    );
  }

  artifactDeployments(overview: ProjectOverviewViewModel, artifact: Artifact): Deployment[] {
    return this.deploymentHistory.latestForArtifact(overview, artifact);
  }

  artifactNetworks(overview: ProjectOverviewViewModel, artifact: Artifact): string[] {
    return this.deploymentHistory.networksForLatestArtifact(overview, artifact);
  }

  isArtifactDeployed(overview: ProjectOverviewViewModel, artifact: Artifact): boolean {
    return this.artifactDeployments(overview, artifact).length > 0;
  }

  hasWebhookTarget(overview: ProjectOverviewViewModel): boolean {
    return overview.deployments.some((deployment) => Boolean(deployment.contractHash));
  }

  shortText(value: string | null | undefined, leading = 3, trailing = 4): string {
    if (!value) {
      return '-';
    }

    if (value.length <= leading + trailing + 3) {
      return value;
    }

    return `${value.slice(0, leading)}...${value.slice(-trailing)}`;
  }

  shortTransactionId(value: string | null | undefined): string {
    return value ? this.shortText(value, 10, 4) : 'No txid';
  }

  async copyValue(value: string | null | undefined, event: Event): Promise<void> {
    event.preventDefault();
    event.stopPropagation();

    if (!value) {
      return;
    }

    await this.clipboard.copy(value);
    this.copiedValue = value;
    window.setTimeout(() => {
      if (this.copiedValue === value) {
        this.copiedValue = '';
      }
    }, 1400);
  }
}
