import { HttpClient } from '@angular/common/http';
import { Injectable } from '@angular/core';
import type { NetworkType } from 'neo-n3-walletkit';
import { firstValueFrom } from 'rxjs';
import { walletConfig } from '../config/wallet.config';

interface RpcResponse<T> {
  result?: T;
  error?: {
    code: number;
    message: string;
  };
}

interface ApplicationLog {
  executions?: ApplicationLogExecution[];
}

interface ApplicationLogExecution {
  vmstate?: string;
  state?: string;
  exception?: string | null;
  notifications?: ApplicationLogNotification[];
  stack?: RpcStackItem[];
}

interface ApplicationLogNotification {
  contract?: string;
  eventname?: string;
  state?: RpcStackItem;
}

interface RpcStackItem {
  type?: string;
  value?: unknown;
}

export interface ContractParameter {
  type: string;
  value: unknown;
}

export interface ConfirmedDeployment {
  transactionId: string;
  vmState: string;
  contractHash: string;
}

export interface ContractInvokeResult {
  script?: string;
  state?: string;
  gasconsumed?: string;
  exception?: string | null;
  stack?: RpcStackItem[];
  notifications?: ApplicationLogNotification[];
}

@Injectable({ providedIn: 'root' })
export class NeoRpcService {
  constructor(private readonly http: HttpClient) {}

  async invokeFunction(
    network: NetworkType,
    contractHash: string,
    methodName: string,
    parameters: ContractParameter[]
  ): Promise<ContractInvokeResult> {
    const endpoint = walletConfig.rpc[network];

    if (!endpoint) {
      throw new Error(`No Neo RPC endpoint is configured for ${network}.`);
    }

    const response = await firstValueFrom(this.http.post<RpcResponse<ContractInvokeResult>>(endpoint, {
      jsonrpc: '2.0',
      method: 'invokefunction',
      params: [contractHash, methodName, parameters],
      id: Date.now()
    }));

    if (response.error) {
      throw new Error(response.error.message);
    }

    if (!response.result) {
      throw new Error('Neo RPC returned no invocation result.');
    }

    return response.result;
  }

  async waitForDeployment(
    network: NetworkType,
    transactionId: string,
    contractManagementHash: string
  ): Promise<ConfirmedDeployment> {
    const endpoint = walletConfig.rpc[network];

    if (!endpoint) {
      throw new Error(`No Neo RPC endpoint is configured for ${network}.`);
    }

    const log = await this.waitForApplicationLog(endpoint, transactionId);
    const execution = log.executions?.[0];
    const vmState = execution?.vmstate ?? execution?.state ?? '';

    if (vmState !== 'HALT') {
      throw new Error(`Deployment transaction finished with ${vmState || 'UNKNOWN'}${execution?.exception ? `: ${execution.exception}` : '.'}`);
    }

    const contractHash = this.findDeployContractHash(execution, contractManagementHash);

    if (!contractHash) {
      throw new Error('Deployment transaction halted, but Pusharoo could not find the deployed contract hash in the application log.');
    }

    return {
      transactionId,
      vmState,
      contractHash
    };
  }

  async waitForHalt(
    network: NetworkType,
    transactionId: string
  ): Promise<{ transactionId: string; vmState: string }> {
    const endpoint = walletConfig.rpc[network];

    if (!endpoint) {
      throw new Error(`No Neo RPC endpoint is configured for ${network}.`);
    }

    const log = await this.waitForApplicationLog(endpoint, transactionId);
    const execution = log.executions?.[0];
    const vmState = execution?.vmstate ?? execution?.state ?? '';

    if (vmState !== 'HALT') {
      throw new Error(`Update transaction finished with ${vmState || 'UNKNOWN'}${execution?.exception ? `: ${execution.exception}` : '.'}`);
    }

    return { transactionId, vmState };
  }

  private async waitForApplicationLog(
    endpoint: string,
    transactionId: string
  ): Promise<ApplicationLog> {
    const maxAttempts = 45;

    for (let attempt = 1; attempt <= maxAttempts; attempt += 1) {
      const response = await firstValueFrom(this.http.post<RpcResponse<ApplicationLog>>(endpoint, {
        jsonrpc: '2.0',
        method: 'getapplicationlog',
        params: [transactionId],
        id: attempt
      }));

      if (response.result) {
        return response.result;
      }

      if (response.error && !this.isPendingLogError(response.error.message)) {
        throw new Error(response.error.message);
      }

      await this.delay(4000);
    }

    throw new Error('Timed out waiting for the deployment transaction application log.');
  }

  private findDeployContractHash(
    execution: ApplicationLogExecution | undefined,
    contractManagementHash: string
  ): string | null {
    const normalizedManagementHash = this.normalizeHash(contractManagementHash);
    const deployNotification = execution?.notifications?.find((notification) =>
      notification.eventname === 'Deploy' &&
      this.normalizeHash(notification.contract ?? '') === normalizedManagementHash
    );

    return this.findHashInStackItem(deployNotification?.state)
      ?? this.findHashInStackItems(execution?.stack ?? []);
  }

  private findHashInStackItems(items: RpcStackItem[]): string | null {
    for (const item of items) {
      const hash = this.findHashInStackItem(item);

      if (hash) {
        return hash;
      }
    }

    return null;
  }

  private findHashInStackItem(item: RpcStackItem | undefined): string | null {
    if (!item) {
      return null;
    }

    if (item.type === 'Hash160' && typeof item.value === 'string') {
      return this.normalizeHash(item.value);
    }

    if ((item.type === 'ByteString' || item.type === 'Buffer') && typeof item.value === 'string') {
      return this.base64StackValueToHash(item.value);
    }

    if (Array.isArray(item.value)) {
      return this.findHashInStackItems(item.value.filter(this.isStackItem));
    }

    return null;
  }

  private base64StackValueToHash(value: string): string | null {
    const binary = atob(value);

    if (binary.length !== 20) {
      return null;
    }

    const bytes = [...binary].map((character) => character.charCodeAt(0));

    return `0x${bytes.reverse().map((byte) => byte.toString(16).padStart(2, '0')).join('')}`;
  }

  private normalizeHash(value: string): string {
    return value.startsWith('0x') ? value.toLowerCase() : `0x${value.toLowerCase()}`;
  }

  private isStackItem(value: unknown): value is RpcStackItem {
    return Boolean(value) && typeof value === 'object';
  }

  private isPendingLogError(message: string): boolean {
    const normalized = message.toLowerCase();

    return normalized.includes('unknown transaction') ||
      normalized.includes('unknown script container') ||
      normalized.includes('not found') ||
      normalized.includes('could not find');
  }

  private async delay(milliseconds: number): Promise<void> {
    await new Promise((resolve) => window.setTimeout(resolve, milliseconds));
  }
}
