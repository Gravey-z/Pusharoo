import { DestroyRef, Component, OnInit, inject } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { DatePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { workspaceRoute, contractLink } from '../../services/workspace-route';
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
import { ProjectWorkspaceContextService } from '../../services/project-workspace-context.service';

type ConsoleMode = 'test' | 'transaction';

interface ContractTarget {
  label: string;
  network: string;
  contractHash: string;
  artifactId: string;
  version: string;
  artifact: Artifact | null;
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
  private readonly destroyRef = inject(DestroyRef);
  private loadGeneration = 0;
  contractId: string | null = null;
  contractPath(...segments: string[]): string[] {
    return contractLink(this.projectId, this.contractId, ...segments);
  }
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
  projectId = '';
  private readonly workspace = inject(ProjectWorkspaceContextService, { optional: true });

  get pageTitle(): string {
    return 'Contract Console';
  }

  get selectedTarget(): ContractTarget | null {
    return this.targets.find(target => this.targetKey(target) === this.selectedTargetKey) ?? null;
  }

  get selectedMethod(): NeoMethod | null {
    return this.methods.find(method => method.name === this.selectedMethodName) ?? null;
  }

  get methods(): NeoMethod[] {
    return this.selectedTarget?.artifact?.manifest.abi.methods ?? [];
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
  ) {}

  ngOnInit(): void {
    workspaceRoute(this.route)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(scope => {
        this.projectId = scope.projectId;
        this.contractId = scope.contractId;
        this.overview = null;
        this.targets = [];
        this.selectedTargetKey = '';
        this.selectedMethodName = '';
        this.parameterValues = {};
        this.consoleEntries = [];
        this.errorMessage = '';
        this.isRunning = false;
        this.mode = 'test';
        this.loadProject();
      });
  }

  retryLoad(): void {
    this.loadProject();
  }

  private loadProject(): void {
    const generation = ++this.loadGeneration;
    const cachedOverview = this.contractId
      ? null
      : this.workspace?.getFreshOverview(this.projectId);
    if (cachedOverview?.project.id === this.projectId) {
      this.applyOverview(cachedOverview);
      return;
    }

    this.isLoading = true;
    this.loadError = '';
    (this.contractId
      ? this.api.getContractOverview(this.projectId, this.contractId)
      : this.api.getProjectOverview(this.projectId)
    ).subscribe({
      next: overview => {
        if (generation !== this.loadGeneration || this.destroyRef.destroyed) return;
        if (this.workspace && !this.contractId) {
          this.workspace.overview = overview;
        }
        this.applyOverview(overview);
      },
      error: error => {
        if (generation !== this.loadGeneration || this.destroyRef.destroyed) return;
        this.overview = null;
        this.targets = [];
        this.loadError = this.errors.format(error, 'Could not load this project.');
        this.isLoading = false;
      }
    });
  }

  private applyOverview(overview: ProjectOverviewViewModel): void {
    this.overview = overview;
    this.targets = this.toTargets(overview);
    this.selectedTargetKey = this.targets[0] ? this.targetKey(this.targets[0]) : '';
    this.selectTarget();
    this.loadError = '';
    this.isLoading = false;
  }

  selectMethod(): void {
    this.resetParameterValues();
  }

  selectTarget(): void {
    this.loadGeneration++;
    this.consoleEntries = [];
    this.errorMessage = '';
    this.isRunning = false;
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
    const generation = this.loadGeneration;
    const targetKey = this.selectedTargetKey;
    const mode = this.mode;

    try {
      const parameters = this.toContractParameters(method.parameters);
      const result =
        mode === 'test'
          ? await this.testInvoke(target, method, parameters)
          : await this.sendTransaction(target, method, parameters);

      if (
        generation !== this.loadGeneration ||
        targetKey !== this.selectedTargetKey ||
        this.destroyRef.destroyed
      )
        return;
      this.addConsoleEntry({
        id: crypto.randomUUID(),
        at: new Date(),
        mode,
        methodName: method.name,
        status: 'success',
        result,
        returnType: method.returntype ?? method.returnType
      });
    } catch (error) {
      if (
        generation !== this.loadGeneration ||
        targetKey !== this.selectedTargetKey ||
        this.destroyRef.destroyed
      )
        return;
      const message = this.errors.format(error, 'Contract call failed.');
      this.errorMessage = message;
      this.addConsoleEntry({
        id: crypto.randomUUID(),
        at: new Date(),
        mode,
        methodName: method.name,
        status: 'error',
        result: { error: message }
      });
    } finally {
      if (generation === this.loadGeneration && !this.destroyRef.destroyed) this.isRunning = false;
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
    if (entry.status !== 'success') {
      return null;
    }

    if (entry.mode === 'transaction') {
      const transactionId = (entry.result as { transactionId?: string }).transactionId;
      return transactionId ?? null;
    }

    return entry.returnType
      ? this.resultFormatter.readableResult(entry.result, entry.returnType)
      : null;
  }

  shortHash(value: string): string {
    return value.length > 17 ? `${value.slice(0, 10)}...${value.slice(-4)}` : value;
  }

  isMainnet(network: string): boolean {
    return network.toLowerCase().includes('mainnet');
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
      target.artifact?.contractName ?? 'Contract'
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
      value: this.parseParameterValue(
        parameter,
        this.parameterValues[this.parameterKey(parameter, index)] ?? ''
      )
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
      .latestConfirmedByNetwork(
        overview.deployments.filter(item => (item.contractId ?? null) === this.contractId)
      )
      .map(deployment => {
        const artifact = overview.artifacts.find(item => item.id === deployment.artifactId);
        return this.toTarget(deployment, artifact ?? null);
      })
      .filter((target): target is ContractTarget => target !== null);
  }

  private toTarget(deployment: Deployment, artifact: Artifact | null): ContractTarget {
    return {
      label: `${deployment.network} - ${deployment.version || 'Recovered deployment'}`,
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
      const lengthDescription = expectedLength
        ? ` with ${expectedLength} hexadecimal characters`
        : '';
      throw new Error(`${this.parameterLabel(parameter)} must be hexadecimal${lengthDescription}.`);
    }

    return `0x${normalized.toLowerCase()}`;
  }
}
