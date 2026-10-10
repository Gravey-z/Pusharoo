import { Routes } from '@angular/router';
import { projectDeploymentAccessGuard } from './guards/project-deployment-access.guard';

export const routes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'home' },
  {
    path: 'home',
    loadComponent: () => import('./pages/landing/landing.component').then((component) => component.LandingComponent)
  },
  {
    path: 'projects',
    loadComponent: () => import('./pages/projects/projects.component').then((component) => component.ProjectsComponent)
  },
  {
    path: 'faucet',
    loadComponent: () => import('./pages/faucet/faucet.component').then((component) => component.FaucetComponent)
  },
  {
    path: 'projects/:projectId/delete',
    loadComponent: () => import('./pages/project-delete/project-delete.component').then((component) => component.ProjectDeleteComponent)
  },
  {
    path: 'projects/:projectId/upload',
    loadComponent: () => import('./pages/artifact-upload/artifact-upload.component').then((component) => component.ArtifactUploadComponent)
  },
  {
    path: 'projects/:projectId/compare',
    loadComponent: () => import('./pages/artifact-compare/artifact-compare.component').then((component) => component.ArtifactCompareComponent)
  },
  {
    path: 'projects/:projectId/deployments/new',
    canActivate: [projectDeploymentAccessGuard],
    loadComponent: () => import('./pages/deployment-create/deployment-create.component').then((component) => component.DeploymentCreateComponent)
  },
  {
    path: 'projects/:projectId/deployments/recovery',
    loadComponent: () => import('./pages/deployment-recovery/deployment-recovery.component').then((component) => component.DeploymentRecoveryComponent)
  },
  {
    path: 'projects/:projectId',
    data: { preload: true },
    loadComponent: () => import('./pages/project-workspace/project-workspace.component').then((component) => component.ProjectWorkspaceComponent),
    children: [
      {
        path: '',
        data: { preload: true },
        loadComponent: () => import('./pages/project-overview/project-overview.component').then((component) => component.ProjectOverviewComponent)
      },
      {
        path: 'artifacts',
        loadComponent: () => import('./pages/project-overview/project-overview.component').then((component) => component.ProjectOverviewComponent),
        data: { releaseTab: 'artifacts', preload: true }
      },
      {
        path: 'deployments',
        loadComponent: () => import('./pages/project-overview/project-overview.component').then((component) => component.ProjectOverviewComponent),
        data: { releaseTab: 'deployments', preload: true }
      },
      {
        path: 'authorized-deployers',
        data: { preload: true },
        loadComponent: () => import('./pages/project-authorized-deployers/project-authorized-deployers.component').then((component) => component.ProjectAuthorizedDeployersComponent)
      },
      {
        path: 'console',
        data: { preload: true },
        loadComponent: () => import('./pages/contract-console/contract-console.component').then((component) => component.ContractConsoleComponent)
      },
      {
        path: 'webhooks',
        data: { preload: true },
        loadComponent: () => import('./pages/event-webhooks/event-webhooks.component').then((component) => component.EventWebhooksComponent)
      }
    ]
  },
  {
    path: 'artifacts/:artifactId/manifest',
    loadComponent: () => import('./pages/manifest-viewer/manifest-viewer.component').then((component) => component.ManifestViewerComponent)
  },
  { path: '**', redirectTo: 'projects' }
];
