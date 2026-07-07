import { DatePipe } from '@angular/common';
import { Component, OnInit } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, RouterLink } from '@angular/router';
import type { NetworkType } from 'neo-n3-walletkit';
import { firstValueFrom } from 'rxjs';
import {
  Deployment,
  NeoMethod,
  NeoParameter,
  ProjectOverviewViewModel
} from '../../models/pusharoo.models';
import { DeploymentHistoryService } from '../../services/deployment-history.service';
import {
  ContractInvokeResult,
  ContractParameter,
  NeoRpcService
} from '../../services/neo-rpc.service';
import { PusharooApiService } from '../../services/pusharoo-api.service';
import { WalletService } from '../../services/wallet.service';
import { PageShellComponent } from '../page-shell/page-shell.component';

type ConsoleMode = 'test' | 'transaction';

interface ContractTarget {
  label: string;
  network: string;
  contractHash: string;
  artifactId: string;
  version: string;
}

interface ConsoleEntry {
  id: string;
  at: Date;
  mode: ConsoleMode;
  methodName: string;
  status: 'success' | 'error';
  result: unknown;
}

@Component({
  selector: 'app-contract-console',
  imports: [DatePipe, FormsModule, PageShellComponent, RouterLink],
  templateUrl: './contract-console.component.html',
  styleUrl: './contract-console.component.scss'
})
export class ContractConsoleComponent implements OnInit {
  overview: ProjectOverviewViewModel | null = null;
  targets: ContractTarget[] = [];
  methods: NeoMethod[] = [];
  selectedTargetKey = '';
  selectedMethodName = '';
  parameterValues: Record<string, string> = {};
  mode: ConsoleMode = 'test';
  isRunning = false;
  errorMessage = '';
  consoleEntries: ConsoleEntry[] = [];
  readonly projectId: string;

  get pageTitle(): string {
    return this.overview ? `${this.overview.project.name}: Contract Console` : 'Contract Console';
  }

  get selectedTarget(): ContractTarget | null {
    return this.targets.find((target) => this.targetKey(target) === this.selectedTargetKey) ?? null;
  }

  get selectedMethod(): NeoMethod | null {
    return this.methods.find((method) => method.name === this.selectedMethodName) ?? null;
  }

  get canRun(): boolean {
    return Boolean(this.selectedTarget && this.selectedMethod && !this.isRunning);
  }

  constructor(
    private readonly route: ActivatedRoute,
    private readonly api: PusharooApiService,
    private readonly deploymentHistory: DeploymentHistoryService,
    private readonly neoRpc: NeoRpcService,
    readonly wallet: WalletService
  ) {
    this.projectId = this.route.snapshot.paramMap.get('projectId') ?? '';
  }

  ngOnInit(): void {
    this.api.getProjectOverview(this.projectId).subscribe((overview) => {
      this.overview = overview;
      this.methods = overview?.latestArtifact?.manifest.abi.methods ?? [];
      this.targets = this.toTargets(overview);
      this.selectedTargetKey = this.targets[0] ? this.targetKey(this.targets[0]) : '';
      this.selectedMethodName = this.methods[0]?.name ?? '';
      this.resetParameterValues();
    });
  }

  selectMethod(): void {
    this.resetParameterValues();
  }

  async run(): Promise<void> {
    this.errorMessage = '';

    const target = this.selectedTarget;
    const method = this.selectedMethod;

    if (!target || !method) {
      this.errorMessage = 'Choose a deployed contract and method.';
      return;
    }

    this.isRunning = true;

    try {
      const parameters = this.toContractParameters(method.parameters);
      const result = this.mode === 'test'
        ? await this.testInvoke(target, method, parameters)
        : await this.sendTransaction(target, method, parameters);

      this.addConsoleEntry({
        id: crypto.randomUUID(),
        at: new Date(),
        mode: this.mode,
        methodName: method.name,
        status: 'success',
        result
      });
    } catch (error) {
      const message = error instanceof Error ? error.message : 'Contract call failed.';
      this.errorMessage = message;
      this.addConsoleEntry({
        id: crypto.randomUUID(),
        at: new Date(),
        mode: this.mode,
        methodName: method.name,
        status: 'error',
        result: { error: message }
      });
    } finally {
      this.isRunning = false;
    }
  }

  methodReturnType(method: NeoMethod): string {
    return method.returntype ?? method.returnType ?? '-';
  }

  parameterKey(parameter: NeoParameter, index: number): string {
    return `${index}:${parameter.name || 'param'}`;
  }

  parameterPlaceholder(parameter: NeoParameter): string {
    switch (parameter.type.toLowerCase()) {
      case 'boolean':
        return 'true';
      case 'integer':
        return '123';
      case 'array':
      case 'map':
      case 'any':
        return 'JSON value';
      case 'hash160':
        return '0x...';
      default:
        return parameter.type;
    }
  }

  formatResult(value: unknown): string {
    return JSON.stringify(this.withDecodedStackValues(value), null, 2);
  }

