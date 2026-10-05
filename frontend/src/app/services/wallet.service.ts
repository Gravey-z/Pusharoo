import { computed, Injectable, signal } from '@angular/core';
import { WalletKit } from 'neo-n3-walletkit';
import { toDataURL } from 'qrcode';
import type {
  ConnectedAccount,
  ContractArgs,
  Method,
  NetworkType,
  Signer,
  WalletProvider,
  WalletSession
} from 'neo-n3-walletkit';
import { defaultWalletConfig, isPusharooNetwork, PusharooNetwork } from '../config/wallet.config';
import { DeploymentDataValue, WalletActionSignature, WalletLoginChallenge } from '../models/pusharoo.models';
import {
  ProjectCreationSignatureMessageService,
  WalletActionSignatureChallenge
} from './project-creation-signature-message.service';
import { RuntimeConfigService } from './runtime-config.service';
import { DeploymentDataService } from './deployment-data.service';
import { NeoRpcService } from './neo-rpc.service';
import type { ContractInvokeResult } from './neo-rpc.service';

type WalletStatus = 'idle' | 'connecting' | 'connected' | 'error';
type ConnectableWalletProvider = Extract<WalletProvider, 'neoline' | 'onegate' | 'walletconnect'>;
interface ContractCallParameter {
  type: string;
  value: unknown;
}

interface BuiltInvocation {
  scriptHash: string;
  operation: string;
  args: ContractArgs;
}

export interface DeploymentFeeEstimate {
  systemFee: string;
  networkFee: string;
  total: string;
}

interface SignedMessageResponse {
  publicKey?: unknown;
  data?: unknown;
  salt?: unknown;
  message?: unknown;
  messageHex?: unknown;
}

interface MessageSigningProvider {
  signMessage: (...args: unknown[]) => Promise<unknown>;
}

@Injectable({ providedIn: 'root' })
export class WalletService {
  private readonly providerStorageKey = 'pusharoo.walletProvider';
  private readonly networkStorageKey = 'pusharoo.walletNetwork';
  private walletKit: WalletKit | null = null;
  private unsubscribeSession: (() => void) | null = null;

  readonly account = signal<ConnectedAccount | null>(null);
  readonly session = signal<WalletSession | null>(null);
  readonly status = signal<WalletStatus>('idle');
  readonly errorMessage = signal('');
  readonly disconnectEpoch = signal(0);
  readonly selectedProvider = signal<ConnectableWalletProvider | null>(null);
  readonly selectedNetwork = signal<PusharooNetwork>(this.getSavedNetwork());
  readonly walletConnectUri = signal('');
  readonly walletConnectQrCode = signal('');
  readonly neonConnectUrl = computed(() => {
    const uri = this.walletConnectUri();
    if (!uri) {
      return '';
    }

    const neonUrl = new URL('https://neon.coz.io/connect');
    neonUrl.searchParams.set('uri', uri);

    return neonUrl.toString();
  });
  readonly isBusy = computed(() => this.status() === 'connecting');
  readonly shortAddress = computed(() => {
    const address = this.account()?.address;

    return address ? `${address.slice(0, 6)}...${address.slice(-4)}` : '';
  });

  networkLabel(network: string | null | undefined): string {
    switch (network?.toLowerCase()) {
      case 'neo3:mainnet':
      case 'mainnet':
        return 'N3:Mainnet';
      case 'neo3:testnet':
      case 'testnet':
        return 'N3:Testnet';
      default:
        return network || 'No network';
    }
  }

  constructor(
    private readonly projectCreationMessage: ProjectCreationSignatureMessageService,
    private readonly runtimeConfig: RuntimeConfigService,
    private readonly deploymentData: DeploymentDataService,
    private readonly neoRpc: NeoRpcService
  ) {}

