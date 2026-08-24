import { DatePipe } from '@angular/common';
import { Component, OnInit } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, RouterLink } from '@angular/router';
import type { NetworkType } from 'neo-n3-walletkit';
import {
  Artifact,
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
import { NeoVmResultFormatterService } from '../../services/neo-vm-result-formatter.service';
import { PusharooApiService } from '../../services/pusharoo-api.service';
import { ApiErrorFormatterService } from '../../services/api-error-formatter.service';
import { WalletService } from '../../services/wallet.service';
import { PageShellComponent } from '../page-shell/page-shell.component';
import { ProjectReleaseNavComponent } from '../../components/project-release-nav/project-release-nav.component';

type ConsoleMode = 'test' | 'transaction';

interface ContractTarget {
  label: string;
  network: string;
  contractHash: string;
  artifactId: string;
  version: string;
  artifact: Artifact;
}

interface ConsoleEntry {
  id: string;
  at: Date;
  mode: ConsoleMode;
  methodName: string;
  status: 'success' | 'error';
  result: unknown;
  returnType?: string;
}

@Component({
  selector: 'app-contract-console',
  imports: [DatePipe, FormsModule, PageShellComponent, ProjectReleaseNavComponent, RouterLink],
  templateUrl: './contract-console.component.html',
  styleUrl: './contract-console.component.scss'
})
export class ContractConsoleComponent implements OnInit {
  overview: ProjectOverviewViewModel | null = null;
  targets: ContractTarget[] = [];
  selectedTargetKey = '';
  selectedMethodName = '';
  parameterValues: Record<string, string> = {};
  mode: ConsoleMode = 'test';
  isRunning = false;
  isLoading = true;
  loadError = '';
  errorMessage = '';
  consoleEntries: ConsoleEntry[] = [];
  readonly projectId: string;

  get pageTitle(): string {
    return 'Contract Console';
  }

  get selectedTarget(): ContractTarget | null {
    return this.targets.find((target) => this.targetKey(target) === this.selectedTargetKey) ?? null;
  }

  get selectedMethod(): NeoMethod | null {
    return this.methods.find((method) => method.name === this.selectedMethodName) ?? null;
  }

  get methods(): NeoMethod[] {
    return this.selectedTarget?.artifact.manifest.abi.methods ?? [];
  }

  get canRun(): boolean {
    return Boolean(this.selectedTarget && this.selectedMethod && !this.isRunning);
  }

  constructor(
    private readonly route: ActivatedRoute,
    private readonly api: PusharooApiService,
    private readonly errors: ApiErrorFormatterService,
    private readonly deploymentHistory: DeploymentHistoryService,
    private readonly neoRpc: NeoRpcService,
    private readonly resultFormatter: NeoVmResultFormatterService,
    readonly wallet: WalletService
  ) {
    this.projectId = this.route.snapshot.paramMap.get('projectId') ?? '';
  }

  ngOnInit(): void {
    this.loadProject();
  }

  retryLoad(): void {
    this.loadProject();
  }

  private loadProject(): void {
    this.isLoading = true;
    this.loadError = '';
    this.api.getProjectOverview(this.projectId).subscribe({
      next: (overview) => {
        this.overview = overview;
        this.targets = this.toTargets(overview);
        this.selectedTargetKey = this.targets[0] ? this.targetKey(this.targets[0]) : '';
        this.selectTarget();
        this.isLoading = false;
      },
      error: (error) => {
        this.overview = null;
        this.targets = [];
        this.loadError = this.errors.format(error, 'Could not load this project.');
        this.isLoading = false;
      }
    });
  }

  selectMethod(): void {
    this.resetParameterValues();
  }

  selectTarget(): void {
    this.selectedMethodName = this.methods[0]?.name ?? '';
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
        result,
        returnType: method.returntype ?? method.returnType
      });
    } catch (error) {
      const message = this.errors.format(error, 'Contract call failed.');
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
    return this.resultFormatter.format(value);
  }

  readableResult(entry: ConsoleEntry): string | null {
    return entry.mode === 'test' && entry.status === 'success' && entry.returnType
      ? this.resultFormatter.readableResult(entry.result, entry.returnType)
      : null;
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
      target.artifact.contractName
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
        if (value.toLowerCase() === 'true' || value === '1') {
          return true;
        }

        if (value.toLowerCase() === 'false' || value === '0') {
          return false;
        }

        throw new Error(`${this.parameterLabel(parameter)} must be true or false.`);
      case 'integer':
        if (!/^[+-]?\d+$/.test(value || '0')) {
          throw new Error(`${this.parameterLabel(parameter)} must be an integer.`);
        }

        return value || '0';
      case 'hash160':
        return this.requireHex(parameter, value, 40);
      case 'hash256':
        return this.requireHex(parameter, value, 64);
      case 'bytearray':
        return this.requireHex(parameter, value);
      case 'publickey':
        return this.requireHex(parameter, value, 66);
      case 'signature':
        return this.requireHex(parameter, value, 128);
      case 'array':
      case 'map':
      case 'any':
        try {
          return value ? JSON.parse(value) : null;
        } catch {
          throw new Error(`${this.parameterLabel(parameter)} must be valid JSON.`);
        }
      case 'void':
        return null;
      case 'interopinterface':
        throw new Error(`${this.parameterLabel(parameter)} cannot be entered manually.`);
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

  private addConsoleEntry(entry: ConsoleEntry): void {
    this.consoleEntries = [entry, ...this.consoleEntries].slice(0, 20);
  }

  private toTargets(overview: ProjectOverviewViewModel | null): ContractTarget[] {
    if (!overview) {
      return [];
    }

    return this.deploymentHistory
      .latestConfirmedByNetwork(overview.deployments)
      .map((deployment) => {
        const artifact = overview.artifacts.find((item) => item.id === deployment.artifactId);
        return artifact ? this.toTarget(deployment, artifact) : null;
      })
      .filter((target): target is ContractTarget => target !== null);
  }

  private toTarget(deployment: Deployment, artifact: Artifact): ContractTarget {
    return {
      label: `${deployment.network} - ${deployment.version}`,
      network: deployment.network,
      contractHash: deployment.contractHash ?? '',
      artifactId: deployment.artifactId,
      version: deployment.version,
      artifact
    };
  }

  private parameterLabel(parameter: NeoParameter): string {
    return parameter.name || `Parameter (${parameter.type})`;
  }

  private requireHex(parameter: NeoParameter, value: string, expectedLength?: number): string {
    const normalized = value.replace(/^0x/i, '');
    const hasExpectedLength = expectedLength === undefined || normalized.length === expectedLength;

    if (!normalized || !hasExpectedLength || !/^[0-9a-f]+$/i.test(normalized)) {
      const lengthDescription = expectedLength ? ` with ${expectedLength} hexadecimal characters` : '';
      throw new Error(`${this.parameterLabel(parameter)} must be hexadecimal${lengthDescription}.`);
    }

    return `0x${normalized.toLowerCase()}`;
  }
}
