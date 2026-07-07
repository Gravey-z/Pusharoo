import { Routes } from '@angular/router';
import { ArtifactCompareComponent } from './pages/artifact-compare/artifact-compare.component';
import { ArtifactUploadComponent } from './pages/artifact-upload/artifact-upload.component';
import { ContractConsoleComponent } from './pages/contract-console/contract-console.component';
import { DeploymentCreateComponent } from './pages/deployment-create/deployment-create.component';
import { EventWebhooksComponent } from './pages/event-webhooks/event-webhooks.component';
import { ManifestViewerComponent } from './pages/manifest-viewer/manifest-viewer.component';
import { LandingComponent } from './pages/landing/landing.component';
import { ProjectOverviewComponent } from './pages/project-overview/project-overview.component';
import { ProjectsComponent } from './pages/projects/projects.component';

export const routes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'home' },
  { path: 'home', component: LandingComponent },
  { path: 'projects', component: ProjectsComponent },
  { path: 'projects/:projectId', component: ProjectOverviewComponent },
  { path: 'projects/:projectId/upload', component: ArtifactUploadComponent },
  { path: 'projects/:projectId/compare', component: ArtifactCompareComponent },
  { path: 'projects/:projectId/deployments/new', component: DeploymentCreateComponent },
  { path: 'projects/:projectId/console', component: ContractConsoleComponent },
  { path: 'projects/:projectId/webhooks', component: EventWebhooksComponent },
  { path: 'artifacts/:artifactId/manifest', component: ManifestViewerComponent },
  { path: '**', redirectTo: 'projects' }
];