  async restoreSavedSession(): Promise<void> {
    if (this.session() || this.status() === 'connecting') {
      return;
    }

    const provider = this.getSavedProvider();

    if (!provider) {
      return;
    }

    this.status.set('connecting');
    this.errorMessage.set('');
    this.selectedProvider.set(provider);

    try {
      const walletKit = await this.initWalletKit(provider, this.selectedNetwork());
      const session = walletKit.isConnected
        ? walletKit.wallet.session
        : provider === 'walletconnect'
          ? null
          : await walletKit.connect();

      if (!session) {
        this.clearSavedProvider();
        this.resetWalletState();
        return;
      }

      this.setWalletKit(walletKit);
      this.setSession(session);
    } catch {
      this.clearSavedProvider();
      this.resetWalletState();
    }
  }

  async connect(provider: ConnectableWalletProvider): Promise<void> {
    this.status.set('connecting');
    this.errorMessage.set('');
    this.selectedProvider.set(provider);
    this.walletConnectUri.set('');
    this.walletConnectQrCode.set('');

    try {
      const walletKit = provider === 'walletconnect'
        ? await this.startWalletConnectApproval(this.selectedNetwork())
        : await this.initWalletKit(provider, this.selectedNetwork());

      const session = provider === 'walletconnect'
        ? null
        : await walletKit.connect();
      this.setWalletKit(walletKit);

      if (session) {
        this.setSession(session);
      }
    } catch (error) {
      this.status.set('error');
      this.account.set(null);
      this.session.set(null);
      this.errorMessage.set(this.getErrorMessage(error, provider));
    }
  }

  async disconnect(): Promise<void> {
    this.errorMessage.set('');
    this.disconnectEpoch.update((value) => value + 1);
    this.account.set(null);
    this.session.set(null);
    const walletKit = this.walletKit;
    this.unsubscribeSession?.();
    this.unsubscribeSession = null;
    this.walletKit = null;

    try {
      await walletKit?.disconnect();
    } finally {
      this.account.set(null);
      this.session.set(null);
      this.walletConnectUri.set('');
      this.walletConnectQrCode.set('');
      this.status.set('idle');
      this.selectedProvider.set(null);
      this.clearSavedProvider();
    }
  }

  selectNetwork(network: PusharooNetwork): void {
    if (this.status() === 'connected') {
      return;
    }

    this.selectedNetwork.set(network);
    this.saveNetwork(network);
    this.errorMessage.set('');
  }

  async deployContract(
    network: NetworkType,
    nefHex: string,
    manifestJson: string,
    contractName: string,
    initializationData: DeploymentDataValue = { type: 'Any', value: null }
  ): Promise<string> {
    const session = this.session();
    const walletKit = this.walletKit;

    if (!walletKit || !session) {
      throw new Error('Connect a wallet before deploying.');
    }

    if (session.network !== network) {
      throw new Error(`Connected wallet is on ${session.network}. Select ${session.network} or reconnect on ${network}.`);
    }

    if (!isPusharooNetwork(network)) {
      throw new Error(`Pusharoo does not support ${network}. Use Neo N3 testnet or mainnet.`);
    }

    const invocation = this.buildReleaseInvocation(
      network,
      'deploy',
      nefHex,
      manifestJson,
      undefined,
      initializationData,
      session.provider
    );
    return walletKit.wallet.request<string>(
      'invokeFunction',
      { invocations: [invocation], signers: [this.deploymentSigner(walletKit, network)] },
      `Deploy ${contractName} with Pusharoo`
    );
  }

