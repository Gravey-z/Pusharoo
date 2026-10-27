import { Injectable } from '@angular/core';
import { Deployment } from '../models/pusharoo.models';

type AttemptReference = Pick<
  Deployment,
  'id' | 'projectId' | 'contractId' | 'network' | 'deployedBy' | 'legacyContractMappingVerified'
>;

@Injectable({ providedIn: 'root' })
export class DeploymentAttemptCapabilityService {
  private readonly prefix = 'pusharoo.deployment-attempt-capability.';

  matchesParent(deployment: AttemptReference): boolean {
    const parent = JSON.stringify({
      projectId: deployment.projectId,
      contractId: deployment.contractId ?? null,
      network: deployment.network,
      deployedBy: deployment.deployedBy
    });
    try {
      const key = `${this.prefix}${deployment.id}.parent`;
      const stored = localStorage.getItem(key);
      if (stored !== null && stored !== parent) {
        const legacy = JSON.parse(stored) as {
          projectId: string;
          contractId?: string | null;
          network: string;
          deployedBy: string;
        };
        if (
          !deployment.legacyContractMappingVerified ||
          legacy.contractId != null ||
          !deployment.contractId ||
          legacy.projectId !== deployment.projectId ||
          legacy.network !== deployment.network ||
          legacy.deployedBy !== deployment.deployedBy
        )
          return false;
        localStorage.setItem(key, parent);
      }
      if (stored === null) localStorage.setItem(key, parent);
      return true;
    } catch {
      return false;
    }
  }

  assertParent(expected: AttemptReference, received: AttemptReference): void {
    if (
      expected.id !== received.id ||
      expected.projectId !== received.projectId ||
      (expected.contractId ?? null) !== (received.contractId ?? null) ||
      expected.network !== received.network ||
      expected.deployedBy !== received.deployedBy ||
      !this.matchesParent(expected) ||
      !this.matchesParent(received)
    ) {
      throw new Error(
        'The deployment response does not match the selected project, contract, network, or wallet.'
      );
    }
  }

  get(deployment: AttemptReference): string | null {
    if (!this.matchesParent(deployment)) return null;
    try {
      return sessionStorage.getItem(`${this.prefix}${deployment.id}`);
    } catch {
      return null;
    }
  }

  set(deployment: AttemptReference, capability: string): void {
    this.assertParent(deployment, deployment);
    try {
      sessionStorage.setItem(`${this.prefix}${deployment.id}`, capability);
    } catch {}
  }

  getTransactionId(deployment: AttemptReference): string | null {
    if (!this.matchesParent(deployment)) return null;
    try {
      return localStorage.getItem(`${this.prefix}${deployment.id}.transaction`);
    } catch {
      return null;
    }
  }

  setTransactionId(deployment: AttemptReference, transactionId: string): void {
    this.assertParent(deployment, deployment);
    const existing = this.getTransactionId(deployment);
    if (existing && !this.matchesTransaction(existing, transactionId)) {
      throw new Error('A different transaction is already saved for this deployment attempt.');
    }
    localStorage.setItem(`${this.prefix}${deployment.id}.transaction`, transactionId);
  }

  setBroadcastPending(deployment: AttemptReference): void {
    this.assertParent(deployment, deployment);
    localStorage.setItem(`${this.prefix}${deployment.id}.broadcast-unknown`, 'true');
  }

  hasUnknownBroadcast(
    deployment: AttemptReference & Pick<Deployment, 'broadcastOutcomeUnknown'>
  ): boolean {
    if (!this.matchesParent(deployment)) return true;
    try {
      return (
        Boolean(deployment.broadcastOutcomeUnknown) ||
        localStorage.getItem(`${this.prefix}${deployment.id}.broadcast-unknown`) === 'true'
      );
    } catch {
      return true;
    }
  }

  complete(deployment: Deployment): void {
    this.assertParent(deployment, deployment);
    const saved = this.getTransactionId(deployment);
    if (
      deployment.status !== 'confirmed' ||
      !deployment.transactionId ||
      (saved && !this.matchesTransaction(saved, deployment.transactionId))
    ) {
      throw new Error(
        'Confirmation did not match the saved transaction. Recovery information was retained.'
      );
    }
    this.clear(deployment);
  }

  discardUnbroadcast(deployment: Deployment): void {
    if (
      !this.matchesParent(deployment) ||
      deployment.status !== 'failed' ||
      deployment.broadcastTrackingVersion !== 1 ||
      deployment.transactionId ||
      this.getTransactionId(deployment) ||
      this.hasUnknownBroadcast(deployment)
    )
      return;
    this.clear(deployment);
  }

  matchesTransaction(left: string | null | undefined, right: string | null | undefined): boolean {
    return Boolean(
      left &&
        right &&
        left.toLowerCase().replace(/^0x/, '') === right.toLowerCase().replace(/^0x/, '')
    );
  }

  private clear(deployment: AttemptReference): void {
    try {
      sessionStorage.removeItem(`${this.prefix}${deployment.id}`);
    } catch {}
    try {
      localStorage.removeItem(`${this.prefix}${deployment.id}.transaction`);
      localStorage.removeItem(`${this.prefix}${deployment.id}.broadcast-unknown`);
      localStorage.removeItem(`${this.prefix}${deployment.id}.parent`);
    } catch {}
  }
}
