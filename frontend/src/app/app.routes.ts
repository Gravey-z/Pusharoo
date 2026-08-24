import { Routes } from '@angular/router';
import { ArtifactCompareComponent } from './pages/artifact-compare/artifact-compare.component';
import { ArtifactUploadComponent } from './pages/artifact-upload/artifact-upload.component';
import { ContractConsoleComponent } from './pages/contract-console/contract-console.component';
import { DeploymentCreateComponent } from './pages/deployment-create/deployment-create.component';
import { DeploymentRecoveryComponent } from './pages/deployment-recovery/deployment-recovery.component';
import { EventWebhooksComponent } from './pages/event-webhooks/event-webhooks.component';
import { ManifestViewerComponent } from './pages/manifest-viewer/manifest-viewer.component';
import { LandingComponent } from './pages/landing/landing.component';
import { ProjectOverviewComponent } from './pages/project-overview/project-overview.component';
import { ProjectDeleteComponent } from './pages/project-delete/project-delete.component';
import { ProjectsComponent } from './pages/projects/projects.component';
import { ProjectWorkspaceComponent } from './pages/project-workspace/project-workspace.component';

export const routes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'home' },
  { path: 'home', component: LandingComponent },
  { path: 'projects', component: ProjectsComponent },
  { path: 'projects/:projectId/delete', component: ProjectDeleteComponent },
  { path: 'projects/:projectId/upload', component: ArtifactUploadComponent },
  { path: 'projects/:projectId/compare', component: ArtifactCompareComponent },
  { path: 'projects/:projectId/deployments/new', component: DeploymentCreateComponent },
  { path: 'projects/:projectId/deployments/recovery', component: DeploymentRecoveryComponent },
  {
    path: 'projects/:projectId',
    component: ProjectWorkspaceComponent,
    children: [
      { path: '', component: ProjectOverviewComponent },
      { path: 'artifacts', component: ProjectOverviewComponent, data: { releaseTab: 'artifacts' } },
      { path: 'deployments', component: ProjectOverviewComponent, data: { releaseTab: 'deployments' } },
      { path: 'console', component: ContractConsoleComponent },
      { path: 'webhooks', component: EventWebhooksComponent }
    ]
  },
  { path: 'artifacts/:artifactId/manifest', component: ManifestViewerComponent },
  { path: '**', redirectTo: 'projects' }
];