  async updateContract(
    network: NetworkType,
    contractHash: string,
    nefHex: string,
    manifestJson: string,
    contractName: string
  ): Promise<string> {
    const session = this.session();
    const walletKit = this.walletKit;

    if (!walletKit || !session) {
      throw new Error('Connect a wallet before updating.');
    }

    if (session.network !== network) {
      throw new Error(`Connected wallet is on ${session.network}. Reconnect on ${network}.`);
    }

    if (!isPusharooNetwork(network)) {
      throw new Error(`Pusharoo does not support ${network}. Use Neo N3 testnet or mainnet.`);
    }

    const contract = walletKit.contract(contractHash);
    const nefValue = session.provider === 'onegate'
      ? nefHex
      : this.hexToBase64(nefHex);
    const args: ContractArgs = [
      { type: 'ByteArray', value: nefValue },
      { type: 'String', value: manifestJson }
    ];

    return await contract.invoke(
      'update',
      args,
      { context: `Update ${contractName} with Pusharoo` }
    );
  }

  async estimateDeploymentFees(
    network: NetworkType,
    operation: 'deploy' | 'update',
    nefHex: string,
    manifestJson: string,
    contractHash?: string,
    initializationData: DeploymentDataValue = { type: 'Any', value: null }
  ): Promise<DeploymentFeeEstimate> {
    const session = this.session();
    const walletKit = this.walletKit;

    if (!walletKit || !session) {
      throw new Error('Connect a wallet before estimating fees.');
    }

    if (session.network !== network || !isPusharooNetwork(network)) {
      throw new Error(`No Neo RPC endpoint is configured for ${network}.`);
    }

    if (!session.methods.includes('calculateFee')) {
      throw new Error(`${session.provider} does not support fee estimation through the connected wallet. Review the final fee shown by your wallet before signing.`);
    }

    const invocation = this.buildReleaseInvocation(
      network,
      operation,
      nefHex,
      manifestJson,
      contractHash,
      initializationData,
      session.provider
    );

    const result = await walletKit.wallet.request<{
      systemFee?: unknown;
      networkFee?: unknown;
      total?: unknown;
    }>('calculateFee', {
      invocations: [invocation],
      signers: [operation === 'deploy'
        ? this.deploymentSigner(walletKit, network)
        : walletKit.connectedSigner()]
    }, `Estimate ${operation} fees with Pusharoo`);

    const systemFee = this.formatFee(result.systemFee);
    const networkFee = this.formatFee(result.networkFee);
    const total = result.total === undefined || result.total === null
      ? null
      : this.formatFee(result.total);

    if (!systemFee || !networkFee) {
      throw new Error('The wallet returned an incomplete fee estimate.');
    }

    return {
      systemFee,
      networkFee,
      total: total ?? this.sumFees(systemFee, networkFee)
    };
  }

  async simulateContractDeployment(
    network: NetworkType,
    nefHex: string,
    manifestJson: string,
    initializationData: DeploymentDataValue
  ): Promise<ContractInvokeResult> {
    const session = this.session();
    const walletKit = this.walletKit;
    if (!walletKit || !session) throw new Error('Connect a wallet before simulating a deployment.');
    if (session.network !== network || !isPusharooNetwork(network)) {
      throw new Error(`The connected wallet must be on ${network} to simulate this deployment.`);
    }

    const invocation = this.buildReleaseInvocation(
      network,
      'deploy',
      nefHex,
      manifestJson,
      undefined,
      initializationData,
      session.provider,
      'hex'
    );
    const signer = this.deploymentSigner(walletKit, network);
    const signerAccount = signer.account ?? this.account()?.scriptHash;
    if (!signerAccount) throw new Error('The connected wallet did not provide a signer account for deployment simulation.');
    return this.neoRpc.invokeFunction(
      network,
      invocation.scriptHash,
      invocation.operation,
      invocation.args,
      [{ account: `0x${signerAccount.replace(/^0x/i, '')}`, scopes: signer.scopes, rules: signer.rules }]
    );
  }

