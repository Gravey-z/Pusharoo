import { Routes } from '@angular/router';

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
    loadComponent: () => import('./pages/deployment-create/deployment-create.component').then((component) => component.DeploymentCreateComponent)
  },
  {
    path: 'projects/:projectId/deployments/recovery',
    loadComponent: () => import('./pages/deployment-recovery/deployment-recovery.component').then((component) => component.DeploymentRecoveryComponent)
  },
  {
    path: 'projects/:projectId',
    loadComponent: () => import('./pages/project-workspace/project-workspace.component').then((component) => component.ProjectWorkspaceComponent),
    children: [
      {
        path: '',
        loadComponent: () => import('./pages/project-overview/project-overview.component').then((component) => component.ProjectOverviewComponent)
      },
      {
        path: 'artifacts',
        loadComponent: () => import('./pages/project-overview/project-overview.component').then((component) => component.ProjectOverviewComponent),
        data: { releaseTab: 'artifacts' }
      },
      {
        path: 'deployments',
        loadComponent: () => import('./pages/project-overview/project-overview.component').then((component) => component.ProjectOverviewComponent),
        data: { releaseTab: 'deployments' }
      },
      {
        path: 'console',
        loadComponent: () => import('./pages/contract-console/contract-console.component').then((component) => component.ContractConsoleComponent)
      },
      {
        path: 'webhooks',
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