  shortHash(value: string): string {
    return value.length > 17 ? `${value.slice(0, 10)}...${value.slice(-4)}` : value;
  }

  targetKey(target: ContractTarget): string {
    return `${target.network}:${target.contractHash}:${target.artifactId}`;
  }

  private async testInvoke(
    target: ContractTarget,
    method: NeoMethod,
    parameters: ContractParameter[]
  ): Promise<ContractInvokeResult> {
    return await this.neoRpc.invokeFunction(
      target.network as NetworkType,
      target.contractHash,
      method.name,
      parameters
    );
  }

  private async sendTransaction(
    target: ContractTarget,
    method: NeoMethod,
    parameters: ContractParameter[]
  ): Promise<{ transactionId: string }> {
    const transactionId = await this.wallet.invokeContract(
      target.network as NetworkType,
      target.contractHash,
      method.name,
      parameters,
      this.overview?.latestArtifact?.contractName ?? 'contract'
    );

    return { transactionId };
  }

  private resetParameterValues(): void {
    this.parameterValues = {};

    for (const [index, parameter] of (this.selectedMethod?.parameters ?? []).entries()) {
      this.parameterValues[this.parameterKey(parameter, index)] = '';
    }
  }

  private toContractParameters(parameters: NeoParameter[]): ContractParameter[] {
    return parameters.map((parameter, index) => ({
      type: this.toContractParameterType(parameter.type),
      value: this.parseParameterValue(parameter, this.parameterValues[this.parameterKey(parameter, index)] ?? '')
    }));
  }

  private parseParameterValue(parameter: NeoParameter, rawValue: string): unknown {
    const value = rawValue.trim();

    switch (parameter.type.toLowerCase()) {
      case 'boolean':
        return value.toLowerCase() === 'true' || value === '1';
      case 'integer':
        return value ? Number(value) : 0;
      case 'array':
      case 'map':
      case 'any':
        return value ? JSON.parse(value) : null;
      case 'void':
        return null;
      default:
        return value;
    }
  }

  private toContractParameterType(type: string): string {
    const normalizedType = type.toLowerCase();
    const typeMap: Record<string, string> = {
      signature: 'Signature',
      boolean: 'Boolean',
      integer: 'Integer',
      hash160: 'Hash160',
      hash256: 'Hash256',
      bytearray: 'ByteArray',
      publickey: 'PublicKey',
      string: 'String',
      array: 'Array',
      map: 'Map',
      interopinterface: 'InteropInterface',
      void: 'Void',
      any: 'Any'
    };

    return typeMap[normalizedType] ?? type;
  }

  private withDecodedStackValues(value: unknown): unknown {
    if (Array.isArray(value)) {
      return value.map((item) => this.withDecodedStackValues(item));
    }

    if (!value || typeof value !== 'object') {
      return value;
    }

    const record = value as Record<string, unknown>;
    const decodedValue = this.tryDecodeStackValue(record);
    const mapped = Object.fromEntries(
      Object.entries(record).map(([key, entryValue]) => [
        key,
        this.withDecodedStackValues(entryValue)
      ])
    );

    return decodedValue === null
      ? mapped
      : {
          ...mapped,
          decodedValue
        };
  }

  private tryDecodeStackValue(item: Record<string, unknown>): string | null {
    const type = typeof item['type'] === 'string' ? item['type'] : '';
    const value = item['value'];

    if ((type !== 'ByteString' && type !== 'Buffer') || typeof value !== 'string') {
      return null;
    }

    return this.tryDecodeBase64Text(value);
  }

  private tryDecodeBase64Text(value: string): string | null {
    try {
      const binary = atob(value);
      const bytes = Uint8Array.from(binary, (character) => character.charCodeAt(0));
      const decoded = new TextDecoder('utf-8', { fatal: true }).decode(bytes);
      const normalized = decoded.trim();

      return normalized && this.isReadableText(normalized) ? normalized : null;
    } catch {
      return null;
    }
  }

  private isReadableText(value: string): boolean {
    return [...value].every((character) => {
      const codePoint = character.codePointAt(0) ?? 0;

      return character === '\n' ||
        character === '\r' ||
        character === '\t' ||
        (codePoint >= 32 && codePoint !== 127);
    });
  }

  private addConsoleEntry(entry: ConsoleEntry): void {
    this.consoleEntries = [entry, ...this.consoleEntries].slice(0, 20);
  }

  private toTargets(overview: ProjectOverviewViewModel | null): ContractTarget[] {
    if (!overview) {
      return [];
    }

    return this.deploymentHistory
      .latestByNetwork(overview.deployments)
      .filter((deployment) => deployment.contractHash)
      .map((deployment) => this.toTarget(deployment));
  }

  private toTarget(deployment: Deployment): ContractTarget {
    return {
      label: `${deployment.network} - ${deployment.version}`,
      network: deployment.network,
      contractHash: deployment.contractHash ?? '',
      artifactId: deployment.artifactId,
      version: deployment.version
    };
  }
}