  async estimateContractInvocationFees(
    network: NetworkType,
    contractHash: string,
    operation: string,
    args: ContractCallParameter[]
  ): Promise<DeploymentFeeEstimate> {
    const session = this.session();
    const walletKit = this.walletKit;
    if (!walletKit || !session || session.network !== network || network !== 'neo3:testnet') {
      throw new Error('Connect a wallet on N3:Testnet to estimate this transaction fee.');
    }
    if (session.provider === 'walletconnect' && !session.methods.includes('calculateFee')) {
      throw new Error('Reconnect your wallet to enable transaction fee estimates.');
    }

    const result = await walletKit.wallet.request<{
      systemFee?: unknown;
      networkFee?: unknown;
      total?: unknown;
    }>('calculateFee', {
      invocations: [{ scriptHash: contractHash, operation, args: args as ContractArgs }],
      signers: [walletKit.connectedSigner()]
    }, `Estimate ${operation} fee with Pusharoo`);

    const systemFee = this.formatFee(result.systemFee);
    const networkFee = this.formatFee(result.networkFee);
    const total = result.total === undefined || result.total === null ? null : this.formatFee(result.total);
    if (!systemFee || !networkFee) throw new Error('The wallet returned an incomplete fee estimate.');
    return { systemFee, networkFee, total: total ?? this.sumFees(systemFee, networkFee) };
  }

  async signWalletLogin(challenge: WalletLoginChallenge): Promise<WalletActionSignature> {
    const session = this.session();
    const account = this.account();
    if (!this.walletKit || !session || !account) {
      throw new Error('Connect a wallet before signing in to Pusharoo.');
    }
    if (challenge.origin !== window.location.origin || challenge.audience !== this.runtimeConfig.value.walletSignatureAudience) {
      throw new Error('The login challenge is for a different Pusharoo application.');
    }
    const signedMessage = await this.signMessage(session, account.address, challenge.message, 'Sign in to Pusharoo');
    if (this.account()?.address !== account.address || this.account()?.scriptHash !== account.scriptHash
      || this.session()?.network !== session.network) {
      throw new Error('The wallet changed while signing in. Try again with the connected account.');
    }
    return this.toWalletActionSignature(account, session, challenge, signedMessage);
  }

  async signFaucetClaim(message: string): Promise<WalletActionSignature> {
    const session = this.session();
    const account = this.account();
    if (!this.walletKit || !session || !account) throw new Error('Connect a wallet before claiming testnet GAS.');
    if (session.network !== 'neo3:testnet') throw new Error('Faucet claims are available on N3:Testnet only.');

    const challenge: WalletActionSignatureChallenge = {
      ...this.projectCreationMessage.createSignatureContext(),
      message
    };
    const signedMessage = await this.signMessage(
      session,
      account.address,
      message,
      'Sign a message to request sponsored testnet GAS. Pusharoo pays the transaction fee.'
    );
    return this.toWalletActionSignature(account, session, challenge, signedMessage);
  }

  async invokeContract(
    network: NetworkType,
    contractHash: string,
    methodName: string,
    args: ContractCallParameter[],
    contractName: string
  ): Promise<string> {
    const session = this.session();
    const walletKit = this.walletKit;

    if (!walletKit || !session) {
      throw new Error('Connect a wallet before sending a contract transaction.');
    }

    if (session.network !== network) {
      throw new Error(`Connected wallet is on ${session.network}. Reconnect on ${network}.`);
    }

    const contract = walletKit.contract(contractHash);

    return await contract.invoke(
      methodName,
      args as ContractArgs,
      { context: `Call ${contractName}.${methodName} with Pusharoo` }
    );
  }

  private buildReleaseInvocation(
    network: NetworkType,
    operation: 'deploy' | 'update',
    nefHex: string,
    manifestJson: string,
    contractHash: string | undefined,
    initializationData: DeploymentDataValue,
    provider: ConnectableWalletProvider,
    byteArrayEncoding: 'provider' | 'hex' = 'provider'
  ): BuiltInvocation {
    const nefValue = byteArrayEncoding === 'hex'
      ? this.cleanHex(nefHex)
      : this.encodeByteArrayForProvider(nefHex, provider);
    const codeAndManifest = [
      { type: 'ByteArray', value: nefValue },
      { type: 'String', value: manifestJson }
    ] as ContractArgs;

    if (operation === 'update') {
      if (!contractHash) throw new Error('A target contract is required to estimate an update.');
      return { scriptHash: contractHash, operation: 'update', args: codeAndManifest };
    }

    const normalizedData = this.deploymentData.normalize(initializationData);
    return {
      scriptHash: this.runtimeConfig.value.wallet.contractManagement[network],
      operation: 'deploy',
      args: [...codeAndManifest, this.toContractArgument(normalizedData, provider, byteArrayEncoding)]
    };
  }

