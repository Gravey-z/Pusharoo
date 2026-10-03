import { HttpClient } from '@angular/common/http';
import { Injectable } from '@angular/core';
import type { NetworkType } from 'neo-n3-walletkit';
import { firstValueFrom } from 'rxjs';
import { isPusharooNetwork } from '../config/wallet.config';
import { RuntimeConfigService } from './runtime-config.service';

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

interface NeoVersionResponse {
  protocol?: { network?: number };
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

export interface RpcInvocationSigner {
  account: string;
  scopes: string | number;
}

export interface ConfirmedDeployment {
  transactionId: string;
  vmState: string;
  contractHash: string;
}

export interface ConfirmedFaucetClaim {
  transactionId: string;
  amountDatoshi: string;
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
  constructor(private readonly http: HttpClient, private readonly runtimeConfig: RuntimeConfigService) {}

  async invokeFunction(
    network: NetworkType,
    contractHash: string,
    methodName: string,
    parameters: ContractParameter[],
    signers?: RpcInvocationSigner[]
  ): Promise<ContractInvokeResult> {
    if (!isPusharooNetwork(network)) {
      throw new Error(`No Neo RPC endpoint is configured for ${network}.`);
    }

    const endpoint = this.runtimeConfig.value.wallet.rpc[network];

    const response = await firstValueFrom(this.http.post<RpcResponse<ContractInvokeResult>>(endpoint, {
      jsonrpc: '2.0',
      method: 'invokefunction',
      params: signers?.length
        ? [contractHash, methodName, parameters, signers]
        : [contractHash, methodName, parameters],
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

  async verifyTestnetContract(contractHash: string): Promise<void> {
    if (!contractHash) throw new Error('The testnet faucet contract is not configured.');
    const endpoint = this.runtimeConfig.value.wallet.rpc['neo3:testnet'];
    const version = await this.rpcRequest<NeoVersionResponse>(endpoint, 'getversion', []);
    if (version.protocol?.network !== 894710606) {
      throw new Error('The configured faucet RPC endpoint is not Neo N3 testnet.');
    }
    await this.rpcRequest(endpoint, 'getcontractstate', [contractHash]);
  }

  async getGasBalanceDatoshi(addressScriptHash: string): Promise<string> {
    const result = await this.invokeFunction('neo3:testnet', '0xd2a4cff31913016155e38e474a2c06d08be276cf', 'balanceOf', [
      { type: 'Hash160', value: addressScriptHash }
    ]);
    return this.stackInteger(result.stack?.[0]).toString();
  }

  async waitForFaucetClaim(
    transactionId: string,
    faucetHash: string,
    recipientScriptHash: string
  ): Promise<ConfirmedFaucetClaim> {
    const endpoint = this.runtimeConfig.value.wallet.rpc['neo3:testnet'];
    const log = await this.waitForApplicationLog(endpoint, transactionId);
    const execution = log.executions?.[0];
    const vmState = execution?.vmstate ?? execution?.state ?? '';
    if (vmState !== 'HALT') {
      throw new Error(`Claim transaction finished with ${vmState || 'UNKNOWN'}${execution?.exception ? `: ${execution.exception}` : '.'}`);
    }

    let claimedAmount: bigint | null = null;
    let transferredAmount: bigint | null = null;
    for (const notification of execution?.notifications ?? []) {
      const state = notification.state?.value;
      if (!Array.isArray(state)) continue;
      if (this.sameHash(notification.contract ?? '', faucetHash)
        && notification.eventname === 'Claimed'
        && state.length >= 2
        && this.sameHash(this.stackHash(state[0]), recipientScriptHash)) {
        claimedAmount = this.stackInteger(state[1]);
      }
      if (this.sameHash(notification.contract ?? '', '0xd2a4cff31913016155e38e474a2c06d08be276cf')
        && notification.eventname === 'Transfer'
        && state.length >= 3
        && this.sameHash(this.stackHash(state[0]), faucetHash)
        && this.sameHash(this.stackHash(state[1]), recipientScriptHash)) {
        transferredAmount = this.stackInteger(state[2]);
      }
    }

    if (claimedAmount === null || transferredAmount !== claimedAmount) {
      throw new Error('Claim transaction halted without matching faucet and GAS transfer events.');
    }
    return { transactionId, amountDatoshi: claimedAmount.toString() };
  }

  async waitForDeployment(
    network: NetworkType,
    transactionId: string,
    contractManagementHash: string
  ): Promise<ConfirmedDeployment> {
    if (!isPusharooNetwork(network)) {
      throw new Error(`No Neo RPC endpoint is configured for ${network}.`);
    }

    const endpoint = this.runtimeConfig.value.wallet.rpc[network];

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
    if (!isPusharooNetwork(network)) {
      throw new Error(`No Neo RPC endpoint is configured for ${network}.`);
    }

    const endpoint = this.runtimeConfig.value.wallet.rpc[network];

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

  private async rpcRequest<T>(endpoint: string, method: string, params: unknown[]): Promise<T> {
    if (!endpoint) throw new Error('The Neo N3 testnet RPC endpoint is not configured.');
    const response = await firstValueFrom(this.http.post<RpcResponse<T>>(endpoint, {
      jsonrpc: '2.0', method, params, id: Date.now()
    }));
    if (response.error) throw new Error(response.error.message);
    if (response.result === undefined) throw new Error(`Neo RPC returned no result for ${method}.`);
    return response.result;
  }

  private stackInteger(item: RpcStackItem | undefined): bigint {
    const value = item?.value;
    if (typeof value === 'number' || typeof value === 'string') {
      const text = String(value);
      if (/^-?\d+$/.test(text)) return BigInt(text);
      if (item?.type === 'ByteString' || item?.type === 'Buffer') return this.littleEndianInteger(text);
    }
    throw new Error('Neo RPC returned an invalid integer value.');
  }

  private littleEndianInteger(base64: string): bigint {
    const bytes = Uint8Array.from(atob(base64), (character) => character.charCodeAt(0));
    let value = 0n;
    for (let index = bytes.length - 1; index >= 0; index -= 1) value = (value << 8n) | BigInt(bytes[index]);
    if (bytes.length && (bytes[bytes.length - 1] & 0x80)) value -= 1n << BigInt(bytes.length * 8);
    return value;
  }

  private stackHash(item: RpcStackItem): string {
    const value = item.value;
    if (typeof value !== 'string') return '';
    if (/^(0x)?[0-9a-f]{40}$/i.test(value)) return this.normalizeHash(value);
    if (item.type === 'ByteString' || item.type === 'Buffer') {
      const bytes = [...atob(value)].map((character) => character.charCodeAt(0)).reverse();
      if (bytes.length === 20) return `0x${bytes.map((byte) => byte.toString(16).padStart(2, '0')).join('')}`;
    }
    return '';
  }

  private sameHash(left: string, right: string): boolean {
    return this.normalizeHash(left).replace(/^0x/i, '') === this.normalizeHash(right).replace(/^0x/i, '');
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
