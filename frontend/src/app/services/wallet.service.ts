import { computed, Injectable, signal } from '@angular/core';
import { WalletKit } from 'neo-n3-walletkit';
import { toDataURL } from 'qrcode';
import type {
  ConnectedAccount,
  ContractArgs,
  Method,
  NetworkType,
  WalletProvider,
  WalletSession
} from 'neo-n3-walletkit';
import { walletConfig } from '../config/wallet.config';
import { ProjectCreationSignature } from '../models/pusharoo.models';
import { ProjectCreationSignatureMessageService } from './project-creation-signature-message.service';

type WalletStatus = 'idle' | 'connecting' | 'connected' | 'error';
type ConnectableWalletProvider = Extract<WalletProvider, 'neoline' | 'onegate' | 'walletconnect'>;
interface ContractCallParameter {
  type: string;
  value: unknown;
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
  private walletKit: WalletKit | null = null;
  private unsubscribeSession: (() => void) | null = null;

  readonly account = signal<ConnectedAccount | null>(null);
  readonly session = signal<WalletSession | null>(null);
  readonly status = signal<WalletStatus>('idle');
  readonly errorMessage = signal('');
  readonly selectedProvider = signal<ConnectableWalletProvider | null>(null);
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

  constructor(
    private readonly projectCreationMessage: ProjectCreationSignatureMessageService
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
      const walletKit = await this.initWalletKit(provider);
      const session = walletKit.isConnected ? walletKit.wallet.session : null;

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
        ? await this.startWalletConnectApproval()
        : await this.initWalletKit(provider);

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

    try {
      await this.walletKit?.disconnect();
    } finally {
      this.unsubscribeSession?.();
      this.unsubscribeSession = null;
      this.walletKit = null;
      this.account.set(null);
      this.session.set(null);
      this.walletConnectUri.set('');
      this.walletConnectQrCode.set('');
      this.status.set('idle');
      this.selectedProvider.set(null);
      this.clearSavedProvider();
    }
  }

  async deployContract(
    network: NetworkType,
    nefHex: string,
    manifestJson: string,
    contractName: string
  ): Promise<string> {
    const session = this.session();
    const walletKit = this.walletKit;

    if (!walletKit || !session) {
      throw new Error('Connect a wallet before deploying.');
    }

    if (session.network !== network) {
      throw new Error(`Connected wallet is on ${session.network}. Select ${session.network} or reconnect on ${network}.`);
    }

    const contractManagementHash = walletConfig.contractManagement[network];
    const contractManagement = walletKit.contract(contractManagementHash);
    const nefValue = session.provider === 'onegate'
      ? nefHex
      : this.hexToBase64(nefHex);
    const args: ContractArgs = [
      { type: 'ByteArray', value: nefValue },
      { type: 'String', value: manifestJson },
      { type: 'Any', value: null }
    ];

    return await contractManagement.invoke(
      'deploy',
      args,
      { context: `Deploy ${contractName} with Pusharoo` }
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

  async signProjectCreation(
    projectName: string,
    projectDescription: string
  ): Promise<ProjectCreationSignature> {
    const session = this.session();
    const account = this.account();

    if (!this.walletKit || !session || !account) {
      throw new Error('Connect a wallet before creating a project.');
    }

    const challenge = await this.projectCreationMessage.create(
      projectName,
      projectDescription,
      account,
      session
    );
    const signedMessage = await this.signMessage(
      session,
      account.address,
      challenge.message,
      projectName
    );

    return {
      address: account.address,
      scriptHash: account.scriptHash,
      network: session.network,
      provider: session.provider,
      origin: challenge.origin,
      issuedAtUtc: challenge.issuedAtUtc,
      nonce: challenge.nonce,
      message: challenge.message,
      publicKey: this.requireSignedMessageField(signedMessage.publicKey, 'publicKey'),
      data: this.requireSignedMessageField(signedMessage.data, 'data'),
      salt: this.optionalSignedMessageField(signedMessage.salt),
      messageHex: this.optionalSignedMessageField(signedMessage.messageHex)
    };
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

  private hexToBase64(hex: string): string {
    const cleanHex = hex.trim().replace(/^0x/i, '');
    const bytes: string[] = [];

    for (let index = 0; index < cleanHex.length; index += 2) {
      bytes.push(String.fromCharCode(Number.parseInt(cleanHex.slice(index, index + 2), 16)));
    }

    return btoa(bytes.join(''));
  }

  private async signMessage(
    session: WalletSession,
    accountAddress: string,
    message: string,
    projectName: string
  ): Promise<SignedMessageResponse> {
    const context = `Create Pusharoo project ${projectName.trim()}`;

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

  private async initWalletKit(provider: ConnectableWalletProvider): Promise<WalletKit> {
    if (provider === 'neoline') {
      return await WalletKit.initNeoLine({ network: walletConfig.network });
    }

    if (provider === 'onegate') {
      return await WalletKit.initOneGate({ network: walletConfig.network });
    }

    const walletConnectMethods: Method[] = ['invokeFunction', 'testInvoke', 'signMessage'];

    return await WalletKit.init({
      projectId: this.getWalletConnectProjectId(),
      relayUrl: 'wss://relay.walletconnect.com',
      metadata: {
        name: 'Pusharoo',
        description: 'Neo smart contract artifact workspace',
        url: window.location.origin,
        icons: [`${window.location.origin}/pusharoo-logo.png`]
      },
      network: walletConfig.network,
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

  private isConnectableProvider(provider: unknown): provider is ConnectableWalletProvider {
    return provider === 'neoline' || provider === 'onegate' || provider === 'walletconnect';
  }

  private getErrorMessage(error: unknown, provider: ConnectableWalletProvider): string {
    if (error instanceof Error && error.message) {
      return error.message;
    }

    if (provider === 'neoline') {
      return 'Could not connect NeoLine. Make sure the extension is installed, unlocked, and on Neo N3 testnet.';
    }

    if (provider === 'onegate') {
      return 'Could not connect OneGate. Open Pusharoo inside a OneGate browser with Neo N3 testnet selected.';
    }

    return 'Could not connect Neon Wallet. Make sure WalletConnect is configured and Neon is on Neo N3 testnet.';
  }

  private getWalletConnectProjectId(): string {
    const projectId = walletConfig.walletConnectProjectId.trim();

    if (!projectId) {
      throw new Error('Add a WalletConnect project ID in frontend/src/app/config/wallet.config.ts to enable Neon Wallet.');
    }

    return projectId;
  }

  private async startWalletConnectApproval(): Promise<WalletKit> {
    const walletKit = await this.initWalletKit('walletconnect');
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