  private deploymentSigner(walletKit: WalletKit, network: PusharooNetwork): Signer {
    return {
      ...walletKit.connectedSigner(64),
      rules: [{
        action: 'Allow',
        condition: {
          type: 'CalledByContract',
          hash: this.runtimeConfig.value.wallet.contractManagement[network]
        }
      }]
    };
  }

  private toContractArgument(
    value: DeploymentDataValue,
    provider: ConnectableWalletProvider,
    byteArrayEncoding: 'provider' | 'hex'
  ): ContractArgs[number] {
    switch (value.type) {
      case 'Any': return { type: 'Any', value: null } as ContractArgs[number];
      case 'String': return { type: 'String', value: value.value } as ContractArgs[number];
      case 'Boolean': return { type: 'Boolean', value: value.value } as ContractArgs[number];
      case 'Integer': return { type: 'Integer', value: value.value } as ContractArgs[number];
      case 'Hash160':
        return { type: 'Hash160', value: `0x${value.value.replace(/^0x/i, '').toLowerCase()}` } as ContractArgs[number];
      case 'ByteArray':
        return {
          type: 'ByteArray',
          value: byteArrayEncoding === 'hex' ? this.cleanHex(value.value) : this.encodeByteArrayForProvider(value.value, provider)
        } as ContractArgs[number];
      case 'Array':
        return {
          type: 'Array',
          value: value.value.map((child) => this.toContractArgument(child, provider, byteArrayEncoding))
        } as ContractArgs[number];
      default:
        throw new Error(`Deployment data type '${(value as { type: string }).type}' is not supported by this wallet.`);
    }
  }

  private encodeByteArrayForProvider(value: string, provider: ConnectableWalletProvider): string {
    const hex = this.cleanHex(value);
    return provider === 'onegate' ? hex : this.hexToBase64(hex);
  }

  private cleanHex(value: string): string {
    return value.trim().replace(/^0x/i, '').toLowerCase();
  }

  private hexToBase64(hex: string): string {
    const cleanHex = hex.trim().replace(/^0x/i, '');
    const bytes: string[] = [];

    for (let index = 0; index < cleanHex.length; index += 2) {
      bytes.push(String.fromCharCode(Number.parseInt(cleanHex.slice(index, index + 2), 16)));
    }

    return btoa(bytes.join(''));
  }

  private formatFee(value: unknown): string | null {
    if (typeof value !== 'string' && typeof value !== 'number') {
      return null;
    }

    const parsed = Number(value);
    return Number.isFinite(parsed) ? `${parsed} GAS` : null;
  }

  private sumFees(systemFee: string, networkFee: string): string {
    const system = Number(systemFee.replace(' GAS', ''));
    const network = Number(networkFee.replace(' GAS', ''));

    return `${system + network} GAS`;
  }

  private async signMessage(
    session: WalletSession,
    accountAddress: string,
    message: string,
    context: string
  ): Promise<SignedMessageResponse> {
    if (session.provider === 'walletconnect') {
      if (!session.methods.includes('signMessage')) {
        throw new Error('Reconnect Neon Wallet so Pusharoo can request message signatures.');
      }

      return this.normalizeSignedMessage(
        await this.walletKit?.wallet.request('signMessage', { message, version: 3 }, context)
      );
    }

    const provider = session.raw;
    if (!this.isMessageSigningProvider(provider)) {
      throw new Error('The connected wallet does not expose message signing.');
    }

    if (session.provider === 'onegate') {
      return this.normalizeSignedMessage(
        await provider.signMessage(message, accountAddress, { withoutSalt: true })
      );
    }

    return this.normalizeSignedMessage(
      await provider.signMessage({ message, version: 3 })
    );
  }

