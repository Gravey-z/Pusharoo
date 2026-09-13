import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { catchError, forkJoin, map, of } from 'rxjs';
import { ProjectDeploymentAccessService } from '../services/project-deployment-access.service';
import { PusharooApiService } from '../services/pusharoo-api.service';
import { WalletService } from '../services/wallet.service';

// This is guidance only. The deployment page and backend must still recheck access,
// because a wallet, authorized-deployer permission, or selected network can change after navigation.
export const projectDeploymentAccessGuard: CanActivateFn = (route) => {
  const projectId = route.paramMap.get('projectId') ?? '';
  const wallet = inject(WalletService);
  const walletAddress = wallet.account()?.address;
  const walletNetwork = wallet.session()?.network;

  if (!projectId || !walletAddress || !walletNetwork) {
    return true;
  }

  const api = inject(PusharooApiService);
  const access = inject(ProjectDeploymentAccessService);
  const router = inject(Router);

  return forkJoin({
    overview: api.getProjectOverview(projectId),
    authorizedDeployers: api.getAuthorizedDeployers(projectId)
  }).pipe(
    map(({ overview, authorizedDeployers }) => access.canStartDeployment(
      access.resolve(overview.project, authorizedDeployers, walletAddress),
      walletNetwork
    ) || router.createUrlTree(
      ['/projects', projectId, 'authorized-deployers'],
      { queryParams: { deploymentAccess: 'required' } }
    )),
    // Do not treat a transient client-side lookup failure as authorization. The page
    // will show its normal retry state and the server still enforces every mutation.
    catchError(() => of(true))
  );
};