  private toWalletActionSignature(
    account: ConnectedAccount,
    session: WalletSession,
    challenge: WalletActionSignatureChallenge,
    signedMessage: SignedMessageResponse
  ): WalletActionSignature {
    return {
      address: account.address,
      scriptHash: account.scriptHash,
      network: session.network,
      provider: session.provider,
      origin: challenge.origin,
      audience: challenge.audience,
      issuedAtUtc: challenge.issuedAtUtc,
      nonce: challenge.nonce,
      message: challenge.message,
      publicKey: this.requireSignedMessageField(signedMessage.publicKey, 'publicKey'),
      data: this.requireSignedMessageField(signedMessage.data, 'data'),
      salt: this.optionalSignedMessageField(signedMessage.salt),
      messageHex: this.optionalSignedMessageField(signedMessage.messageHex)
    };
  }

  private normalizeSignedMessage(value: unknown): SignedMessageResponse {
    if (!value || typeof value !== 'object') {
      throw new Error('The wallet returned an invalid signature response.');
    }

    return value as SignedMessageResponse;
  }

  private requireSignedMessageField(value: unknown, fieldName: string): string {
    if (typeof value !== 'string' || value.trim().length === 0) {
      throw new Error(`The wallet signature did not include ${fieldName}.`);
    }

    return value;
  }

  private optionalSignedMessageField(value: unknown): string | null {
    return typeof value === 'string' && value.trim().length > 0 ? value : null;
  }

  private isMessageSigningProvider(provider: unknown): provider is MessageSigningProvider {
    return !!provider
      && typeof provider === 'object'
      && 'signMessage' in provider
      && typeof provider.signMessage === 'function';
  }

  private setWalletKit(walletKit: WalletKit): void {
    this.unsubscribeSession?.();
    this.walletKit = walletKit;
    this.unsubscribeSession = walletKit.onSessionChange((session) => {
      this.session.set(session);
      this.account.set(walletKit.account);
      this.status.set(session ? 'connected' : 'idle');

      if (session && this.isConnectableProvider(session.provider)) {
        if (isPusharooNetwork(session.network)) {
          this.selectedNetwork.set(session.network);
          this.saveNetwork(session.network);
        }
        this.selectedProvider.set(session.provider);
        this.saveProvider(session.provider);
      } else if (!session) {
        this.selectedProvider.set(null);
        this.clearSavedProvider();
      }
    });
  }

  private setSession(session: WalletSession): void {
    this.session.set(session);
    this.account.set(this.walletKit?.account ?? null);
    this.status.set('connected');

    if (this.isConnectableProvider(session.provider)) {
      if (isPusharooNetwork(session.network)) {
        this.selectedNetwork.set(session.network);
        this.saveNetwork(session.network);
      }
      this.selectedProvider.set(session.provider);
      this.saveProvider(session.provider);
    }
  }

  private resetWalletState(): void {
    this.unsubscribeSession?.();
    this.unsubscribeSession = null;
    this.walletKit = null;
    this.account.set(null);
    this.session.set(null);
    this.walletConnectUri.set('');
    this.walletConnectQrCode.set('');
    this.status.set('idle');
    this.selectedProvider.set(null);
  }

  private async initWalletKit(
    provider: ConnectableWalletProvider,
    network: PusharooNetwork
  ): Promise<WalletKit> {
    if (provider === 'neoline') {
      return await WalletKit.initNeoLine({ network });
    }

    if (provider === 'onegate') {
      return await WalletKit.initOneGate({ network });
    }

    const walletConnectMethods: Method[] = ['invokeFunction', 'testInvoke', 'calculateFee', 'signMessage'];

    return await WalletKit.init({
      projectId: this.getWalletConnectProjectId(),
      relayUrl: 'wss://relay.walletconnect.com',
      metadata: {
        name: 'Pusharoo',
        description: 'Neo smart contract artifact workspace',
        url: window.location.origin,
        icons: [`${window.location.origin}/pusharoo-logo.png`]
      },
      network,
      methods: walletConnectMethods
    });
  }

  private getSavedProvider(): ConnectableWalletProvider | null {
    try {
      const provider = localStorage.getItem(this.providerStorageKey);

      return this.isConnectableProvider(provider) ? provider : null;
    } catch {
      return null;
    }
  }

  private saveProvider(provider: ConnectableWalletProvider): void {
    try {
      localStorage.setItem(this.providerStorageKey, provider);
    } catch {
      // Storage can be blocked in private or embedded browser contexts.
    }
  }

  private clearSavedProvider(): void {
    try {
      localStorage.removeItem(this.providerStorageKey);
    } catch {
      // Storage can be blocked in private or embedded browser contexts.
    }
  }

  private getSavedNetwork(): PusharooNetwork {
    try {
      const network = localStorage.getItem(this.networkStorageKey);

      return network && isPusharooNetwork(network) ? network : defaultWalletConfig.network;
    } catch {
      return defaultWalletConfig.network;
    }
  }

  private saveNetwork(network: PusharooNetwork): void {
    try {
      localStorage.setItem(this.networkStorageKey, network);
    } catch {
      // Storage can be blocked in private or embedded browser contexts.
    }
  }

  private isConnectableProvider(provider: unknown): provider is ConnectableWalletProvider {
    return provider === 'neoline' || provider === 'onegate' || provider === 'walletconnect';
  }

  private getErrorMessage(error: unknown, provider: ConnectableWalletProvider): string {
    if (error instanceof Error && error.message) {
      return error.message;
    }

    if (provider === 'neoline') {
      return `Could not connect NeoLine. Make sure the extension is installed, unlocked, and on ${this.selectedNetwork()}.`;
    }

    if (provider === 'onegate') {
      return `Could not connect OneGate. Open Pusharoo inside a OneGate browser with ${this.selectedNetwork()} selected.`;
    }

    return `Could not connect Neon Wallet. Make sure WalletConnect is configured and Neon is on ${this.selectedNetwork()}.`;
  }

  private getWalletConnectProjectId(): string {
    const projectId = this.runtimeConfig.value.wallet.walletConnectProjectId.trim();

    if (!projectId) {
      throw new Error('Add a WalletConnect project ID in frontend/src/app/config/wallet.config.ts to enable Neon Wallet.');
    }

    return projectId;
  }

  private async startWalletConnectApproval(network: PusharooNetwork): Promise<WalletKit> {
    const walletKit = await this.initWalletKit('walletconnect', network);
    const { uri, approval } = await walletKit.createConnection();

    if (!uri) {
      throw new Error('Neon Wallet did not return a WalletConnect URI.');
    }

    this.walletConnectUri.set(uri);
    this.walletConnectQrCode.set(await toDataURL(uri, {
      errorCorrectionLevel: 'M',
      margin: 1,
      width: 220
    }));
    void this.waitForWalletConnectApproval(walletKit, approval);

    return walletKit;
  }

  private async waitForWalletConnectApproval(
    walletKit: WalletKit,
    approval: () => Promise<WalletSession>
  ): Promise<void> {
    try {
      const session = await approval();
      this.walletConnectUri.set('');
      this.walletConnectQrCode.set('');
      this.setWalletKit(walletKit);
      this.setSession(session);
    } catch (error) {
      this.status.set('error');
      this.errorMessage.set(this.getErrorMessage(error, 'walletconnect'));
    }
  }
}
